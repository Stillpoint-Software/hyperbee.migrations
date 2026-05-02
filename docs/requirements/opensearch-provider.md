# OpenSearch Provider for Hyperbee.Migrations

**Status:** Draft (revised after assessment)
**Date:** 2026-05-02
**Research:** [docs/research/0001-opensearch-provider.md](../research/0001-opensearch-provider.md)
**Assessment:** [docs/research/0002-opensearch-provider-assessment.md](../research/0002-opensearch-provider-assessment.md)
**Existing ADRs constraining the design:** ADR-0001 through ADR-0010

## Problem

Hyperbee.Migrations ships providers for Aerospike, Couchbase, MongoDB, and Postgres but has no OpenSearch provider. Teams that use OpenSearch for search, log analytics, or vector workloads have no first-class migration story in the .NET ecosystem — the only viable options are JVM tools (elasticsearch-evolution, hubrick), the Liquibase OpenSearch extension (single `httpRequest` change type, gives up on abstraction), or hand-rolled imperative scripts. The result is undocumented schema drift, unsafe ad-hoc reindexes, and no shared lock against concurrent CI runners.

A native provider closes the gap and lets the same teams that use Hyperbee.Migrations for Postgres/Couchbase use it for OpenSearch with consistent ergonomics: versioned migrations, distributed locks, JSON resource files, and a thin DSL over native APIs.

## Requirements

### Lifecycle & Warmup

#### R-01: Provider implements the standard IMigrationRecordStore contract

**Actor:** Hyperbee.Migrations runtime — invoked by application startup
**Intention:**
- *Immediate:* OpenSearch provider plugs into existing `MigrationRunner` without core changes
- *Outcome:* Consumers compose providers identically across databases
- *Metric:* `MigrationRunner` has zero OpenSearch-specific code paths

**Friction today:**
- Current: No provider exists; teams either skip migrations or hand-roll one-off scripts
- Failure mode: Schema drift across environments; nothing tracks what's been applied
- Frequency: Every team adopting OpenSearch hits this on first deploy

**Given:** A consumer registers `services.AddOpenSearchMigrations(...)`
**When:** `MigrationRunner.RunAsync` is invoked
**Then:** The runner discovers, locks, applies, and journals migrations using only the existing core contract; provider supplies an `IMigrationRecordStore` implementation
**Otherwise:** Any deviation from the contract is a defect, not an extension point

**Priority:** Must — this is the contract gate
**Confidence:** High (ADR-0003 fixes the contract)

#### R-02: Cluster bootstrapper waits for cluster readiness before any migration runs

**Actor:** Provider startup path — once per `MigrationRunner.RunAsync` invocation
**Intention:**
- *Immediate:* Migrations don't fail on transient cluster unavailability during deploy
- *Outcome:* Pod start order doesn't matter; eventually-consistent cluster startups still succeed
- *Metric:* Zero "cluster_not_ready"-class failures on healthy clusters

**Friction today:**
- Current: Couchbase provider already solves this with a 7-state bootstrapper; OpenSearch needs equivalent
- Failure mode: Deploys race the cluster's startup and fail intermittently
- Frequency: Every cold-start deploy and every CI run with a fresh container

**Given:** Provider has just been initialized; cluster reachability is unknown
**When:** `InitializeAsync` runs before any migration is applied
**Then:** Provider polls `GET /_cluster/health?wait_for_status=<configured>&timeout=<configured>` with bounded retries until ready, OR fails with a clear `OpenSearchNotReadyException` after the configured global timeout
**Otherwise:** A clear distinction is logged between "cluster unreachable" (network) and "cluster reachable but unhealthy" (status red / pending tasks)

**Depends on:** R-03
**Priority:** Must
**Confidence:** High

#### R-03: Cluster health threshold is per-environment configurable

**Actor:** Operator wiring up the provider for a given environment
**Intention:**
- *Immediate:* Single-node dev clusters and multi-node prod clusters both work without code changes
- *Outcome:* Same migration code runs in unit tests, dev, staging, and prod
- *Metric:* No environment-specific forks of the migration runner config

**Friction today:**
- Current: Tools that hardcode green never run on single-node dev (replicas have nowhere to go)
- Failure mode: Hardcoded threshold blocks dev or weakens prod
- Frequency: Every multi-environment rollout

**Given:** Provider options expose a `ClusterHealthThreshold` property accepting `Yellow` or `Green`
**When:** Bootstrapper or implicit waits run
**Then:** They wait for the configured threshold (SDK default `Yellow` so dev/CI single-node clusters work out of the box; production deployments call `WithProductionDefaults()` per R-29 to flip to `Green`)
**Otherwise:** Setting an unrecognized value throws at options-binding time, not runtime; resolved value is logged at INFO via the startup banner (R-25)

**Depends on:** R-29
**Priority:** Must
**Confidence:** High

### Distributed Locking

#### R-04: Lock acquired via optimistic concurrency on a singleton lock document

**Actor:** Provider — once per migration run, before any migration applies
**Intention:**
- *Immediate:* Concurrent CI/deploy runners cannot overlap migrations
- *Outcome:* Deterministic single-writer semantics on schema operations
- *Metric:* Zero observed concurrent migration runs in production

**Friction today:**
- Current: OpenSearch has no native lock primitive; no .NET library implements one
- Failure mode: Without a lock, two pods racing to apply the same migration produces partial state
- Frequency: Every deploy with replicas > 1; every CI matrix run

**Given:** Two runners attempt `CreateLockAsync` simultaneously
**When:** Both read the lock doc, attempt to write with `if_seq_no`/`if_primary_term`
**Then:** Exactly one succeeds; the loser receives a 409 `version_conflict_engine_exception` and surfaces `MigrationLockUnavailableException`. The lock index is created (or asserted) with `number_of_replicas: 0` to eliminate replica-write coupling on the lock primary shard (PA-2 mitigation)
**Otherwise:** Loser does not retry implicitly; caller decides

**Depends on:** R-06
**Priority:** Must
**Confidence:** High (ADR-0005 — provider-native locking; pattern ports from Aerospike)

#### R-05: Lock auto-renews via background heartbeat with bounded lifetime, validated parameters, realtime takeover, and explicit cancellation

**Actor:** Provider lock handle — runs for the duration of `MigrationRunner.RunAsync`
**Intention:**
- *Immediate:* Long-running migrations don't lose their lock and get crashed by takeover; misconfigured lock parameters fail loudly at startup
- *Outcome:* Crashed runners' stale locks are reclaimable by the next runner; refresh-lag does not cause false takeovers
- *Metric:* Zero false-takeovers during active migrations; zero permanent lock-out from crashed runners; zero "ledger written but lock was lost" silent corruptions

**Friction today:**
- Current: Aerospike provider just shipped this exact pattern; OpenSearch needs equivalent — but OpenSearch has refresh-interval visibility lag that Aerospike does not
- Failure mode: Without renewal, a long migration loses its lock; without bounded lifetime, a crashed runner blocks indefinitely; without realtime takeover, search-staleness causes false takeover; without an explicit cancellation contract, max-lifetime can be hit while the runner blindly continues
- Frequency: Reindexes and policy rollouts can take minutes-to-hours; crashes happen

**Given:** A lock has been acquired with `Acquired_At` and `Last_Heartbeat` timestamps
**When:** The lock handle's heartbeat timer fires every `LockRenewInterval` (default 30s)
**Then:**
1. Heartbeat updates `Last_Heartbeat` via CAS (`if_seq_no`/`if_primary_term`)
2. Takeover candidates that observe staleness MUST use `GET /{lockIndex}/_doc/{id}?realtime=true` (not search) to verify the lock document's actual write recency, eliminating refresh-lag false positives
3. Reaching `LockMaxLifetime` triggers an explicit cancellation contract: the in-flight migration's `CancellationToken` is cancelled, current statement aborts, ledger write for the in-progress migration is skipped, and `MigrationLockExpiredException` is surfaced — the runner does NOT silently continue
4. Options are validated at startup: `LockRenewInterval < LockStaleAfter < LockMaxLifetime` AND `LockStaleAfter ≥ 2 * LockRenewInterval`; violations throw `OptionsValidationException` with the offending pair and the recommended adjustment

**Otherwise:** A would-be acquirer that finds `Last_Heartbeat` older than `LockStaleAfter` (default 60s = 2x renew interval) AND confirms staleness via realtime GET overwrites the lock via CAS

**Depends on:** R-04
**Priority:** Must
**Confidence:** High (direct port of Aerospike `LockHandle` with OpenSearch-specific realtime/cancellation additions)

**Notes:**
- Convenience presets `LockTuning.Default` / `LockTuning.LongRunningReindex` / `LockTuning.FastCi` are documented in code comments and samples (R-27), not as requirements; setting one parameter explicitly without the others uses the preset's coherent values, not framework defaults

### Ledger Storage

#### R-06: Migration ledger stored in a strict-mapped OpenSearch index

**Actor:** Provider — read on startup, written after each migration
**Intention:**
- *Immediate:* Authoritative record of what's been applied lives in OpenSearch itself
- *Outcome:* No external dependency for migration state; backups include migration state
- *Metric:* Ledger and data live in the same cluster snapshot

**Friction today:**
- Current: Tools like elastic-migrations (PHP) split ledger into a separate DB — operationally awkward
- Failure mode: External-DB ledger introduces a second system that must be backed up coherently with OpenSearch
- Frequency: Every backup/restore exercise

**Given:** Provider initializes for the first time
**When:** `InitializeAsync` runs
**Then:** Provider creates an index (default name `.migrations`, configurable) with `dynamic: strict` mapping containing typed fields:
- `id` (keyword) — migration record id (per ADR-0009 convention)
- `runOn` (date) — UTC timestamp
- `direction` (keyword) — `Up` | `Down`
- `status` (keyword) — `succeeded` | `failed` | `partially_rolled_back`
- `appliedBy` (keyword) — runner identity: `{machineName}/{processId}[/{RunnerId}]` for postmortem forensics
- `checksum` (keyword) — content hash of statements + body
- `error` (text) — exception details on failure
- `failedStatementIndex` (integer, nullable) — when `partially_rolled_back`, the index of the rollback statement that failed

Creation is idempotent. Strict mapping is **immutable per the Forbidden trust boundary** — schema changes are not supported in v1; field additions must land before release.

**Otherwise:** If the index exists with an incompatible mapping (missing required fields), fail at startup with a clear remediation message naming the missing fields

**Priority:** Must
**Confidence:** High

#### R-07: Ledger writes use optimistic concurrency with refresh-wait

**Actor:** Provider — once per migration applied
**Intention:**
- *Immediate:* Concurrent runners can't double-apply the same migration even if R-04 lock fails
- *Outcome:* Defense in depth against split-brain
- *Metric:* Re-running a journaled migration is a no-op (returns from `ExistsAsync`)

**Given:** A migration has just completed `UpAsync` successfully
**When:** Provider calls `WriteAsync(record)`
**Then:** Write uses `if_seq_no`/`if_primary_term` and `?refresh=wait_for`; subsequent `ExistsAsync` returns true without delay
**Otherwise:** A 409 indicates concurrent writer; surface as a typed exception so the caller can bail out cleanly

**Depends on:** R-06
**Priority:** Must
**Confidence:** High

**Performance budget:** R-24c includes a measured-cost test asserting "100-migration bootstrap completes in < N seconds" (N to be determined empirically against a 3-node Testcontainers cluster). If the budget is exceeded, the alternative is `?refresh=true` for ledger writes (the ledger is a hot single-doc index where the cost of forced refresh is bounded). Removing the refresh wait is **not** an alternative — `ExistsAsync` read-after-write would be unreliable.

### Statement Grammar & Resources

#### R-08: Statement grammar is a thin Parlot verb prefix over opaque JSON

**Actor:** Migration author — writing JSON resource files
**Intention:**
- *Immediate:* Author writes one statement per logical operation in a familiar Couchbase-provider style
- *Outcome:* Migrations are reviewable in PRs without understanding a custom format
- *Metric:* New authors are productive within an hour of seeing a sample

**Friction today:**
- Current: Existing Couchbase, Aerospike, MongoDB providers use Parlot grammars over JSON resource files; OpenSearch should match the house style
- Failure mode: Inventing a new file format fragments author muscle memory
- Frequency: Every new migration

**Given:** A migration ships a `statements.json` resource alongside its class
**When:** The provider runs the migration
**Then:** Each entry in `statements[]` is parsed by Parlot recognizing the verb set in R-09; verb prefix is matched, remainder of payload is opaque JSON passed through to OpenSearch
**Otherwise:** Parser failures include the file name, statement index, and the recognized verb-so-far in the error message

**Priority:** Must
**Confidence:** High (ADR-0001, ADR-0002)

**Parser choice is non-negotiable.** Parlot is the house standard across all Hyperbee.Migrations providers per ADR-0001 — no alternative parser (regex, ANTLR, Sprache/Pidgin, hand-rolled state machine) is acceptable for this provider or any future grammar work. Future verb additions extend the Parlot grammar; they do not introduce a second parsing path.

#### R-08a: Verb set covers index/mapping/settings/template/alias/policy/reindex/refresh/wait

**Given:** R-08 grammar is in place
**When:** A migration uses any of the v1 verb set
**Then:** Each verb compiles to the corresponding OpenSearch REST call:
- `CREATE INDEX <name> [IF NOT EXISTS] [WITH BODY $body]`
- `DROP INDEX <name> [IF EXISTS]`
- `UPDATE MAPPING ON <idx> WITH BODY $body`
- `UPDATE SETTINGS ON <idx> [CLOSE] WITH BODY $body`
- `CREATE TEMPLATE <name> WITH BODY $body`
- `CREATE COMPONENT <name> WITH BODY $body`
- `ALIAS SWAP <a> FROM <old> TO <new>` / `ALIAS ADD <a> ON <idx>` / `ALIAS REMOVE <a> ON <idx>`
- `CREATE POLICY <id> WITH BODY $body` / `APPLY POLICY <id> TO <pattern>`
- `REINDEX FROM <src> TO <dst> [WITH BODY $body] [WAIT FOR COMPLETION true|false]` — **provider auto-injects `op_type: create` into the request body by default** (parser-level safe-default; closes PM-3). Authors who explicitly want re-write semantics opt out with `REINDEX UNSAFE FROM <src> TO <dst> ...` (justification required per R-18)
- `MIGRATE INDEX <old> TO <new> [WITH TEMPLATE <template-id> | WITH BODY $body] [VIA ALIAS <alias>]` — composite verb encoding the canonical zero-downtime reindex-and-swap pattern (see R-30)
- `REFRESH <name>`
- `WAIT FOR <green|yellow> [ON <idx>] [TIMEOUT <duration>]` — `WAIT FOR YELLOW` is the documented "not red" idiom; no separate `WAIT FOR not red` verb in v1
- `WAIT UNTIL TASK <id> COMPLETE [TIMEOUT <duration>]`

**Depends on:** R-08
**Priority:** Must
**Confidence:** High (verb set derived from research §2.2 / §3.4)

**Safe-default principle:** Where the lazy-path call would produce silently incorrect behavior, the parser injects the safe default at compile time — same precedent as R-17's `dynamic: strict` injection. R-24c integration test asserts `op_type: create` is on the wire by default for `REINDEX`.

#### R-09: JSON bodies are sibling object references, not embedded strings

**Actor:** Migration author
**Intention:**
- *Immediate:* Mappings/settings/policies are real JSON objects in the resource file, not escaped strings
- *Outcome:* IDE JSON tooling validates payloads; no quote-escaping bugs
- *Metric:* Zero migrations fail in production due to JSON-string escaping errors

**Given:** A statement uses `WITH BODY $name`
**When:** Provider executes the statement
**Then:** Provider resolves `$name` against sibling properties on the same statement object; the resolved value is sent verbatim as the request body
**Otherwise:** Missing or undefined `$name` reference fails at parse time with file/index/name in the error

**Examples:**
```json
{
  "statement": "CREATE INDEX `users-v2` WITH BODY $usersIndex",
  "usersIndex": { "settings": { "number_of_shards": 2 }, "mappings": { "properties": { ... } } }
}
```

**Namespace policy** (closes MD-3 at parser level, not docs):
- `$<name>` references in statement strings (Parlot-resolved) MUST resolve against sibling JSON properties on the same statement object — no other resolution path
- `{{<scope>.<name>}}` references in any string (templating-resolved) MUST resolve against R-10 scopes — no other resolution path
- Reserved `$<name>` identifiers are checked at parse time: `$body`, `$query`, `$script` are reserved keywords; sibling properties using these names without a corresponding verb consumer fail at parse
- Reserved templating scope names (`env`, `config`, `runtime`, `secrets`) cannot be used as `$name` body references (parse-time error names the conflict)

**Depends on:** R-08
**Priority:** Must
**Confidence:** High

### Templating

#### R-10: Hyperbee.Templating renders resources before parse

**Actor:** Migration author and operator
**Intention:**
- *Immediate:* Index names, replica counts, analyzers vary across environments without forking files
- *Outcome:* Same migration runs in dev/staging/prod
- *Metric:* Zero env-specific forks of `statements.json`

**Friction today:**
- Current: No provider currently uses Hyperbee.Templating; OpenSearch is the first
- Failure mode: Without templating, every new env needs a fork or post-processing step
- Frequency: Every multi-environment rollout

**Given:** A `statements.json` contains `{{config.indexPrefix}}`, `{{env.NODE_ENV}}`, `{{runtime.version}}`, or `{{secrets.snapshotKey}}` references
**When:** Provider loads the resource
**Then:**
1. Hyperbee.Templating renders the entire file with four scopes (`env`, `config`, `runtime`, `secrets`) BEFORE Parlot parsing
2. Values rendered from the `secrets` scope are wrapped in a `SecretMarker` (opaque struct carrying the value + an interned content hash). The marker survives templating output and is replaced with the literal value at the *last* moment before HTTP dispatch
3. All log sinks and exception messages route through a `SecretScrubber` (R-25) that replaces any byte sequence matching a known secret content-hash with `***REDACTED***` — value-coupled, not name-coupled. A secret accidentally pasted into the `config` scope by an operator (MD-15) is still scrubbed at log time

**Otherwise:** Unresolved variables fail at render time with the variable name and resource path; render-time errors include the line and column of the source template, not the post-render JSON

**Depends on:** R-08
**Priority:** Must
**Confidence:** Medium (engine choice is decided; the four-scope wiring is new and not yet validated against Hyperbee.Templating's API surface)

### Async & Wait Semantics

#### R-11: Long-running operations use the Tasks API with polling

**Actor:** Provider — automatic for `REINDEX`, snapshot, restore, force-merge
**Intention:**
- *Immediate:* Reindexes longer than 30s don't time out at the HTTP layer
- *Outcome:* Migrations of any duration succeed; progress is visible in logs
- *Metric:* Successful reindex of an index with 10M+ docs without operator intervention

**Given:** A statement triggers an operation that supports `wait_for_completion=false`
**When:** Provider sends the request
**Then:** Request includes `?wait_for_completion=false`; provider polls `GET /_tasks/{task_id}` with exponential backoff (start 500ms, cap 30s) until `completed: true`, then surfaces `response.error` if non-null; intermediate `status.created`/`status.total` is logged at **DEBUG** every poll, with INFO emitted only on percentage-progress thresholds (10%, 25%, 50%, 75%, 90%) or backoff-state transitions
**Otherwise:** Task cancellation via `CancellationToken` calls `POST /_tasks/{id}/_cancel` and waits for confirmation before returning

**Depends on:** R-08a
**Priority:** Must
**Confidence:** High

#### R-12: Implicit cluster-health wait follows mutating structural operations, scoped and mode-controlled

**Actor:** Provider — automatic after mutating statements per `WaitMode`
**Intention:**
- *Immediate:* Authors don't have to remember to add `WAIT FOR YELLOW` after every `CREATE INDEX`, but production deployments don't suffer N+1 health-check storms
- *Outcome:* Migrations are robust by default; cluster master is not flooded by per-statement waits at scale
- *Metric:* No "index_not_found_exception" failures on subsequent statements within the same migration; no observable master-task-queue pressure from health checks even at 1000-statement runs

**Given:** Provider options expose a `WaitMode` enum: `PerStatement` (current behavior; SDK default), `PerMigration` (one wait at migration end gating all dirty indices touched; default in production via R-29), `Off` (only R-13 explicit waits run). A statement of type `CREATE INDEX`, `REINDEX`, `ALIAS SWAP`, `UPDATE SETTINGS`, or `APPLY POLICY` completes
**When:** Provider moves to the next statement (PerStatement) or finishes the migration (PerMigration)
**Then:**
1. Implicit waits scope to the mutated index by default: `GET /_cluster/health/<idx>?wait_for_status=<R-03 threshold>&timeout=<configurable, default 30s>` — a permanently-yellow unrelated index (e.g., `.opendistro_security` with unallocated replicas) does NOT stall waits scoped to other indices (closes NF-3)
2. Cluster-wide health waits are only invoked via explicit `WAIT FOR <green|yellow>` (no `ON <idx>`) per R-13
3. Under `PerMigration`, the provider tracks "dirty indices" touched during the migration and issues one consolidated health check at migration end — health is checked per-index in parallel, results aggregated

**Otherwise:** Implicit wait can be skipped per-statement with `NO WAIT("<justification>")` modifier — bare `NO WAIT` fails at parse time. Justification token requires a non-empty reason string; structured WARN log `migration.no_wait{reason, statementIdx, migrationId}` emitted on every use. Under `PerMigration` mode, per-statement `NO WAIT` is parsed but no-op (logged at DEBUG)

**Depends on:** R-03, R-08a, R-29
**Priority:** Must
**Confidence:** High (resolves prior Open Question on `NO WAIT` escape syntax; replaces previous Medium-confidence per-statement design)

#### R-13: Explicit `WAIT FOR ...` verbs are first-class statements

**Given:** R-12 is in place
**When:** An author writes `WAIT FOR GREEN ON users-v2 TIMEOUT 60s` or `WAIT UNTIL TASK <id> COMPLETE TIMEOUT 5m`
**Then:** The verb runs as a standalone statement (no associated mutation), with the same wait/poll semantics
**Otherwise:** Timeout exceeded surfaces a typed exception with the operation context

**Depends on:** R-08a
**Priority:** Must
**Confidence:** High

### Idempotency & Safety

#### R-14: Idempotency markers (`IF [NOT] EXISTS`) check live cluster state

**Given:** A statement carries `IF NOT EXISTS` (create) or `IF EXISTS` (drop)
**When:** Provider executes the statement
**Then:** Provider checks the live cluster state (e.g., `HEAD /{idx}`) before issuing the mutating request; non-matching state results in a no-op with INFO log
**Otherwise:** Race conditions between check and mutate produce a clean error, not a silent failure

**Depends on:** R-08a
**Priority:** Must
**Confidence:** High

#### R-15: Conditional execution via `WHEN VERSION` and contexts

**Given:**
- A statement carries `WHEN VERSION <op> '<version>'` (e.g., `WHEN VERSION > '2.10'`)
- The wrapper carries `context: ["prod", "staging"]`
- Provider options expose `ActiveContext` (string, comma-separated tags), bindable from `IConfiguration` key `Migrations:ActiveContext`
- Provider options expose `ContextResolutionPolicy` enum: `RequireExplicit` (any migration with a `context:` block requires `ActiveContext` to be non-null; null = `MissingActiveContextException` at startup) and `SkipIfUnset` (SDK default). Production deployments call `WithProductionDefaults()` (R-29) which forces `RequireExplicit`. `RunIfUnset` is **not exposed** — silent prod-everywhere behavior is forbidden

**When:** Provider evaluates the statement
**Then:** Statement is skipped (with INFO log) if the active runtime context isn't in the wrapper's list, or if the version comparison evaluates false
**Otherwise:** Unparseable version or context expression fails at parse time. Missing `ActiveContext` under `RequireExplicit` policy fails at startup with the exact configuration key to set

**Depends on:** R-15a, R-29
**Priority:** Must (was Should — promoted because MD-1 was Critical)
**Confidence:** High (resolves prior Open Question on context source-of-truth)

#### R-15a: `WHEN VERSION` uses semantic version comparison

**Actor:** Migration author writing version-conditional statements
**Intention:**
- *Immediate:* `'2.9' < '2.10'` evaluates correctly (it does NOT under string comparison)
- *Outcome:* Version-gated migrations behave consistently across normal OpenSearch 2.x version bumps
- *Metric:* Integration test asserts `'2.9' < '2.10'`, `'2.10.0' = '2.10'`, `'2.11.0-SNAPSHOT' > '2.11.0-rc1'`

**Friction today:**
- Current: A naive string comparator returns `'2.9' > '2.10'` (lexically TRUE), flipping a guarded statement from skipped to executed on a normal point release
- Failure mode: Silent wrong-execution on cluster version bumps
- Frequency: Every consumer running `WHEN VERSION` against a 2.x → 2.10+ cluster

**Given:** A statement carries `WHEN VERSION <op> '<version>'` where `<op>` is one of `=`, `!=`, `<`, `<=`, `>`, `>=`
**When:** Provider parses the statement
**Then:** Provider parses `<version>` to `System.Version` (or equivalent SemVer type) at parse time; cluster version reported by `GET /` is normalized to the same type. Suffix handling: known suffixes (`-SNAPSHOT`, `-rc<N>`, AWS `OpenSearch_<version>` prefix) are normalized via documented rules; unrecognized suffixes are rejected at parse time with a remediation pointing to the canonical forms
**Otherwise:** Unparseable version literal fails at parse time with the file/index and the canonical forms in the error message

**Depends on:** R-15
**Priority:** Must (correctness)
**Confidence:** High (parse-time validation closes the entire silent-mismatch class)

#### R-16: `ALIAS SWAP` compiles to one atomic `_aliases` request body with in-body precondition

**Given:** A statement `ALIAS SWAP <alias> FROM <old> TO <new>`
**When:** Provider executes the statement
**Then:** Provider issues a single `POST /_aliases` with both `remove` and `add` actions in one body — atomic on the cluster master; never two separate requests. The precondition (`<alias>` currently points at `<old>`) is expressed **inside the same atomic body** — the `remove` action targets `<old>` so the cluster rejects the entire body atomically if `<old>` is not the current target
**Otherwise:** No separate precondition GET — TOCTOU windows are eliminated by relying on the cluster's atomic rejection of the multi-action body when the precondition fails. Failure surfaces as `AliasSwapPreconditionFailedException` with the actual current target named in the message

**Depends on:** R-08a
**Priority:** Must — this is the headline value-add for zero-downtime patterns
**Confidence:** High (closes NF-2 TOCTOU)

#### R-17: Component-template-aware `dynamic: strict` injection on flat `CREATE INDEX` bodies only

**Given:** A `CREATE INDEX` statement omits an explicit `dynamic` setting in the body AND the body does NOT include a `composed_of` clause (component-template composition)
**When:** Provider sends the create request
**Then:** Provider injects `"mappings": { "dynamic": "strict" }` into the body (preserving existing properties)
**Otherwise:**
- If the body contains `composed_of`, injection is **skipped** — component templates layer mappings differently and silent injection at index-create time can clobber a component's `dynamic: false` (closes PM-4)
- If `dynamic` is explicitly set in the body (`true`, `runtime`, etc.), the author's value is preserved and a structured INFO log emits `migration.dynamic_strict_skipped{reason: "explicit_value", value: "true"}` so the author can verify their value won (closes MD-9)
- A `CREATE INDEX` body using `composed_of` should set `dynamic: strict` at the component-template level (`CREATE COMPONENT`) — sample R-27 demonstrates the pattern

**Priority:** Must — eliminates the most common silent-failure migration bug (mapping explosion)
**Confidence:** High (component-template detection is syntactic — `composed_of` key presence)

#### R-18: Parse-time syntactic detection of unsafe operations + UNSAFE justification token

**Given:** A statement attempts a known-unsafe operation. Syntactic enumeration covers: `DELETE INDEX` without `IF EXISTS`, `_delete_by_query`, mapping field type change in `UPDATE MAPPING` body, mapping field removal in `UPDATE MAPPING` body, static settings update without `CLOSE` flag, `REINDEX` without `op_type: create` (covered by R-08a auto-injection), `_close` without explicit pairing
**When:** Provider parses the statement (before execution)
**Then:** Parse fails with a remediation hint pointing to the safe alternative (reindex via alias swap; close-update-open with explicit `CLOSE` flag)
**Otherwise:** Author can override with `UNSAFE("<justification>")` modifier — bare `UNSAFE` fails at parse time. Justification token requires a non-empty reason string. Provider emits structured WARN log `migration.unsafe_bypass{reason, statementIdx, migrationId, operation}` on every bypass. Provider options expose `RequireUnsafeJustification` (SDK default false; `WithProductionDefaults()` flips to true so dev exploration is friction-free but production runs reject bare UNSAFE). The full enumeration of UNSAFE-required operations ships in R-27 samples documentation

**Depends on:** R-08
**Priority:** Must (was Should — promoted because MD-2 visibility was Critical and the justification token closes the laziest-path bypass)
**Confidence:** High (syntactic detection only; semantic detection — actually understanding query effects — is deferred to v1.1)

### Rollback

#### R-19: Optional rollback block per statement, best-effort

**Actor:** Migration author writing reversible operations (alias swaps, ISM policy changes)
**Intention:**
- *Immediate:* Author can attach an inverse statement that runs on `DownAsync`
- *Outcome:* Common reversible operations are reversible; irreversible ones are flagged
- *Metric:* Authors don't try to "undo" mapping changes (which is impossible)

**Given:** A statement object has a `rollback` property containing another statement string
**When:** Migration runs in `Down` direction
**Then:**
1. Each rollback statement is parsed and executed in reverse order
2. **Partial-rollback semantics (closes NF-5):** If rollback statement N fails after statements N+1..M have already rolled back successfully, the ledger entry for the migration is updated to `status: partially_rolled_back` with `failedStatementIndex: N` (per R-06 schema)
3. Subsequent runs refuse to retry the migration in either direction without an explicit `--force-resume` operator override; the failure error lists which statements rolled back and which didn't, plus a remediation pointing to `--force-resume`
4. `--force-resume` is an opt-in CLI flag on the runner project (R-26) that allows the operator to manually drive recovery after they have inspected and reconciled the cluster state

**Otherwise:** Statements without a `rollback` block raise `RollbackNotSupportedException` on Down with the missing-rollback statement index in the message; documentation states this clearly so authors don't expect auto-inverse

**Priority:** Must (was Should — promoted because partial-rollback ledger state is a correctness gap)
**Confidence:** High (semantics now explicit; ledger state is well-defined)

### Bulk Operations

#### R-20: Bulk loads use `BulkAllObservable` with backoff defaults

**Given:** A migration uses the bulk-load helper to seed many documents
**When:** Provider issues bulk requests
**Then:** Defaults are: 5MB batches, exponential backoff on 429 (1s → 2s → 4s, 5 retries), 8x parallelism, `refresh=false`; explicit `_refresh` is invoked once at end
**Otherwise:** All defaults are overridable via options; 429 responses are logged at WARN with batch size and retry count

**Priority:** Should
**Confidence:** High

### Authentication

#### R-21: Auth supports basic, API key, mTLS, and AWS SigV4

**Given:** Provider options include auth configuration
**When:** Provider initializes the OpenSearch client
**Then:**
1. Basic auth, API key, and mTLS are supported via the core package; AWS SigV4 is supported via the optional `OpenSearch.Net.Auth.AwsSigV4` package, registered only when an opt-in extension is called
2. **AWS endpoint loud-fail (closes MD-6, PM-2 partial):** if the configured endpoint matches `*.amazonaws.com` or `*.aoss.amazonaws.com` AND SigV4 has not been registered, provider throws `AwsSigV4NotConfiguredException` at startup with the exact one-line `services.AddAwsSigV4(...)` snippet to add. Inverse mismatch (SigV4 configured against a non-AWS endpoint) emits WARN
3. **AWS ISM endpoint capability detection (closes PM-6):** when the AWS endpoint pattern matches, the provider probes `_plugins/_ism` capability at bootstrap. AWS Managed domains on older versions exposing ISM at `_opendistro/_ism` (or with insufficient `restapi` IAM permissions) fail loudly with the actual endpoint path tried and the IAM action required
4. **Credential resolver lifetime (closes PM-2):** SigV4 signer is wired to an identity resolver that re-resolves credentials per request, not cached at client construction — required for IRSA / instance-profile rotation scenarios

**Otherwise:** Missing required auth credentials fail at startup with a clear error indicating which auth mode was configured

**Priority:** Must (basic + SigV4 + AWS endpoint detection); Should (API key, mTLS)
**Confidence:** High

### DI, Discovery & Conventions

#### R-22: DI extension follows the house pattern

**Given:** Consumer registers `services.AddOpenSearchMigrations(opts => { ... })`
**When:** Service provider builds
**Then:** Provider registers `IMigrationRecordStore`, `MigrationRunner`, options factory, and resource runner with the same lifetimes and binding patterns as Couchbase/Aerospike/MongoDB/Postgres providers; `IConfiguration` sections (`Migrations:FromAssemblies`, `Migrations:FromPaths`) merge with the lambda
**Otherwise:** Misregistration (e.g., calling without an OpenSearchClient configured) fails at startup, not first migration

**Priority:** Must
**Confidence:** High (ADR-0006)

#### R-23: Reflection-based discovery and convention-based record IDs apply unchanged

**Given:** R-22 is in place
**When:** `MigrationRunner.RunAsync` runs
**Then:** Migrations are discovered via reflection per ADR-0004 and IDs generated per ADR-0009 — no provider-specific overrides
**Otherwise:** Custom conventions are still pluggable via `IMigrationConventions`

**Priority:** Must
**Confidence:** High

#### R-29: `WithProductionDefaults()` extension method explicitly configures production-safety defaults

**Actor:** Operator wiring up the provider for a production environment
**Intention:**
- *Immediate:* One discoverable IntelliSense-visible call sets all production-safety defaults coherently
- *Outcome:* No hidden coupling via an environment enum; the call site shows what changed; behavior is auditable in source
- *Metric:* Production deployments call `.WithProductionDefaults()` exactly once, at the DI registration site

**Friction today:**
- Current: First-time-use of an environment enum risks "I set Profile=Production and forgot what that implies"; an extension method shows in IntelliSense and is grep-able in code review
- Failure mode: Without an explicit forcing function, operators inherit dev defaults silently into production (MD-4, PM-7)
- Frequency: Every production deployment

**Given:** Consumer registers
```csharp
services.AddOpenSearchMigrations(opts => { ... }).WithProductionDefaults();
```
**When:** Service provider builds
**Then:** Extension method explicitly sets:
- `ClusterHealthThreshold = Green` (R-03)
- `WaitMode = PerMigration` (R-12)
- `RequireUnsafeJustification = true` (R-18)
- `ContextResolutionPolicy = RequireExplicit` (R-15)

Per-option settings the operator chains AFTER `WithProductionDefaults()` win (the extension does not re-apply defaults). The startup banner (R-25) emits all resolved values at INFO so the operator can verify the configuration in production logs

**Otherwise:** No environment enum exists; "production" is a behavior set the operator opts into, not a profile that silently changes behavior. Calling `WithProductionDefaults()` against a single-node cluster will hit the Green-threshold ceiling — this is the intended trade and is documented

**Depends on:** R-03, R-12, R-15, R-18, R-25
**Priority:** Must
**Confidence:** High (replaces the rejected `EnvironmentProfile` enum design — IR meta-finding)

#### R-30: `MIGRATE INDEX` composite verb encodes the zero-downtime reindex-and-swap pattern

**Actor:** Migration author propagating a template/mapping/settings change to existing data
**Intention:**
- *Immediate:* Authors who need to migrate existing data to a new index shape get one verb that does it correctly — they don't compose four statements and risk a wrong intermediate state
- *Outcome:* The canonical pattern (create new versioned index → reindex with `op_type: create` → atomic alias swap) is encoded as the lazy path; no sample reading required
- *Metric:* Production scenario test (R-24c) demonstrates `MIGRATE INDEX` produces identical end-state to the hand-composed four-statement equivalent

**Friction today:**
- Current: A teammate who runs `CREATE TEMPLATE` thinking it propagates to existing indices gets a silent wrong-state failure (template only matches future indices). The four-statement workaround (`CREATE INDEX new` + `REINDEX` + `ALIAS SWAP` + optional `DROP INDEX old`) requires reading samples and remembering to add `op_type: create`, the alias swap precondition, the right wait modes
- Failure mode: Author writes `UPDATE MAPPING` on an existing index expecting analyzers to apply to existing docs (they don't); or runs `CREATE TEMPLATE` and assumes propagation; or hand-composes a reindex that loses data on retry because they forgot `op_type: create`
- Frequency: Every time a team needs to apply a mapping/settings/template change to a populated index — the common case in mature production deployments

**Given:** A statement of the form `MIGRATE INDEX <old> TO <new> [WITH TEMPLATE <id> | WITH BODY $body] [VIA ALIAS <alias>] [TIMEOUT <duration>]`
**When:** Provider parses and executes the statement
**Then:** Parser decomposes the verb into a deterministic sequence of AST nodes:
1. `CREATE INDEX <new> [IF NOT EXISTS]` — body resolved from either `WITH TEMPLATE <id>` (provider performs `GET /_index_template/<id>` at execute-time and uses the resolved `template` block) OR `WITH BODY $body` (sibling reference per R-09). `dynamic: strict` injection per R-17 applies to the resolved body unless `composed_of` is present
2. `REINDEX FROM <old> TO <new>` with auto-injected `op_type: create` (per R-08a) and `WAIT FOR COMPLETION true` (per R-11 Tasks API polling)
3. If `VIA ALIAS <alias>` is present: `ALIAS SWAP <alias> FROM <old> TO <new>` with in-body precondition (R-16). If absent, no swap is performed — author retains responsibility for cutover (this preserves migrations that intentionally retain both indices, e.g., for read-traffic comparison)

The decomposition is **performed at parse time**, producing the same AST shape as the four-statement hand-composed equivalent. Each sub-statement is subject to all standard middleware (implicit waits per R-12, secret scrubbing per R-10/R-25, observability per R-25). Failure of any sub-statement halts the composite; partial-rollback ledger semantics (R-19) record which sub-statement failed for `--force-resume` recovery.

**Otherwise:**
- `WITH TEMPLATE <id>` referencing a non-existent template fails at **execute time** (parser produces an AST node carrying the template id as an unresolved reference; runtime middleware performs `GET /_index_template/<id>` immediately before the CREATE INDEX is dispatched; missing template surfaces with the index-template name in the error). Per ADR-0015, the parser is offline-pure — no parse-time network I/O
- `MIGRATE INDEX a TO a` (same source and destination) fails at parse time (purely syntactic check)
- The verb does NOT support arbitrary author-provided sub-statements between create/reindex/swap. Authors who need custom intermediate logic (e.g., run a Painless script during reindex) hand-compose using the underlying verbs

**Depends on:** R-08a, R-11, R-16, R-17, R-19
**Priority:** Should — closes the template-propagation lazy-path gap; adopters with mature production data benefit immediately
**Confidence:** High — runtime template resolution preserves offline parse, isolates I/O to middleware boundary (per ADR-0015)


### Testing

#### R-24: Unit tests cover all parser, lock, and compilation logic

**Actor:** CI pipeline
**Intention:**
- *Immediate:* Fast feedback on grammar and lock correctness without Docker
- *Outcome:* Most regressions caught before integration tier
- *Metric:* Unit suite runs in under 10s; covers every verb's parse path and every lock state transition

**Given:** ADR-0010 mandates unit + integration tiers
**When:** Unit tests run
**Then:** Unit tests cover (a) Parlot grammar for every verb in R-08a (positive and negative cases including malformed inputs and ambiguous prefixes), (b) statement compilation to OpenSearch request shapes via mocked `IConnection`, (c) lock CAS state machine including renewal, takeover-on-staleness, max-lifetime expiry, and crash mid-renewal, (d) implicit-wait insertion logic for R-12, (e) Hyperbee.Templating four-scope rendering, (f) `dynamic: strict` injection (R-17), (g) parse-time unsafe-operation detection (R-18 syntactic tier)
**Otherwise:** Each test names the requirement it validates in its DisplayName

**Priority:** Must
**Confidence:** High

#### R-24a: Integration tests cover every verb against a real OpenSearch container

**Actor:** CI pipeline
**Intention:**
- *Immediate:* Verify the provider end-to-end against real OpenSearch behavior, not mocks
- *Outcome:* Confidence that production-representative scenarios actually work
- *Metric:* Every verb in R-08a has at least one happy-path and one negative integration test

**Friction today:**
- Current: Existing `Hyperbee.Migrations.Integration.Tests` project uses Testcontainers for Aerospike — same pattern applies
- Failure mode: Without a real cluster, parser/compiler bugs surface only in production
- Frequency: Every release

**Given:** Docker is available; tests run against a Testcontainers OpenSearch image **pinned by sha256 digest** (e.g., `opensearchproject/opensearch@sha256:...`); image bumps are explicit PR-level decisions, not silent CI-time drift (closes PM-11)
**When:** Integration suite runs
**Then:** Tests verify (a) bootstrapper waits for cluster ready and fails cleanly when not, (b) ledger index is created with strict mapping (including `appliedBy`, `direction`, `failedStatementIndex`) and survives re-init, (c) every verb in R-08a executes its OpenSearch operation correctly (CRUD round-trips assert state via `_cat`/`_search`), (d) atomic `ALIAS SWAP` is single-request and atomic with in-body precondition (R-16 / NF-2), (e) `REINDEX` polls Tasks API, surfaces progress, and asserts `op_type: create` is on the wire by default (R-08a / PM-3), (f) `dynamic: strict` injection is applied for flat bodies and SKIPPED for `composed_of` bodies (R-17 / PM-4), (g) idempotency markers no-op correctly, (h) implicit waits gate subsequent statements per `WaitMode`, (i) WHEN VERSION semver: `'2.9' < '2.10'` (R-15a / PM-9)
**Otherwise:** Integration tests are skipped (not failed) when Docker is unavailable, with a clear `[TestCategory("RequiresDocker")]` exclusion mechanism mirroring the Aerospike pattern

**Depends on:** R-08a, R-24
**Priority:** Must
**Confidence:** High

#### R-24b: Integration tests cover lock contention, crash recovery, and concurrent runners

**Actor:** CI pipeline; this is the production-safety harness
**Intention:**
- *Immediate:* Prove the lock actually prevents concurrent migrations and recovers from crashes
- *Outcome:* No production incident class "two pods migrated at once"; no class "crashed migration locked us out forever"
- *Metric:* Concurrent-runner test runs 50 iterations without false acquisition or false starvation

**Friction today:**
- Current: Aerospike provider just shipped auto-renewing locks; that test pattern transfers
- Failure mode: Without these tests, the lock works in theory but fails under real conditions (clock skew, network blips, OpenSearch slow refresh, etc.)
- Frequency: Every blue/green deploy

**Given:** Two `MigrationRunner` instances share the same cluster and ledger
**When:** Both invoke `RunAsync` simultaneously with conflicting migrations
**Then:** Tests verify (a) only one acquires the lock; the other receives `MigrationLockUnavailableException`, (b) heartbeat renewal extends the lock under sustained workload (>1 renewal interval), (c) abrupt termination of the lock holder allows the next runner to take over after `LockStaleAfter` and not before, (d) `LockMaxLifetime` ceiling stops renewal and surfaces the warning, (e) version conflict on ledger write (R-07) surfaces as a typed exception, (f) lock acquisition CAS handles 409 retry semantics correctly under refresh-interval lag
**Otherwise:** Test uses controllable `TimeProvider` (already wired via DI per the Aerospike pattern) so timing is deterministic, not wall-clock

**Depends on:** R-04, R-05, R-07, R-24a
**Priority:** Must
**Confidence:** High (pattern is proven on Aerospike)

#### R-24c: Integration tests cover production-representative scenarios

**Actor:** CI pipeline; this is the soak harness for "does it really work"
**Intention:**
- *Immediate:* Validate scenarios that bite real teams, not just synthetic happy paths
- *Outcome:* Provider is provably production-capable, not just feature-complete
- *Metric:* Each named production scenario has a passing test

**Given:** Realistic data shapes (10K-100K docs in a seed index)
**When:** Integration suite runs the production-scenario subset
**Then:** Tests verify:
- (a) Zero-downtime alias swap pattern: create v2 → reindex from v1 with active background writes to v1 → atomic alias swap → asserts no docs lost, no docs double-written. Asserts `op_type: create` is auto-injected by R-08a even when the migration body omits it
- (b) ISM policy attachment to existing index works (`POST /_plugins/_ism/add` after policy create)
- (c) Mapping update on existing index produces expected "no reindex" gotcha and the provider's diagnostic warns about it
- (d) Static settings update fails clearly without `CLOSE` flag and succeeds with it
- (e) Reindex of 100K docs streams progress and does not time out at HTTP layer (Tasks API); progress logs at INFO only on percentage thresholds, DEBUG every poll
- (f) Bulk-load with simulated 429 retries via toxiproxy or chaos provider
- (g) `dynamic: strict` rejects unexpected fields with the documented error
- (h) **Lock false-takeover scenario (PM-1, PA-5):** simulated refresh-lag during heartbeat verifies takeover candidate uses realtime GET and does NOT take over a healthy holder
- (i) **Reindex stale-dst scenario (PM-3):** crashed prior run leaves dst with partial docs; new run with `op_type: create` (auto-injected) skips them safely, no double-write
- (j) **LockMaxLifetime cancellation contract (PM-12):** simulated long-running migration that exceeds `LockMaxLifetime` aborts the in-flight statement, skips ledger write, surfaces `MigrationLockExpiredException`
- (k) **Lock primary-shard contention (PA-2):** N concurrent `CreateLockAsync` invocations against the same lock index; assert lock-index settings include `number_of_replicas: 0`; assert tail latency for losers is bounded
- (l) **Templating JSON-context (PM-5):** `{{#if}}`, `{{each}}` rendering inside JSON statement strings; assert rendered JSON is well-formed; assert render-time errors surface line/column of source template
- (m) **Ledger refresh budget (R-07 / PA-1):** 100-migration bootstrap completes within budget against 3-node Testcontainers cluster
- (n) **Partial-rollback ledger state (R-19 / NF-5):** rollback statement N fails after N+1..M succeeded → ledger has `status: partially_rolled_back` with `failedStatementIndex: N`; subsequent runs require `--force-resume`
- (o) **`MIGRATE INDEX` composite (R-30):** end-to-end test asserts the composite verb produces identical end-state to the hand-composed `CREATE INDEX` + `REINDEX` + `ALIAS SWAP` sequence (cluster state diff is empty); also asserts `WITH TEMPLATE` resolves to the same body as the template's `template` block

**Otherwise:** Each scenario has a single named test with clear assertions; failures surface the specific assertion that failed, not just "test failed"

**Depends on:** R-24a
**Priority:** Must — this is the "production-capable" gate
**Confidence:** Medium (some scenarios like 429 simulation need infra choices made)

### Distribution & Production Readiness

#### R-26: Runner project follows the existing per-provider pattern

**Actor:** Operator deploying migrations as a standalone executable
**Intention:**
- *Immediate:* Operators run migrations the same way they run Aerospike/Couchbase/MongoDB/Postgres migrations
- *Outcome:* No special-casing in deploy pipelines per provider
- *Metric:* The same Helm chart / Dockerfile / Octopus deploy template works for OpenSearch by swapping the package

**Friction today:**
- Current: Existing providers ship `runners/Hyperbee.MigrationRunner.<Provider>` projects; OpenSearch must match
- Failure mode: Diverging from the runner pattern fragments operator muscle memory
- Frequency: Every deploy

**Given:** A `runners/Hyperbee.MigrationRunner.OpenSearch` project exists
**When:** Operator runs the binary with standard configuration (appsettings.json + env overrides)
**Then:** Runner reads connection details, profile, target version, and locking from `IConfiguration`; binds to `OpenSearchMigrationOptions` per ADR-0006; loads embedded migration assemblies; invokes `MigrationRunner.RunAsync`; exits with non-zero on failure and zero on success
**Otherwise:** Runner produces structured JSON logs (matching the Aerospike runner) suitable for log aggregation; emits a final summary of applied/skipped/failed migrations

**Depends on:** R-22
**Priority:** Must
**Confidence:** High

#### R-27: Samples project demonstrates every v1 verb

**Actor:** New adopter or PR reviewer
**Intention:**
- *Immediate:* Authors can copy-paste a sample for any operation
- *Outcome:* Adoption time measured in minutes, not hours
- *Metric:* Each verb in R-08a appears in at least one sample migration with a meaningful body

**Given:** A `runners/samples/Hyperbee.Migrations.OpenSearch.Samples` project exists
**When:** Adopter browses samples
**Then:** Samples include (a) initial index creation with mapping and settings, (b) alias swap zero-downtime reindex (hand-composed), (c) ISM policy creation and attachment, (d) component template + composable index template pattern, (e) bulk seed of N docs, (f) conditional migration via `WHEN VERSION`, (g) rollback example for a reversible operation, (h) templating example with environment-specific values, (i) **`MIGRATE INDEX` composite verb (R-30) — the recommended pattern for propagating template/mapping changes to existing data**, (j) `UNSAFE("...")` and `NO WAIT("...")` justification idioms with the syntactic enumeration of operations requiring them
**Otherwise:** Each sample is a runnable migration class with a comment explaining the production scenario it demonstrates. Sample (i) is featured prominently in the README as the answer to "how do I apply template changes to existing data?"

**Depends on:** R-08a, R-19, R-26
**Priority:** Should
**Confidence:** High

#### R-28: Multi-topology validation: single-node, multi-node, AWS Managed OpenSearch

**Actor:** CI pipeline + manual validation cycle
**Intention:**
- *Immediate:* Provider works on the topologies real teams use, not just CI single-node
- *Outcome:* Production deploys to AWS Managed OpenSearch and on-prem multi-node clusters succeed without surprises
- *Metric:* Documented test results against each topology before each release

**Friction today:**
- Current: Tools tested only against single-node fail in subtle ways on multi-node (replica allocation, cluster state propagation, refresh timing, SigV4 auth path)
- Failure mode: Production-only bugs (yellow vs green hardcoding; SigV4 auth misconfiguration; replica allocation timeouts)
- Frequency: First production deploy of every release

**Given:** Three target topologies are recognized: (a) single-node Testcontainers (CI default), (b) multi-node (3-node) Testcontainers Compose for replica behavior, (c) AWS Managed OpenSearch domain with SigV4 auth (scheduled CI cycle)
**When:** Release validation runs
**Then:**
- Topology (a) and (b) are **fully automated in CI on every PR** — multi-node is no longer optional; OpenSearch's Docker image runs as a 3-node cluster trivially via Testcontainers `INetwork` + `discovery.seed_hosts` + `cluster.initial_master_nodes`. Topology (b) verifies: green-threshold behavior, replica allocation, shard relocation during `ALIAS SWAP`, the lock index `number_of_replicas: 0` setting prevents replica-write coupling under concurrent acquire (PA-2)
- Topology (c) is a scheduled validation (e.g., nightly or pre-release) with a runbook covering the smoke-test verbs (R-08a), SigV4 connectivity, and ISM endpoint capability probing (R-21)

**Otherwise:** When AWS Managed validation cannot be reached in scheduled CI (no AWS account credentials available), this is logged on the release checklist as "deferred"; manual validation results are recorded in the release notes

**Depends on:** R-21, R-24a
**Priority:** Must (a, b — both CI-automated); Should (c — scheduled)
**Confidence:** High (multi-node Compose is well-supported by Testcontainers-dotnet)

### Observability

#### R-25: Structured logging at key state transitions, with secret scrubbing

**Given:** Standard ILogger is configured
**When:** Provider runs
**Then:**
- DEBUG: every statement compiled and dispatched; Tasks API per-poll progress
- INFO: bootstrapper state transitions, lock acquired/renewed/released, each migration start/end with duration, Tasks API percentage thresholds (10/25/50/75/90%), Tasks API backoff transitions, **startup banner emitting all resolved defaults** (`Profile`, `ClusterHealthThreshold`, `WaitMode`, `RequireUnsafeJustification`, `ContextResolutionPolicy`, `ActiveContext`, rollback enabled/disabled, lock parameters)
- WARN: 429 retries (with batch size and retry count), lock takeover events, slow waits, structured `migration.unsafe_bypass` and `migration.no_wait` events with justification reasons
- ERROR: parse failures (with file/index/recognized-verb-so-far), lock conflicts, task errors, `MigrationLockExpiredException`
- All log sinks and exception messages route through `SecretScrubber` (R-10) — values matching known secret content-hashes are redacted to `***REDACTED***` regardless of which scope they came from (closes MD-15)

**Otherwise:** Correlation includes migration id and task id where applicable

**Priority:** Must (was Should — promoted because the startup banner and SecretScrubber both close Critical/High findings)
**Confidence:** High

## Constraints

- **Compatibility with ADRs 0001-0010:** Must comply or supersede explicitly. No requirement currently supersedes any ADR.
- **Client packages:** OpenSearch.Client 1.8+ and OpenSearch.Net 1.8+; AWS SigV4 via optional package
- **TFM:** net8.0 / net9.0 to match the rest of Hyperbee.Migrations
- **License:** Apache 2.0 compatible
- **Async-only API surface** (matches existing providers)
- **Cancellation:** `CancellationToken` propagates from runner through all async paths
- **Templating engine:** Hyperbee.Templating (in-house) — first provider to wire it
- **Parser:** Parlot (ADR-0001) — non-negotiable house standard; no alternative parser permitted
- **No external lock dependency** (Redis/etcd) — must be OpenSearch-native (ADR-0005)
- **Minimum cluster version:** OpenSearch 2.0+ (decide on legacy ES support — see Open Questions)

## Trust Boundaries

**Autonomous** (provider acts without human approval):
- Acquire and renew the migration lock; take over a stale lock that exceeds `LockStaleAfter` after **realtime GET verification** (R-05)
- Apply migrations in version order
- Skip statements gated by `IF [NOT] EXISTS` or `WHEN` conditions (subject to `ContextResolutionPolicy`)
- Inject `dynamic: strict` into flat managed-index mappings (NOT into `composed_of` bodies — R-17)
- Inject `op_type: create` into `REINDEX` request bodies by default (R-08a)
- Poll Tasks API and surface progress
- Atomic alias swap as a single `_aliases` request with in-body precondition (R-16)
- Emit the startup banner with resolved configuration defaults (R-25)
- Cancel the in-flight migration's `CancellationToken` when `LockMaxLifetime` is reached (R-05)

**Escalate** (caller decides):
- Lock contention (`MigrationLockUnavailableException`) — caller chooses retry or bail
- Bootstrapper timeout — caller chooses to fail the deploy or retry later
- 409 on ledger write — caller bails (concurrent runner detected)
- `MigrationLockExpiredException` (max-lifetime hit mid-migration) — caller decides to retry after operator review
- Partial-rollback recovery (`status: partially_rolled_back`) — operator must invoke `--force-resume` after reconciling cluster state

**Forbidden** (provider never does):
- Run migrations without acquiring the lock (when locking is enabled)
- Bypass parse-time unsafe-operation detection silently (must require `UNSAFE("<justification>")` opt-in with non-empty reason)
- Bypass implicit waits silently (must require `NO WAIT("<justification>")` opt-in with non-empty reason under `WaitMode = PerStatement`)
- Auto-generate inverse operations (rollback is opt-in only)
- Modify the migration ledger index mapping after creation (immutable per R-06)
- Silently apply `context`-gated migrations when `ActiveContext` is unset under `ContextResolutionPolicy = RequireExplicit` (R-15)
- Log secret values from any scope — value-coupled scrubbing applies regardless of source (R-10, R-25)
- Run two `MigrationRunner.RunAsync` calls concurrently within a single process
- Take over a lock based on search-staleness alone (must verify via realtime GET — R-05)
- Execute a `REINDEX` without `op_type: create` unless explicit `REINDEX UNSAFE("<justification>") FROM ...` is used (R-08a, R-18)
- Inject `dynamic: strict` into a body with `composed_of` (must defer to component template — R-17)

## Out of Scope

- **OpenSearch Dashboards saved objects** — different host/port; use Dashboards' own export API
- **k-NN, ML connectors, anomaly detection plugin objects** — ecosystem extras for v1
- **Remote reindex (`reindex.remote.allowlist`)** — supported as a body verbatim pass-through but no provider-level allowlist management
- **Auto-generated rollbacks** — too dangerous; rollback is opt-in only
- **Multi-cluster migration orchestration** — one cluster per provider instance
- **Snapshot repository plugin installation** — repos are pre-existing; provider configures, does not install
- **Pre-OpenSearch Elasticsearch 7.x and earlier** — see Open Questions
- **Schema diffing or auto-generated migrations** — out of band; teams write migrations manually

## Decisions & Open Questions

### Decided

- **Hybrid Parlot grammar over opaque JSON bodies** — *rationale:* matches Couchbase/Aerospike/MongoDB house style and ADR-0001/ADR-0002. *Influences:* R-08, R-08a, R-09
- **Sibling `$name` body references over inline JSON strings** — *rationale:* eliminates quote-escaping; real JSON tooling can format and lint. Reserved Parlot identifiers (`$body`, `$query`, `$script`) and reserved templating scope names (`env`, `config`, `runtime`, `secrets`) cannot collide. *Influences:* R-09
- **Hyperbee.Templating with env/config/runtime/secrets scopes** — *rationale:* in-house engine, four-scope structure covers prior-art needs. *Influences:* R-10
- **Auto-renewing lock heartbeat ported from Aerospike, with realtime-GET takeover and explicit max-lifetime cancellation contract** — *rationale:* OpenSearch refresh-lag invalidates pure search-based staleness checks; max-lifetime must abort, not warn. *Influences:* R-04, R-05
- **Ledger lives in OpenSearch itself** — *rationale:* operational simplicity (one system to back up); ADR-0005 prefers provider-native. Strict mapping is immutable; forensic fields (`appliedBy`, `direction`, `failedStatementIndex`) MUST land before v1. *Influences:* R-06, R-07
- **Implicit + explicit wait grammar with `WaitMode` enum (PerStatement / PerMigration / Off)** — *rationale:* default robustness without N+1 master storms; PerMigration is production default. Implicit waits scope to the mutated index by default. *Influences:* R-12, R-13
- **Optional best-effort rollback with explicit partial-rollback ledger semantics** — *rationale:* most NoSQL operations are not safely reversible; partial-rollback failure mid-sequence requires `partially_rolled_back` state and `--force-resume` recovery. *Influences:* R-19, R-06
- **`WithProductionDefaults()` extension method instead of an environment enum** — *rationale:* discoverable in IntelliSense, grep-able in code review, no hidden coupling. Replaces an earlier `EnvironmentProfile` proposal that was rejected during assessment for hidden-coupling concerns. *Influences:* R-03, R-12, R-15, R-18, R-29
- **`Yellow` SDK default health threshold; `Green` via `WithProductionDefaults()`** — *rationale:* dev/CI single-node clusters cannot reach Green; safer default for SDK while production explicitly opts in. *Influences:* R-03, R-29
- **`UNSAFE("<justification>")` and `NO WAIT("<justification>")` modifiers require non-empty reasons** — *rationale:* MD-2/MD-11 single-token bypasses are silent in PR review; justification token gives high-signal grep target. *Influences:* R-12, R-18, Trust Boundaries
- **`op_type: create` auto-injected into `REINDEX` bodies by default (parser-level, opt-out via `REINDEX UNSAFE`)** — *rationale:* same precedent as R-17 dynamic-strict injection; sample-based fix to a laziest-path correctness hazard is anti-pattern. *Influences:* R-08a
- **Component-template-aware `dynamic: strict` injection (skipped when body has `composed_of`)** — *rationale:* layered mappings; injection at index level clobbers component-level `dynamic: false`. *Influences:* R-17
- **`ALIAS SWAP` precondition is in-body, not a separate GET** — *rationale:* eliminates TOCTOU window; cluster atomically rejects entire body. *Influences:* R-16
- **Semantic version comparison for `WHEN VERSION`** — *rationale:* string compare returns wrong answer on `'2.9' < '2.10'`; correctness gap, not future concern. *Influences:* R-15a
- **`ActiveContext` option as source-of-truth for context filter; `ContextResolutionPolicy.RequireExplicit` in production** — *rationale:* silent-skip and silent-run are both worse than fail-loud; production must require explicit context. *Influences:* R-15
- **Render-time `SecretMarker` + log-time `SecretScrubber` by content hash** — *rationale:* value-coupled redaction protects against operators accidentally putting secrets in `config` scope (MD-15). *Influences:* R-10, R-25
- **Multi-node Testcontainers Compose CI is Must, not Should** — *rationale:* Green-threshold and replica-allocation behaviors are never exercised on single-node; OpenSearch image runs as 3-node cluster trivially. *Influences:* R-28
- **Testcontainers OpenSearch image pinned by sha256 digest** — *rationale:* "2.x latest" is mutable; CI silently picks up new image, prod runs older cluster, behavior diverges. *Influences:* R-24a
- **Lock index `number_of_replicas: 0`** — *rationale:* eliminates replica-write coupling on the lock primary shard under N concurrent runners (PA-2). *Influences:* R-04
- **AWS endpoint loud-fail + ISM endpoint capability detection** — *rationale:* MD-6/PM-2/PM-6 are caught at startup with the exact remediation snippet, not silently in production. *Influences:* R-21
- **Tasks API per-poll progress logged at DEBUG, INFO only on percentage thresholds** — *rationale:* PA-4 log flood for long reindexes. *Influences:* R-11, R-25
- **`MIGRATE INDEX` composite verb encoding the canonical reindex-and-swap pattern** — *rationale:* template/mapping changes do not propagate to existing data in OpenSearch; sample-only documentation is anti-pattern (assessment 0002 meta-finding). The composite verb makes the safe pattern the lazy path. *Influences:* R-08a, R-30, R-27

### Open

- **Legacy Elasticsearch 7.x support** — Status: deferred. Reason: API surface is identical to OpenSearch 1.x but the package and license differ. Leaning: NOT in v1 — keep this OpenSearch-specific; add a sibling `Elasticsearch` provider later if demand exists. Depends on: user/maintainer call. Influences: client package choice in Constraints.
- **Snapshot/restore as v1 verbs** — Status: deferred. Reason: snapshot repos require pre-existing config; long-running operations stress the warmup model. Leaning: include `WAIT UNTIL TASK` infrastructure in v1 (R-11) and add `SNAPSHOT`/`RESTORE` verbs in v1.1. Depends on: scope decision. Influences: verb set in R-08a.
- **Security-plugin objects (roles, role mappings) as v1 verbs** — Status: deferred. Reason: requires admin-cert auth which complicates DI; tenant model is a separate design problem. Leaning: not in v1. Depends on: scope decision. Influences: verb set in R-08a, Out of Scope.
- **Semantic unsafe-operation detection (R-18 deep tier)** — Status: deferred. Reason: requires reading live mapping/index state at parse or pre-execute time; semantic understanding of query effects is a research project. Leaning: ship syntactic enumeration in v1; semantic detection in v1.1 if real-world incidents justify. Depends on: post-v1 incident telemetry. Influences: R-18.
- **`WHEN VERSION` long-tail suffix support** — Status: deferred. Reason: AWS `OpenSearch_<x>` prefix and `-rc<N>` / `-SNAPSHOT` qualifiers will need normalization rules as they appear in real clusters. Leaning: ship clean `MAJOR.MINOR.PATCH` + documented suffix rules in v1; tighten as needed. Depends on: production diversity. Influences: R-15a.
- **AWS Managed OpenSearch CI automation** — Status: deferred (Should). Reason: requires AWS account scaffolding and credentials in CI. Leaning: scheduled validation runbook in v1; full automation v1.1+. Depends on: project AWS account access. Influences: R-28.
- **JSON Schema for `statements.json` (IDE help)** — Status: deferred. Reason: nice-to-have IDE ergonomics; not blocking correctness. Leaning: v1.1. Depends on: adopter feedback. Influences: R-08, R-09.
- **Topology-aware bulk-load parallelism** — Status: deferred. Reason: PA-6 says default 8x parallelism saturates small-node thread pools and triggers self-induced 429s. Leaning: ship with conservative defaults documented; add adaptive tuning in v1.1. Depends on: real-cluster benchmarks. Influences: R-20.
- **OpenSearch.Client v2 / cluster 3.x compatibility** — Status: monitor. Reason: PM-8 says client may go stagnant against 3.x clusters. Leaning: track upgrade cadence; canary against `next-major` Testcontainers image; bump pinned image when 3.x ships. Depends on: OpenSearch project release schedule. Influences: R-24a, Constraints.

## Recommended next steps

1. **`/nop:propose`** to evaluate concrete implementation strategies against these requirements as fitness criteria. The remaining tensions (Open Questions) are mostly scope decisions; the load-bearing implementation choices to evaluate are: (a) parser-level injection vs runtime middleware for `op_type: create` and `dynamic: strict`; (b) lock-index initialization (provision-on-demand vs explicit options); (c) `WithProductionDefaults()` implementation (extension method vs builder pattern); (d) bootstrapper architecture (state machine like Couchbase vs simpler async sequence).
2. **`/nop:plan`** once propose selects a winner.
