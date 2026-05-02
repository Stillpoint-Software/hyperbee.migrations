# Plan: OpenSearch Provider for Hyperbee.Migrations

**Status:** Active
**Created:** 2026-05-02 (collapsed from 8-phase to 4-phase after assessment 0003 calibration)
**Branch:** `devs/bfarmer/provider-opensearch`
**Inputs:**
- Requirements: [docs/requirements/opensearch-provider.md](../../requirements/opensearch-provider.md) (31 testable requirements)
- Design: [docs/design/opensearch-provider.md](../../design/opensearch-provider.md) (Pragmatic Hybrid)
- Research: [0001](../../research/0001-opensearch-provider.md), [0002](../../research/0002-opensearch-provider-assessment.md), [0003](../../research/0003-opensearch-plan-assessment.md)
- ADRs: 0001-0015 (especially 0011-0015 for this provider)

## Velocity calibration

This plan is sized to the maintainer's actual velocity:
- Aerospike provider (with auto-renewing lock + Parlot grammar) shipped in **1 day**
- Couchbase provider (most complex, 7-state bootstrapper + N1QL grammar) shipped in **under 1 week**

Realistic estimate: **3-7 days of focused work** for the core provider, **1-2 days polish**. The plan structure follows that cadence.

## Objective

Build a production-capable OpenSearch provider satisfying all 31 requirements and complying with all 15 ADRs:

- Zero data loss during reindex/alias swaps
- No permanent lockouts from crashed runners
- Same migrations run unchanged across single-node dev, multi-node CI, AWS Managed (scheduled)
- Parser-level safe defaults per ADR-0011 (`op_type: create`, component-template-aware `dynamic: strict`)
- Parser is offline-pure; all I/O in runtime middleware per ADR-0015
- `WithProductionDefaults()` extension surface per ADR-0012
- Always-create indices with `AssumeIndicesExist` override per ADR-0013
- State-machine façade over `IBootstrapStep[]` pipeline per ADR-0014

## Style Reference

Citations across 6 patterns (≥10 file:line refs).

### Pattern 1 — Auto-renewing lock with TimeProvider (R-04, R-05, ADR-0005)

- **CAS acquire**: [AerospikeRecordStore.cs:53-90](../../../src/Hyperbee.Migrations.Providers.Aerospike/AerospikeRecordStore.cs#L53-L90) — `WritePolicy.recordExistsAction = CREATE_ONLY` is server-enforced atomicity; `KEY_EXISTS_ERROR` translates to `MigrationLockUnavailableException`. **OpenSearch analogue:** `if_seq_no`/`if_primary_term` returning 409 → `MigrationLockUnavailableException` (per ADR-0011 + R-04).
- **Heartbeat renewal loop**: [AerospikeRecordStore.cs:92-144](../../../src/Hyperbee.Migrations.Providers.Aerospike/AerospikeRecordStore.cs#L92-L144) — uses `Task.Delay(interval, _timeProvider, ct)` for test-time virtualization; deadline check enforces `LockMaxLifetime`; transient errors logged but not re-thrown (TTL provides recovery buffer). **OpenSearch must extend this**: per R-05 + NF-1 from assessment 0002, OpenSearch heartbeat must use `realtime: true` GET on takeover (refresh-lag would otherwise produce false-takeovers).
- **LockHandle disposal**: [AerospikeRecordStore.cs:199-244](../../../src/Hyperbee.Migrations.Providers.Aerospike/AerospikeRecordStore.cs#L199-L244) — `Interlocked.CompareExchange` for idempotent dispose; cancels renew before deleting record; logs critical on cleanup failure.
- **Parameter validation (sample)**: [AerospikeRecordStore.cs:44-48](../../../src/Hyperbee.Migrations.Providers.Aerospike/AerospikeRecordStore.cs#L44-L48) — only validates `LockRenewInterval < LockExpireInterval`. **OpenSearch must add** `LockStaleAfter ≥ 2 * LockRenewInterval` per R-05.
- **Options shape**: [AerospikeMigrationOptions.cs:17-44](../../../src/Hyperbee.Migrations.Providers.Aerospike/AerospikeMigrationOptions.cs#L17-L44) — `LockExpireInterval` (60s), `LockRenewInterval` (30s), `LockMaxLifetime` (1h). OpenSearch will rename `LockExpireInterval` → `LockStaleAfter` for clarity.

### Pattern 2 — Multi-state bootstrapper (ADR-0014, R-02)

- **State-machine façade**: [CouchbaseBootstrapper.cs:36-67](../../../src/Hyperbee.Migrations.Providers.Couchbase/CouchbaseBootstrapper.cs#L36-L67) — single public `WaitForSystemReadyAsync(TimeSpan? timeout, CancellationToken)`; uses `TimeoutTokenSource` + linked CTS; sequential `WaitForCluster` → `WaitForBuckets` → `Warmup`.
- **6-state cluster wait**: [CouchbaseBootstrapper.cs:91-180](../../../src/Hyperbee.Migrations.Providers.Couchbase/CouchbaseBootstrapper.cs#L91-L180) — Start → WaitForUri → StateUriReady → WaitForHealthy → StateHealthy → WaitForReady; explicit 5s sleep at StateHealthy works around the SDK bootstrap race. **OpenSearch's analogue**: per ADR-0014 we wrap `IBootstrapStep[]` with this state-machine shape, exposing `BootstrapResult.Steps` for diagnostics.
- **Notify interval pattern**: [CouchbaseBootstrapper.cs:28-34](../../../src/Hyperbee.Migrations.Providers.Couchbase/CouchbaseBootstrapper.cs#L28-L34) — bounded by `Math.Min(timeoutSeconds, reportSeconds)`; logs progress at interval without blocking actual operation timeout.
- **Sacrificial query warmup**: [CouchbaseBootstrapper.cs:214-235](../../../src/Hyperbee.Migrations.Providers.Couchbase/CouchbaseBootstrapper.cs#L214-L235) — first `system:*` query after hard shutdown returns unpredictable results; this query primes N1QL. OpenSearch analogue: optional final step (skip-able) that primes a known system index.

### Pattern 3 — Parlot grammar (ADR-0001, R-08)

- **`static readonly Parser<T>` cache**: [StatementParser.cs:35](../../../src/Hyperbee.Migrations.Providers.Couchbase/Parsers/StatementParser.cs#L35) — parser built once at class load (PA-8 already pattern-encoded; satisfies ADR-0011 spike test).
- **Keyword definitions**: [StatementParser.cs:40-62](../../../src/Hyperbee.Migrations.Providers.Couchbase/Parsers/StatementParser.cs#L40-L62) — `Terms.Text("CREATE", caseInsensitive: true)` for SQL-style keywords. OpenSearch reuses this exactly.
- **Identifier with backtick escape**: [StatementParser.cs:69-73](../../../src/Hyperbee.Migrations.Providers.Couchbase/Parsers/StatementParser.cs#L69-L73) — `Between(Terms.Char('`'), pattern, Terms.Char('`')).Or(plainIdentifier)` — OpenSearch index names with dots/dashes need this same shape.
- **Composed reference grammars with disambiguation**: [StatementParser.cs:88-110](../../../src/Hyperbee.Migrations.Providers.Couchbase/Parsers/StatementParser.cs#L88-L110) — `keyspaceRef = OneOf(keyspaceNs3, keyspace3, ..., keyspace1)` for 1/2/3-part graceful disambiguation.
- **Statement disambiguation order**: [StatementParser.cs:286-301](../../../src/Hyperbee.Migrations.Providers.Couchbase/Parsers/StatementParser.cs#L286-L301) — `createPrimaryIndex` BEFORE `createIndex` (both start with CREATE) — order matters in `OneOf`. OpenSearch will need similar care for `CREATE INDEX` vs `CREATE TEMPLATE` vs `CREATE COMPONENT` vs `CREATE POLICY`.
- **Public parse entry**: [StatementParser.cs:304-314](../../../src/Hyperbee.Migrations.Providers.Couchbase/Parsers/StatementParser.cs#L304-L314) — `TryParse` + throw `NotSupportedException` with full statement. **OpenSearch must do better** per assessment 0002 — include file/index/recognized-verb in error.

### Pattern 4 — DI registration (ADR-0006, ADR-0012)

- **Two-overload entrypoint**: [Aerospike/ServiceCollectionExtensions.cs:12-20](../../../src/Hyperbee.Migrations.Providers.Aerospike/ServiceCollectionExtensions.cs#L12-L20) — no-config + `Action<options>` overloads delegate to private with caller `Assembly`.
- **Options factory closure**: [Aerospike/ServiceCollectionExtensions.cs:24-52](../../../src/Hyperbee.Migrations.Providers.Aerospike/ServiceCollectionExtensions.cs#L24-L52) — factory builds options with `DefaultMigrationActivator(provider)`, applies user config, merges `IConfiguration` `Migrations:FromAssemblies`/`FromPaths` with code assemblies, deduplicates, defaults to caller.
- **Singleton registrations**: [Aerospike/ServiceCollectionExtensions.cs:54-62](../../../src/Hyperbee.Migrations.Providers.Aerospike/ServiceCollectionExtensions.cs#L54-L62) — `OptionsType` singleton, upcast to `MigrationOptions` for runner, `IMigrationRecordStore` singleton, `MigrationRunner` singleton, resource runner generic transient, `TryAddSingleton(TimeProvider.System)`. **OpenSearch adds**: `IBootstrapStep[]` registrations (per ADR-0014), `WithProductionDefaults()` extension that mutates options post-registration (per ADR-0012).
- **IConfiguration helper**: [Aerospike/ServiceCollectionExtensions.cs:65-66](../../../src/Hyperbee.Migrations.Providers.Aerospike/ServiceCollectionExtensions.cs#L65-L66) — `GetEnumerable<T>` returns empty for missing sections (defensive).

### Pattern 5 — Options inheritance (ADR-0006)

- **Base + provider-specific shape**: [AerospikeMigrationOptions.cs:3](../../../src/Hyperbee.Migrations.Providers.Aerospike/AerospikeMigrationOptions.cs#L3) — `class AerospikeMigrationOptions : MigrationOptions`.
- **Default-named constants**: [AerospikeMigrationOptions.cs:5-7](../../../src/Hyperbee.Migrations.Providers.Aerospike/AerospikeMigrationOptions.cs#L5-L7) — `public const string DefaultNamespace = "test"` style.
- **Two-constructor pattern**: [AerospikeMigrationOptions.cs:29-44](../../../src/Hyperbee.Migrations.Providers.Aerospike/AerospikeMigrationOptions.cs#L29-L44) — parameterless ctor delegates to activator overload; activator overload sets defaults.
- **Deconstruct convenience**: [AerospikeMigrationOptions.cs:46-51](../../../src/Hyperbee.Migrations.Providers.Aerospike/AerospikeMigrationOptions.cs#L46-L51) — tuple unpacking for ergonomic access.

### Pattern 6 — Project file shape (ADR-0006, R-21)

- **Csproj template**: [Hyperbee.Migrations.Providers.Aerospike.csproj:21-31](../../../src/Hyperbee.Migrations.Providers.Aerospike/Hyperbee.Migrations.Providers.Aerospike.csproj#L21-L31) — central package management (versions implicit at solution level), `<Title>`, `<Description>`, `PackageId`/`Authors`/license metadata, `InternalsVisibleTo` for unit tests, `<ProjectReference>` to core, `<PackageReference>` for client SDKs + DI/Hosting/Logging abstractions + Parlot. OpenSearch project mirrors this exactly with `OpenSearch.Client` substituting for `Aerospike.Client`; AwsSigV4 NuGet is opt-in (separate package or conditional reference per ADR-0011).

### Anti-patterns to avoid (extracted from audit)

- **Don't dispatch network I/O from the parser** (per ADR-0015). Aerospike/Couchbase parsers don't; OpenSearch's `MIGRATE INDEX ... WITH TEMPLATE` must produce an `unresolved-reference` AST node — runtime middleware resolves the template body.
- **Don't bare-`UNSAFE`** — Couchbase has nothing like this, but OpenSearch's `UNSAFE` and `NO WAIT` modifiers must require non-empty justification per R-18 (assessment 0002 MD-2).
- **Don't fold safe-default injection into runtime middleware alone** — assessment 0002 PM-3, PM-4, MD-9 prove parser-level enforcement is required (per ADR-0011 hybrid).
- **Don't return null from `IMigrationRecordStore.ReadAsync` without doc**: [AerospikeRecordStore.cs:165-166](../../../src/Hyperbee.Migrations.Providers.Aerospike/AerospikeRecordStore.cs#L165-L166) returns null — works today because no caller hits that path; OpenSearch should match for contract consistency.

## Git workflow

| Phase | Snapshot tag | When taken |
|-------|--------------|------------|
| 0 | `opensearch/phase-0-spike-validated` | After Phase 0 (scaffold + spike) — gate before further work |
| 1 | `opensearch/phase-1-foundation` | After foundation + foundation verbs work end-to-end |
| 2 | `opensearch/phase-2-atomic-composite` | After REINDEX/ALIAS/MIGRATE/templates/cross-cutting features land |
| 3 | `opensearch/phase-3-shippable` | After distribution + multi-topology CI green |

Branch: `devs/bfarmer/provider-opensearch` from `main`. Per-phase PRs.

---

## Phase 0: Scaffold + Risk-First Spike

**Goal:** Project structure exists; harness boots; **the riskiest assumption (parser-emitted AST safe-default flags merge cleanly into arbitrary user-supplied JSON bodies) is validated against real OpenSearch.** If the spike fails, ADR-0011 needs revision and Approach A (runtime-middleware-only — see design rejected approaches) becomes the documented fallback.

**Estimated effort:** Half a day to one day.

**Completion Criteria:**
- Solution builds clean across all four projects (provider, runner, samples, tests)
- Style Reference section populated with ≥10 file:line citations across ≥4 patterns
- Single-node Testcontainers harness boots; cluster reaches yellow
- 10 representative spike tests pass against real OpenSearch (5 CREATE INDEX shapes + 5 REINDEX shapes — see kill criterion below)
- Phase 0 snapshot tagged

**Phase 0 kill criterion (verbatim per assessment 0003 / A8):**
> *Merge logic cannot deterministically produce expected JSON without ambiguity for any of the 5 documented edge cases.*

If this fires, escalate per `/nop:debug` and consider whether ADR-0011 needs superseding before Phase 1 starts. **Fallback architecture:** Approach A (Couchbase-Clone, runtime middleware only) per design rejected approaches. AST types and grammar (Tasks 0.3, 0.4) remain reusable; only the merge middleware (Task 0.5) becomes rework.

### Tasks

#### 0.1: Codebase audit + Style Reference (promoted to first task per A4)

Audit existing providers; populate the Style Reference section above with concrete citations. Without this, downstream "follow existing pattern" claims are unverifiable.

- [x] Read [AerospikeRecordStore.cs](../../../src/Hyperbee.Migrations.Providers.Aerospike/AerospikeRecordStore.cs) — auto-renewing lock pattern, TimeProvider injection
- [x] Read [CouchbaseBootstrapper.cs](../../../src/Hyperbee.Migrations.Providers.Couchbase/CouchbaseBootstrapper.cs) — state-machine pattern
- [x] Read [Couchbase StatementParser.cs](../../../src/Hyperbee.Migrations.Providers.Couchbase/Parsers/StatementParser.cs) — Parlot grammar shape
- [x] Read [Aerospike/ServiceCollectionExtensions.cs](../../../src/Hyperbee.Migrations.Providers.Aerospike/ServiceCollectionExtensions.cs) — DI pattern
- [x] Read [AerospikeMigrationOptions.cs](../../../src/Hyperbee.Migrations.Providers.Aerospike/AerospikeMigrationOptions.cs) — options inheritance
- [x] Read [Aerospike csproj](../../../src/Hyperbee.Migrations.Providers.Aerospike/Hyperbee.Migrations.Providers.Aerospike.csproj) — project file shape
- [x] Populate Style Reference section: 6 patterns, ≥20 file:line citations, anti-patterns extracted

#### 0.2: Project scaffolding

Mirror the Aerospike layout exactly: `src/Hyperbee.Migrations.Providers.OpenSearch/`, `runners/Hyperbee.MigrationRunner.OpenSearch/`, `runners/samples/Hyperbee.Migrations.OpenSearch.Samples/`, `tests/.../OpenSearch/`.

- [ ] Create four projects; net8.0;net9.0; Apache 2.0
- [ ] NuGet refs: OpenSearch.Client 1.8.x, OpenSearch.Net 1.8.x, Parlot, Hyperbee.Templating, Testcontainers + OpenSearch image (pinned by sha256 digest)
- [ ] Add to solution; `dotnet build` clean

#### 0.3: Single-node Testcontainers harness + hello-world

- [ ] `OpenSearchTestContainer.cs` mirroring Aerospike harness shape
- [ ] Hello-world test: container boots, `_cluster/health` returns yellow
- [ ] Document the version-support contract (per A11/NF-6): minimum supported OpenSearch version, pinned digest, AWS Managed caveat — comment header in the container file + README line

#### 0.4: Hyperbee.Templating first-contact spike (per A6)

Wire the four-scope renderer (`env`, `config`, `runtime`, `secrets`) and validate that JSON-context rendering with `{{#if}}` and `{{each}}` blocks produces well-formed output. Catches first-contact bugs before they cascade.

- [ ] Wire renderer with all four scopes
- [ ] Three smoke tests: simple substitution, conditional inside JSON, iteration inside JSON
- [ ] Document any quirks discovered in Style Reference

#### 0.5: Spike — minimal AST + grammar + SafeDefaultMergeMiddleware

Smallest implementation that validates the parser/runtime split.

- [ ] `StatementAst` abstract record with `SafeDefaults` dictionary; concrete `CreateIndexAst` and `ReindexAst`
- [ ] Parlot grammar parsing only `CREATE INDEX <name> WITH BODY $body` and `REINDEX FROM <src> TO <dst> [WITH BODY $body]`
- [ ] `SafeDefaultMergeMiddleware` operating on `JsonNode` trees: merge `op_type: create` (REINDEX dest path); merge `dynamic: strict` (CREATE INDEX mappings path) with `composed_of` detection
- [ ] Unit tests for AST construction, grammar positive/negative cases, merge logic (10+ cases)

#### 0.6: Spike — 10 wire-level integration tests against real OpenSearch

Capture actual HTTP request bodies via custom `IConnection` or HTTP capture; assert merge correctness.

- [ ] Test: CreateIndex flat body without `mappings` → request has `mappings.dynamic: strict`
- [ ] Test: CreateIndex with explicit `mappings.dynamic: true` → preserves user value; INFO logged
- [ ] Test: CreateIndex with `composed_of` → injection skipped; INFO logged
- [ ] Test: CreateIndex with `mappings.properties` only → injection adds `dynamic: strict` alongside properties
- [ ] Test: CreateIndex with settings only → injection creates `mappings.dynamic: strict` block
- [ ] Test: Reindex without body → request has `{ "source": {...}, "dest": {..., "op_type": "create"} }`
- [ ] Test: Reindex with existing body and `dest` object → preserves user fields, adds `op_type: create`
- [ ] Test: Reindex with body specifying `op_type: index` → fails at parse time (UNSAFE required)
- [ ] Test: Reindex with body specifying `op_type: create` explicitly → idempotent inject
- [ ] Test: Round-trip — Create + Reindex against actual cluster, verify destination strict mapping and op_type:create honored

**Phase 0 gate:** All 10 tests pass + kill criterion not fired. Tag `opensearch/phase-0-spike-validated`.

---

## Phase 1: Foundation + Foundation Verbs

**Goal:** Empty migration runs end-to-end against single-node Testcontainers. Lock acquired and renewed; ledger initialized; bootstrapper completes. Foundation verbs (CREATE/DROP INDEX, UPDATE MAPPING/SETTINGS, REFRESH, WAIT) execute correctly. Lock contention and crash recovery scenarios pass.

**Estimated effort:** 1-2 days.

**Completion Criteria:**
- DI surface complete: `services.AddOpenSearchMigrations(opts => {}).WithProductionDefaults()` (ADR-0012)
- Bootstrapper façade with `IBootstrapStep[]` pipeline (ADR-0014)
- Ledger schema with all forensic fields per R-06 (`appliedBy`, `direction`, `failedStatementIndex`)
- LockHandle: CAS acquire + heartbeat renew + realtime-GET takeover + `LockMaxLifetime` cancellation contract (R-05)
- Lock parameter validation at startup (`LockRenewInterval < LockStaleAfter < LockMaxLifetime` AND `LockStaleAfter ≥ 2 * LockRenewInterval`)
- `AssumeIndicesExist` override path (ADR-0013)
- Foundation verbs all parse, compile, execute integration-green: `CREATE INDEX [IF NOT EXISTS]`, `DROP INDEX [IF EXISTS]`, `UPDATE MAPPING ON`, `UPDATE SETTINGS ON [CLOSE]`, `REFRESH`, `WAIT FOR <green|yellow> [ON <idx>]`, `WAIT UNTIL TASK`
- `IF [NOT] EXISTS` markers check live cluster state
- `UNSAFE("...")` and `NO WAIT("...")` justification tokens parse-validated; bare forms reject at parse
- WaitMode enum with `PerStatement` (default), `Off`; scoped implicit waits (per-index) per R-12 (PerMigration deferred to Phase 2 since it depends on cross-statement dirty-index tracking)
- Parse-time syntactic unsafe-op enumeration per R-18
- $body sibling resolution + reserved namespace policy per R-09 (reserved: `$body`, `$query`, `$script`, scope names `env`, `config`, `runtime`, `secrets`)
- R-24b lock contention + crash recovery integration tests pass

### Tasks (subtasks added during execution)

- **1.1** Options + DI extension + `WithProductionDefaults()` (ADR-0012); IConfiguration binding from `Migrations:OpenSearch:*`
- **1.2** `IBootstrapStep` interface + initial steps (RestPing, ClusterHealth, LedgerInit, LockInit) + `OpenSearchBootstrapper` state-machine façade (ADR-0014)
- **1.3** Ledger init step with strict mapping + forensic fields; `AssumeIndicesExist` verification path (ADR-0013)
- **1.4** Lock init step with `number_of_replicas: 0` (ADR-0013, PA-2)
- **1.5** `LockHandle` — CAS acquire, heartbeat renewal loop with TimeProvider, realtime-GET on takeover, `LockMaxLifetime` cancellation contract; lock parameter validation (R-05)
- **1.6** `OpenSearchRecordStore : IMigrationRecordStore` (ADR-0003); ledger CAS write with `?refresh=wait_for`
- **1.7** Full Parlot grammar for foundation verbs (extends spike grammar from 0.5); reserved namespace policy
- **1.8** Statement compilers (AST → IRequest) for foundation verbs
- **1.9** `IF [NOT] EXISTS` live HEAD checks
- **1.10** `UNSAFE` + `NO WAIT` justification tokens; structured WARN log events
- **1.11** WaitMode enum + scoped `ImplicitWaitMiddleware` (R-12)
- **1.12** Parse-time R-18 syntactic unsafe-op enumeration
- **1.13** Startup banner emitting all resolved configuration (R-25)
- **1.14** Integration tests: empty migration end-to-end + R-24b lock contention/crash recovery suite (uses controllable TimeProvider for determinism)

Tag `opensearch/phase-1-foundation` after completion criteria met.

---

## Phase 2: Atomic Operations + Composite + Cross-Cutting

**Goal:** Zero-downtime alias swap reindex pattern works against multi-node cluster. `MIGRATE INDEX` composite verb decomposes correctly with **runtime template lookup** (per ADR-0015). Templates, ISM policies, partial rollback, all cross-cutting safety features land. Multi-node Testcontainers Compose CI integrated.

**Estimated effort:** 2-3 days.

**Completion Criteria:**
- REINDEX with Tasks API polling (R-11); `op_type: create` auto-injection (validated against Phase 0 spike)
- ALIAS SWAP with in-body atomic precondition (R-16, NF-2)
- ALIAS ADD / ALIAS REMOVE
- TEMPLATE / COMPONENT / POLICY / APPLY POLICY verbs
- **MIGRATE INDEX composite (R-30)** — parser produces decomposed AST sequence (CREATE + REINDEX + ALIAS SWAP) with `BodySource = TemplateRef("foo")` for `WITH TEMPLATE`; runtime middleware resolves template body via `GET /_index_template/<id>` immediately before CREATE INDEX dispatch (per ADR-0015 — parser is offline-pure)
- `WHEN VERSION` semver comparator (R-15a) — `'2.9' < '2.10'` correct
- Component-template-aware `dynamic: strict` injection (R-17 — skipped on `composed_of`)
- Hyperbee.Templating four-scope renderer in production path (Phase 0 spike → real wiring)
- `SecretMarker` + `SecretScrubber` log sink wrapper (R-10/R-25 value-coupled redaction)
- `ActiveContext` + `ContextResolutionPolicy` (R-15)
- `WaitMode.PerMigration` implementation (dirty-index tracking + consolidated end-of-migration wait)
- Down direction execution; partial-rollback ledger semantics (R-19) — `status: partially_rolled_back` + `failedStatementIndex`; runner exposes `--force-resume`
- **Multi-node Testcontainers Compose harness** (per A2/A3 — built here, not in Phase 0)
- All R-24c production scenarios pass (15 enumerated tests; see table below)

### R-24c production scenario test table (per A11)

| Test | Description | Phase introducing | Required topology |
|------|-------------|-------------------|-------------------|
| (a) | Zero-downtime alias swap with active background writes | Phase 2 | Multi-node |
| (b) | ISM policy attachment to existing index (`POST /_plugins/_ism/add`) | Phase 2 | Single-node |
| (c) | Mapping update on existing index "no reindex" gotcha + diagnostic warning | Phase 2 | Single-node |
| (d) | Static settings update fails clearly without `CLOSE`, succeeds with it | Phase 1 | Single-node |
| (e) | Reindex of 100K docs streams progress, doesn't time out at HTTP layer | Phase 2 | Single-node |
| (f) | Bulk-load with simulated 429 retries | Phase 3 | Single-node |
| (g) | `dynamic: strict` rejects unexpected fields | Phase 1 | Single-node |
| (h) | Lock false-takeover scenario with simulated refresh-lag | Phase 1 | Single-node |
| (i) | Reindex stale-dst recovery — `op_type:create` skips partial prior-run docs safely | Phase 2 | Single-node |
| (j) | `LockMaxLifetime` cancellation contract — in-flight migration aborts cleanly | Phase 1 | Single-node |
| (k) | Lock primary-shard contention — N concurrent acquires, replicas:0 verified | Phase 1 | Multi-node |
| (l) | Templating JSON-context — `{{#if}}` and `{{each}}` rendering inside JSON | Phase 2 | Single-node |
| (m) | Ledger refresh budget — 100-migration bootstrap completes within budget | Phase 1 | Multi-node |
| (n) | Partial-rollback ledger state — `status: partially_rolled_back` with `failedStatementIndex` | Phase 2 | Single-node |
| (o) | `MIGRATE INDEX` composite produces identical end-state to hand-composed sequence | Phase 2 | Single-node |

### Tasks (subtasks added during execution)

- **2.1** REINDEX verb + Tasks API polling middleware with progress thresholds (R-11; INFO at 10/25/50/75/90%, DEBUG every poll)
- **2.2** ALIAS SWAP with in-body atomic precondition; ALIAS ADD / ALIAS REMOVE
- **2.3** TEMPLATE / COMPONENT / POLICY / APPLY POLICY verbs
- **2.4** `MIGRATE INDEX` composite — parser decomposition + runtime template resolution middleware (per ADR-0015)
- **2.5** WHEN VERSION semver parser + comparator (R-15a)
- **2.6** Component-template-aware `dynamic: strict` injection refinement
- **2.7** Hyperbee.Templating renderer in production path (extends Phase 0 spike); SecretMarker + SecretScrubber + log sink wrapper
- **2.8** ActiveContext + ContextResolutionPolicy (R-15)
- **2.9** WaitMode.PerMigration (dirty-index tracking)
- **2.10** Down direction execution; partial-rollback ledger semantics; runner `--force-resume` flag
- **2.11** Multi-node Testcontainers Compose harness (3 nodes, Compose-style)
- **2.12** R-24c production scenario tests — full 15-test suite per table above

Tag `opensearch/phase-2-atomic-composite` after completion criteria met.

---

## Phase 3: Distribution + Polish

**Goal:** Provider is shippable. SigV4 works on AWS Managed; runner project, samples, multi-topology CI, AWS scheduled validation runbook all in place.

**Estimated effort:** 1-2 days.

**Completion Criteria:**
- Auth: basic, API key, mTLS in core package; SigV4 via opt-in extension
- AWS endpoint loud-fail (R-21); ISM endpoint capability detection
- SigV4 per-request credential resolution (PM-2 mitigation)
- `BulkAllObservable` wrapper with documented defaults (R-20)
- Runner project mirrors existing pattern (R-26)
- Samples project includes all 10 samples per R-27 — featured: `MIGRATE INDEX` composite, `UNSAFE("...")` and `NO WAIT("...")` justification idioms with explicit syntactic enumeration of operations requiring them
- Multi-node Testcontainers Compose CI runs every PR (R-28b Must)
- AWS Managed scheduled validation runbook in repo (R-28c Should); release-checklist line: "AWS validation status documented in README with date of last successful run, OR 'AWS unverified for this release' notice with reason."
- Documentation: README, getting-started guide, **template-propagation FAQ** explicitly answering "how do I apply template changes to existing data?" with `MIGRATE INDEX` as the answer
- ADR compliance audit — verify each of ADR 0001-0015 has either a passing test or doc reference

### Tasks (subtasks added during execution)

- **3.1** Basic auth, API key, mTLS in core package
- **3.2** SigV4 opt-in extension; AWS endpoint loud-fail; ISM endpoint capability detection (R-21); per-request credential resolution
- **3.3** `BulkAllObservable` wrapper with R-20 defaults
- **3.4** `Hyperbee.MigrationRunner.OpenSearch` runner project mirroring existing runner
- **3.5** `Hyperbee.Migrations.OpenSearch.Samples` — all 10 samples; `MIGRATE INDEX` featured
- **3.6** Multi-node Testcontainers Compose CI integration (uses Phase 2 harness from Task 2.11)
- **3.7** AWS Managed scheduled validation runbook (`docs/runbooks/opensearch-aws-validation.md`)
- **3.8** Documentation: README, getting-started, template-propagation FAQ
- **3.9** ADR compliance audit (final regression check, not first-time)

Tag `opensearch/phase-3-shippable` after completion criteria met.

---

## Definition of Done (per phase)

Before tagging a phase snapshot:
- [ ] All phase completion criteria checked
- [ ] All tests green (unit + integration)
- [ ] `dotnet build` clean across all projects
- [ ] No new warnings introduced
- [ ] Plan checkboxes updated for completed tasks
- [ ] Status Summary updated; Learnings appended if applicable
- [ ] ADRs touched by this phase verified against acceptance criteria (per B1 / NF-5)

## Learnings Ledger

(Empty initially. Appended after Reflect surfaces a learning.)

## Status Summary

| Phase | Status | Notes |
|-------|--------|-------|
| 0 — Scaffold + Spike | Not Started | Critical gate; if spike fails, ADR-0011 needs revision and Approach A becomes fallback |
| 1 — Foundation + Foundation Verbs | Not Started | |
| 2 — Atomic + Composite + Cross-Cutting | Not Started | |
| 3 — Distribution + Polish | Not Started | |

**Current task:** Phase 0, Task 0.1 **Done**. Style Reference populated.
**Next action:** Task 0.2 (project scaffolding) — requires git approval to create `devs/bfarmer/provider-opensearch` branch.
**Blockers:** Awaiting user authorization for git operations (branch + commits).

---

## Plan Self-Check

- **Dependencies:** Tasks ordered with blockers first (audit before scaffolding; spike validates before foundation; foundation before composite; composite before distribution).
- **Clarity:** Phase 0 is subtask-detailed; Phases 1-3 are task-level with subtasks expanded by `/nop:implement` at phase start.
- **Vertical slices:** Phase 0 demoable (spike tests pass); Phase 1 demoable (empty migration runs end-to-end); Phase 2 demoable (zero-downtime alias swap test passes); Phase 3 demoable (shippable).
- **ADRs written:** 0001-0015 in `docs/decisions/`; per-phase DoD includes ADR check.
- **Riskiest assumption isolated:** Phase 0's spike is gated by an objective kill criterion; fallback (Approach A) documented if spike fails.
- **R-24c enumerated:** 15-test table specifies which phase introduces each scenario and required topology.
- **Velocity-calibrated:** estimated 3-7 days focused work (1-2 days polish), matching maintainer's actual provider-development pace.
