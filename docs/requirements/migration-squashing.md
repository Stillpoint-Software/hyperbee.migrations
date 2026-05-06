# Migration Squashing for Hyperbee.Migrations

**Status:** Draft (revised for destructive model 2026-05-05)
**Date:** 2026-05-04
**Research:** [docs/research/0005-migration-squashing.md](../research/0005-migration-squashing.md)
**Existing ADRs constraining the design:** ADR-0003 (record store contract), ADR-0009 (convention-based record IDs), ADR-0011 (parser offline / runtime injection — anchor for OpenSearch fusion middleware), ADR-0015 (parser-pure / I/O-runtime split)

## Problem

When a migration list grows long, fresh-environment provisioning slows to the point of noticeably blocking developer flow and CI. EF Core has had an open issue requesting first-class squash since 2014; the .NET ecosystem still lacks a robust answer. Hyperbee.Migrations does not currently have squash support, and the migration-record shape does not yet carry the metadata (per-migration checksum, replaces-graph) that any future squash mechanism requires.

The cost of inaction has two parts. **Now:** consumers who reach the painful migration count (~50+ in Postgres analogues; harder to predict for NoSQL providers) have no first-class story and will work around hyperbee with hand-authored baselines that lose the audit trail. **Future:** retrofitting `Replaces=[…]` and per-record checksums onto an established ledger is significantly harder than landing them today, while the existing record count is small. The Phase 1 scaffolding requirements in this document are no-regrets even if the squash *generator* is years away.

## Requirements

### Theme 1: Core Scaffolding (provider-agnostic)

#### R-01: Migration record carries a content checksum

**Actor:** Hyperbee.Migrations runtime — invoked on every ledger write across every provider
**Intention:**
- *Immediate:* Each ledger row records cryptographic evidence of what was applied
- *Outcome:* Future squash mechanisms can verify "this row really records what we think it records" before transparently marking a squash as applied (R-04)
- *Metric:* 100% of new ledger writes after this lands carry a non-null `Checksum`; squash operations refuse to act against null-checksum history without explicit `--accept-unverified`

**Friction today:**
- Current: `MigrationRecord` is `(Id, RunOn)` only ([`MigrationRecord.cs`](../../src/Hyperbee.Migrations/MigrationRecord.cs))
- Failure mode: A future squash that auto-marks replaced versions as applied has no way to confirm the historical row recorded the migration as authored vs. a hand-edited variant
- Frequency: Every squash release that wants to be safe — discovered only when trust would be required and turns out to be unjustifiable

**Given:** A migration is about to be journaled as applied
**Then:** The record store writes `Checksum = ComputeChecksum(migration)` along with `Id` and `RunOn`. The checksum is a SHA-256 hex over the migration's effective body bytes (per-provider; see Open Question on exact scope)
**Otherwise:** Pre-existing ledger rows written before this requirement landed retain `Checksum = null`; the runner treats null as "pre-checksum era" and tolerates it for already-applied migrations, but never writes new null-checksum rows

**Priority:** Must — every later requirement depends on having checksum data
**Confidence:** High (mechanically straightforward; ADR-0003 contract extension is additive, not breaking)

#### R-02: Migration attribute supports a `Replaces` collection

**Actor:** Migration author writing or generating a squash migration
**Intention:**
- *Immediate:* The author can declare which prior migrations a squash subsumes
- *Outcome:* The runner has the data it needs to apply Django-style auto-mark behavior (R-04)
- *Metric:* A squash migration loaded via reflection exposes a non-empty `Replaces` collection of prior versions

**Friction today:**
- Current: `[Migration(version)]` carries only a version and optional profiles ([`MigrationAttribute.cs`](../../src/Hyperbee.Migrations/MigrationAttribute.cs))
- Failure mode: Without explicit subsumption metadata, a squash is indistinguishable from any other migration; the runner cannot recognize which prior versions are now redundant
- Frequency: Every squash the feature ever produces

**Given:** A migration class declares `[Migration(version, Replaces = new[] { 1000L, 1010L, 1020L })]` (exact attribute shape resolved per Open Question 2)
**When:** `MigrationRunner.DiscoverMigrations` enumerates migrations
**Then:** The discovered descriptor exposes `IReadOnlyList<long> Replaces` with the declared versions; an empty/missing collection means "this is a regular migration, not a squash"
**Otherwise:** Replaces values that are not present in any other discovered migration in the same assembly raise a load-time validation error — the user pointed at a version that doesn't exist

**Depends on:** R-01
**Priority:** Must — the contract surface a squash author writes against
**Confidence:** High (additive attribute parameter)

#### R-03: Runner runs a squash's `UpAsync` only on fresh installations

**Actor:** Hyperbee.Migrations runner — once per migration during reconciliation
**Intention:**
- *Immediate:* Environments that already applied the original chain don't re-run anything; new environments get the fast-path
- *Outcome:* Squashes can ship without coordinating fleets — each environment self-determines whether it needs the squash body
- *Metric:* Zero double-execution incidents (squash body running on an environment that already has the replaced versions in its ledger)

**Friction today:**
- Current: Without squash-aware reconciliation, the only way to ship a squash is to hand-stamp the ledger on every environment (the EF/Rails/Knex/Prisma pattern)
- Failure mode: Manual hand-stamping does not scale and is error-prone in fleets >3 environments
- Frequency: Every squash release in any team operating mixed environments

**Given:** A squash migration M has `Replaces = [V1, V2, V3]` and is queued to run; the ledger contains records for some subset of `{V1, V2, V3}`
**When:** The runner reaches M during reconciliation
**Then:**
- If **all of `{V1, V2, V3}`** are in the ledger: do not run `UpAsync`; the squash is already effectively applied (R-04 follows up to mark it explicitly)
- If **none** are in the ledger: run `UpAsync` normally (fresh install fast-path)
- If a **strict subset** (some but not all) are in the ledger: run `UpAsync` only if every prior original migration that was a Replace target has *also* run individually before reaching this squash; otherwise refuse and surface a clear error (this is the deprecation-window invariant — see R-09)

**Otherwise:** A `SquashCheckException` names the missing or partially-applied versions and tells the operator either to delete the squash or to apply the originals first

**Depends on:** R-02
**Priority:** Must — without this, `Replaces` is a dead attribute
**Confidence:** Medium (the strict-subset path is the subtle case; needs implementation prototyping to validate the spec)

#### R-04: Runner transparently marks a squash as applied when its replaces set is fully in the ledger

**Actor:** Hyperbee.Migrations runner — same reconciliation pass as R-03
**Intention:**
- *Immediate:* A previously-migrated environment's audit trail correctly records "the squash is now your effective state"
- *Outcome:* Subsequent runs (and any `--prune` step in R-15) can rely on squash ledger rows existing across the fleet
- *Metric:* After a single run on an environment that had all replaced versions, the ledger contains both the squash's row *and* the original rows; the squash row's `Checksum` matches the squash's bytes

**Friction today:**
- Current: n/a — squash feature does not exist
- Failure mode: Without auto-marking, R-03's "skip the body" path leaves the ledger out of sync with code reality (squash file exists, no row records it)
- Frequency: Every environment that catches up to a squash release

**Given:** R-03 just decided to skip a squash's `UpAsync` because all `Replaces` versions are in the ledger
**Then:** The runner writes the squash's record to the ledger via the existing `IMigrationRecordStore.WriteAsync` API (with R-01 checksum and R-05 record kind) before continuing; the original Replace targets are *not* deleted from the ledger — the audit trail of original applications is preserved
**Otherwise:** If the write fails, the run fails and surfaces the error — this is not a recoverable mid-state

**Depends on:** R-01, R-02, R-03
**Priority:** Must
**Confidence:** Medium (interaction with provider-specific ledger shapes — Postgres uses two tables, OpenSearch uses an index — needs a per-provider verification pass)

#### R-05: Record kind enum distinguishes Migration / Squash / Baseline

**Actor:** Auditors and operators reading the ledger; future tooling that inspects what's been applied
**Intention:**
- *Immediate:* A ledger reader can tell at a glance which rows are originals, squashes, or hand-authored baselines
- *Outcome:* Audit queries (`how many squashs have we shipped?`, `is this environment past the baseline?`) become trivially answerable
- *Metric:* `MigrationRecord.Kind` is one of `Migration | Squash | Baseline`; default value when absent (pre-this-release rows) is `Migration`

**Friction today:**
- Current: `MigrationRecord` has no kind discriminator
- Failure mode: An auditor cannot distinguish a normal migration row from a row written by R-04's auto-mark
- Frequency: Every audit query against a ledger that has been through a squash

**Given:** A migration record is being written
**Then:** `MigrationRecord.Kind` is set: `Squash` if the migration declares non-empty `Replaces`, `Baseline` if explicitly authored as one (deferred — likely a separate attribute or convention; see Open Question 4), otherwise `Migration`

**Depends on:** R-02
**Priority:** Should — could be deferred until a second squash-using release; not a blocker for the squash itself
**Confidence:** High

### Theme 2: Author Experience

#### R-06: Authors declare data-migration statements as elidable or preserve-verbatim

**Actor:** Migration author writing a migration that performs both schema and data operations
**Intention:**
- *Immediate:* Authors choose explicitly whether each data operation should be carried into squashes verbatim or dropped on the assumption fresh installs don't need the back-fill
- *Outcome:* Squash correctness for data migrations is the author's deliberate choice, not a silent default
- *Metric:* Zero squashes that silently drop data operations the author intended to preserve

**Friction today:**
- Current: n/a — but it's the universal blind spot across every other migration tool except Django
- Failure mode: A squash generator that silently drops `RunPython`-equivalent operations corrupts fresh installs that needed the data. A squash generator that preserves them all carries seed/back-fill operations into envs that don't need them
- Frequency: Every squash that crosses a migration containing data operations

**Given:** A migration contains a data-bearing operation (e.g., OpenSearch `REINDEX`-with-script, Couchbase seed insert, MongoDB validator-driven backfill)
**When:** Squash tooling encounters that statement during fusion or snapshot derivation
**Then:** The author has marked the statement (or the migration as a whole) with one of:
- `elidable: true` — drop on squash; rationale is that rows the operation patches do not yet exist in fresh installs
- `preserve: true` (default for data-bearing ops) — carry verbatim into the squash body; runs on every fresh install
- `replay-on-fresh-only: true` — runs only when the squash's `UpAsync` actually executes (R-03 fresh-install path), never on auto-marked envs

The exact attribution mechanism (per-statement marker in `statements.json` for resource migrations; an attribute on a code-bearing migration class) is per-provider but the three modes are the contract. Default for non-data operations is "carry verbatim"; default for *unmarked* data operations is **refuse to squash** until the author chooses

**Otherwise:** Squash tooling encountering an unmarked data operation surfaces a clear error naming the statement and the three available modes — never silently drops or carries

**Priority:** Must — the universal blind spot deserves explicit handling
**Confidence:** Medium (the mode taxonomy is borrowed from Django; the per-provider attribution mechanism is unspecified at this level)

#### R-07: Squash tooling refuses to fuse migrations that have `DownAsync` overrides without explicit author opt-out

**Actor:** Squash author or squash generator
**Intention:**
- *Immediate:* The honest answer that squashes are Up-only (per industry practice; see research Finding 8) is enforced loudly at squash-creation time
- *Outcome:* Authors don't accidentally lose Down support across a squash boundary
- *Metric:* No squash ships that would silently strip a Down path the author had implemented

**Friction today:**
- Current: OpenSearch is the only provider with formal rollback infrastructure ([`OpenSearchExceptions.cs`](../../src/Hyperbee.Migrations.Providers.OpenSearch/OpenSearchExceptions.cs)); other providers leave `DownAsync` virtual and rarely implement it
- Failure mode: Composing N inverses into a single inverse has no clean answer; pretending otherwise produces a squash that "supports Down" but actually corrupts state on rollback
- Frequency: Every squash that crosses a migration with a real Down implementation

**Given:** Squash tooling is asked to fuse or generate a squash that subsumes one or more migrations with non-no-op `DownAsync`
**When:** The tooling enumerates the candidate migrations
**Then:** The tooling refuses by default and reports which migrations have Downs; the author can opt out with an explicit "I accept that the squash is Up-only and rollback across this boundary requires backup restore" flag (the language is intentionally heavy)
**Otherwise:** With the opt-out, the squash is generated; the resulting squash migration's `DownAsync` is a no-op that throws `RollbackNotSupportedException` with a message naming the underlying restore-from-backup expectation

**Priority:** Must — the alternative is a silent integrity hazard
**Confidence:** High (matches industry practice across Django, Flyway, Prisma)

#### R-08: ~~Original replaced migrations remain in source~~ (DELETED 2026-05-05 — destructive-model reframe)

> **DELETED.** The destructive-model reframe (per ADR-0019) removes original migrations at squash creation rather than retaining them during a deprecation window. The safety net is replaced by the fleet readiness check — see R-15 (promoted to v1 mandatory). Original requirement text retained below for traceability.

#### R-08 (original — retained for traceability)

**Actor:** Project maintainer shipping a squash release
**Intention:**
- *Immediate:* Environments mid-catch-up can run originals that have not yet been observed-applied to fleet members
- *Outcome:* Squashes are deployable without a "stop the world, sync every environment, then ship" protocol
- *Metric:* For any squash release, every Replaces version still has a discoverable original migration in the assembly

**Friction today:**
- Current: n/a — but every tool that lacks this gets it wrong (EF Core's "delete the migrations folder" workflow is the existence proof)
- Failure mode: Deleting originals at squash-creation time strands environments at the un-replaced cut-point with no way forward
- Frequency: Discovered the first time a fleet has a member behind the squash boundary at release time

**Given:** A squash with `Replaces = [V1, V2, V3]` is shipped
**Then:** The originals for V1, V2, V3 remain present in the migration assembly until R-15's audit-aware prune confirms no environment is on the un-replaced path; only then are they archived (not deleted, per R-14)

**Depends on:** R-02, R-14, R-15
**Priority:** Must — this is the precondition for the whole partial-squash model
**Confidence:** High

### Theme 3: Per-Provider Generation Strategy

#### R-09: v1 ships **Postgres only** with destructive squash codegen; v1.1 = Aerospike+MongoDB; v1.2 = Couchbase+OpenSearch (REVISED 2026-05-05 per Assessment 0007)

> **REVISED** per Assessment 0007 IR-CP-4 (Red wins). Original framing (retained below for traceability) assumed multi-provider v1 with universal scaffolding only. The destructive-model reframe + canonicalization-risk analysis (consensus C11) shows OpenSearch + Couchbase High-risk providers should not ship in v1 — production migrations would exercise the canonicalizer for the first time in customer destructive squashes. v1 ships Postgres `PgDumpSnapshotStrategy` only (path-finder); other providers ship `NullSquashStrategy` returning `Unsupported(...)`. v1.1 (~3 months later, gated on Postgres metrics under thresholds): Aerospike (Low risk) + MongoDB (Medium-High). v1.2 (~6 months later): Couchbase + OpenSearch (both High risk). Hand-authoring is NOT a documented fallback (per MD-8 deletion).

#### R-09 (original framing — superseded by revision above):

**Actor:** Project — scope decision for v1 of the squash feature
**Intention:**
- *Immediate:* The v1 generator-target is *no provider* — every provider gets the universal scaffolding (R-01 through R-08) and supports hand-authored squashes identically; per-provider generators ship in Phase 2 in priority order
- *Outcome:* The user-facing experience for authoring a squash is identical across all five providers; only the *generation tooling* varies in availability
- *Metric:* Phase 1 (universal scaffolding) ships supporting hand-authored squashes for Aerospike, Couchbase, MongoDB, OpenSearch, and Postgres simultaneously; Phase 2 ships per-provider strategy implementations starting with Postgres (`PgDumpSnapshotStrategy`), then MongoDB (`IntrospectionSnapshotStrategy`), then OpenSearch (`AstFusionStrategy` or alternate `RestApiSnapshotStrategy`); Couchbase and Aerospike strategies are added when consumer demand justifies

**Friction today:**
- Current: n/a (no squash feature)
- Failure mode: A v1 that privileges any single provider's generator creates asymmetric authoring UX, reinforces the wrong precedent ("squashes are an OpenSearch/Postgres feature"), and risks the design being shaped to one provider's specifics
- Frequency: One scope-decision; the framing here is load-bearing for the entire feature's perceived universality

**Given:** v1 of the squash feature is being scoped
**Then:** v1 ships only the universal scaffolding (Theme 1: R-01 through R-05, plus R-06 elidable contract, R-07 Down refusal, R-08 originals-stay-in-source). No provider-specific generator is part of v1. Phase 2 is sequenced Postgres → MongoDB → OpenSearch → others; each Phase 2 strategy is a separate, independent shipping unit

**Depends on:** R-10 (for the Phase 2 OpenSearch contract surface area, even though OpenSearch is no longer first)
**Priority:** Must — establishes scope and the cross-provider universality stance
**Confidence:** High (revised after user directives 2026-05-04 — "any provider, equally well" + "assess across ALL providers")

#### R-10: OpenSearch squash generation uses AST operation fusion via a middleware extension point

**Actor:** OpenSearch provider — at squash-creation time (build/CI tooling, not runtime)
**Intention:**
- *Immediate:* Fusion takes ASTs, returns ASTs — composable with the existing parser/dispatcher pipeline
- *Outcome:* The fusion logic can be tested independently and reused if squash tooling moves between CLI and CI
- *Metric:* Fusion is a pure function `IList<StatementAst> -> IList<StatementAst>` with no I/O dependencies

**Friction today:**
- Current: n/a; the middleware infrastructure exists but is currently used only for runtime safe-default merging ([`SafeDefaultMergeMiddleware.cs`](../../src/Hyperbee.Migrations.Providers.OpenSearch/Internal/Middleware/SafeDefaultMergeMiddleware.cs))
- Failure mode: Putting fusion in the dispatcher conflates runtime concerns with build-time concerns; putting it in the parser violates ADR-0015 (parser-pure)
- Frequency: Once, at architecture commitment

**Given:** A request to fuse a sequence of OpenSearch migrations into a squash
**When:** The fusion middleware processes the parsed ASTs
**Then:** It applies deterministic fusion rules (R-11) and emits a fused AST list; no network I/O occurs at fusion time (matches ADR-0015 spirit even though fusion runs at build, not parse)
**Otherwise:** Statements that cannot be safely fused (UNSAFE-modified, REINDEX with author-opaque body) pass through untouched

**Depends on:** ADR-0011, ADR-0015
**Priority:** Must
**Confidence:** Medium (the middleware extension pattern is established; the precise placement — `Internal/Middleware/` vs. a new `Internal/Squash/` namespace — is a design call)

#### R-11: OpenSearch fusion implements at minimum the obvious commutative pairs

**Actor:** OpenSearch fusion middleware (R-10)
**Intention:**
- *Immediate:* The common multi-step migration patterns reduce to single statements
- *Outcome:* Real-world OpenSearch migration chains (the 9000-series samples and analogous user code) measurably shrink after squash
- *Metric:* For the existing OpenSearch sample migrations 1000–9001 ([`runners/samples/Hyperbee.Migrations.OpenSearch.Samples/`](../../runners/samples/Hyperbee.Migrations.OpenSearch.Samples/)), fusion produces a measurably smaller statement count (target: at least 30% reduction on synthetic test chains)

**Friction today:**
- Current: n/a
- Failure mode: A fusion implementation that handles only one or two pair types delivers no real value — most chains have multiple kinds of redundancy
- Frequency: Every squash; if the rules are too narrow, users hand-edit the squash output, defeating the point

**Given:** A fusion pass over an AST list
**Then:** The following rules apply (the canonical Django-equivalent set, ported to OpenSearch verbs):
- `CREATE INDEX X` ∘ `UPDATE MAPPING X` (later) → merged `CREATE INDEX X` with the unioned mapping
- `CREATE INDEX X` ∘ `DROP INDEX X` (later) → ∅ (nothing emitted)
- `CREATE TEMPLATE T` ∘ `CREATE TEMPLATE T'` (same name, later) → last write wins
- `CREATE COMPONENT C` ∘ `CREATE COMPONENT C'` (same name, later) → last write wins
- `ALIAS SWAP a→b` ∘ `ALIAS SWAP b→c` → `ALIAS SWAP a→c`
- `WAIT FOR HEALTH`, `REFRESH` between schema ops → droppable

**Otherwise:** Any rule that is not yet implemented yields the originals untouched (graceful degradation; never fuse incorrectly)

**Depends on:** R-10
**Priority:** Should — concrete fusion-rule list; a smaller starter set is acceptable for v1 if the rules above are understood as the target
**Confidence:** Medium (rule correctness needs case-by-case verification; some pairs interact with index settings in non-obvious ways)

#### R-12: Statements modified by `UNSAFE` or carrying `REINDEX` semantics are opaque to fusion

**Actor:** OpenSearch fusion middleware
**Intention:**
- *Immediate:* Operations whose authors have explicitly disclaimed safe defaults are not silently re-ordered or dropped
- *Outcome:* Authorial intent for non-commutative operations survives squash
- *Metric:* Zero fused squashes that re-order or merge statements containing `UNSAFE(...)` or `REINDEX`

**Friction today:**
- Current: n/a
- Failure mode: Fusing two `REINDEX` operations (where the destination of one is the source of another) is correct *only* if the data flow happens to commute — a property fusion cannot statically check
- Frequency: Every chain that does staged reindexing (a common pattern)

**Given:** Fusion encounters a `StatementAst` carrying an `UNSAFE` modifier or a `ReindexAst` node
**Then:** The statement passes through fusion untouched and acts as a barrier — statements before and after it are fused independently within their respective groups, but never across the barrier

**Depends on:** R-10
**Priority:** Must — the alternative is correctness loss
**Confidence:** High (matches OpenSearch grammar's existing `UNSAFE` semantics)

### Theme 4: Verification & Integrity

#### R-13: Squash creation runs round-trip verification before commit

**Actor:** Squash tooling (CLI verb or build target)
**Intention:**
- *Immediate:* The fused/generated squash is proven equivalent to the original chain on at least one ephemeral instance before being committed
- *Outcome:* Authors don't ship a squash whose net effect diverges from the originals
- *Metric:* 100% of squashes committed via the tooling pass round-trip verification; 0% require post-commit fix-up

**Friction today:**
- Current: n/a
- Failure mode: Without verification, a fusion-rule bug or a snapshot-generator omission produces a squash that is silently non-equivalent — discovered weeks later when an environment provisions wrong
- Frequency: Every release where the tooling has a bug

**Given:** A squash has been generated (R-10–R-12 for OpenSearch; hand-authored for other providers)
**When:** The author commits the squash via the tooling
**Then:** The tooling spins up two ephemeral provider containers (reusing [`tests/.../Container/`](../../tests/Hyperbee.Migrations.Integration.Tests/Container/)), applies the original chain to A and the squash to B, captures and compares per-provider state snapshots, and refuses the commit if the comparison diverges
**Otherwise:** The tooling emits a diff naming the divergent state elements; the author sees what fusion missed

**Depends on:** R-09 (and per-provider snapshot capability when extended to other providers)
**Priority:** Must — the integrity story for squashes
**Confidence:** Medium (the per-provider comparison contract is non-trivial — what counts as "equivalent state" is provider-specific)

#### R-14: Replaced migrations are archived, not deleted

**Actor:** Squash pruning tooling (R-15)
**Intention:**
- *Immediate:* Provenance ("when did we add column X?", "what was the rationale for migration 1042?") survives squash
- *Outcome:* Forensic queries against historical migrations remain answerable from the source tree
- *Metric:* Every replaced migration is recoverable from the archive subtree without git spelunking

**Friction today:**
- Current: n/a
- Failure mode: Tools that delete (EF Core's "remove the migrations folder" workflow) lose authorial context permanently; git history alone is insufficient — operators don't `git log --follow` deleted files
- Frequency: Every operator forensic question after a squash

**Given:** A squash has been confirmed safe to prune (R-15) and the replaced originals are about to be removed from the active migrations folder
**Then:** The originals move to a per-assembly `Migrations/_archive/` subtree (or equivalent convention); a manifest at `_archive/INDEX.md` maps each archived file → the squash that subsumed it; the archive is committed in source control

**Depends on:** R-15
**Priority:** Should — the alternative (git history alone) is a worse but acceptable fallback
**Confidence:** Medium (the exact archival mechanism — separate folder vs. git-only via a manifest — is an open question for `/nop:propose`)

#### R-15: Pruning replaced migrations is gated on audit confirmation that no fleet member needs them

**Actor:** Operator running the deprecation tool (`dotnet hyperbee-migrations squash --prune`)
**Intention:**
- *Immediate:* The two-phase deprecation Django leaves to manual discipline is automated
- *Outcome:* Pruning is safe by default; the operator can't accidentally strand an environment
- *Metric:* Zero strandings: every pruned squash has confirmation that all known environments have the squash row in their ledger

**Friction today:**
- Current: n/a
- Failure mode: Pruning before a slow-moving environment catches up turns the slow environment's next deploy into a 404 ("the squash needs migration 1042 which doesn't exist")
- Frequency: Whenever an operator forgets the manual two-phase discipline (i.e., whenever the discipline is required)

**Given:** An operator runs `--prune` against a squash release
**When:** The tool is invoked with a list of known environment ledger sources (connection strings, exported ledger dumps, or a CI-tracked manifest)
**Then:** The tool reads each environment's ledger, confirms every named environment has the squash's record applied, and only then archives the originals (R-14) and strips the `Replaces=[…]` attribute from the squash
**Otherwise:** Any environment missing the squash row aborts the prune with a manifest of unresolved environments — operator either resolves them (by running migrations) or accepts the risk via an explicit `--accept-stranding` flag

**Depends on:** R-04, R-14
**Priority:** Should — the alternative is "operators do this carefully by hand," which Django shows is universally not done carefully
**Confidence:** Medium (the mechanism for "list of known environment ledgers" needs design; could be a simple connection-string list, could be a richer manifest)

#### R-16: Migration directory carries an integrity hash

**Actor:** Squash tooling on the author's side; runner on the consumer's side (optional)
**Intention:**
- *Immediate:* Out-of-band edits to migration files (manual tweaks, accidental git operations, partial squash commits) are detectable
- *Outcome:* The squash's "this is the directory we tested" guarantee survives the trip from author's machine to runner
- *Metric:* The hash detects any single-byte change to any migration file in the assembly

**Friction today:**
- Current: n/a
- Failure mode: A partial squash commit (some files deleted, new file written, but a stale companion file left behind) silently produces a broken state; no other migration tool except Atlas catches this
- Frequency: Any incident where someone hand-edits or partially commits

**Given:** Squash tooling has finished generating a squash
**Then:** A Merkle-style hash over the migration directory contents is computed and stored in the assembly's resources (analogous to Atlas's `atlas.sum`); `dotnet build` includes the hash in the output, and the runner *optionally* verifies it at startup (gated by a config flag, off by default until the ecosystem stabilizes)

**Priority:** Could — Phase 4 enrichment, not gating
**Confidence:** Low (atlas.sum's exact mechanism in Go ports awkwardly to .NET embedded resources; needs spike before commitment)

## Constraints

- **ADR-0003 must remain unbroken.** Any extension to `IMigrationRecordStore` is additive (adding optional methods or new record fields), never breaking. Existing providers continue to compile and run with no squash awareness.
- **ADR-0009 (convention-based record IDs) must remain unbroken.** A squash migration's record ID is derived by the same convention as any other migration; `Replaces` does not change ID derivation.
- **ADR-0011 (parser/runtime split) must remain unbroken.** Fusion runs at build time, not parse time; the parser stays pure.
- **ADR-0015 (parser-offline / I/O-runtime) must remain unbroken.** Fusion is a pure AST transform with no I/O; verification (R-13) is a separate runtime concern with its own clear boundary.
- **Pre-checksum ledger rows (R-01) must continue to be readable.** A `null` checksum means "pre-checksum era" and is tolerated for already-applied migrations; squash operations refuse to act against null-checksum history without an explicit `--accept-unverified` flag.
- **Down support varies by provider.** Hyperbee allows but does not require `DownAsync` overrides; squash tooling must respect this asymmetry (R-07).
- **Sample migrations are small (≤10 per provider).** This feature is forward-looking; targeting users with future migration counts of 50+. Phase 1 scaffolding lands now; Phase 2 generation lands when consumer demand justifies it.

## Trust Boundaries

**Autonomous** (system decides without human):
- Mark a squash as applied (write its ledger row) when all of its `Replaces` versions are present in the ledger (R-04)
- Skip a squash's `UpAsync` when its `Replaces` set is fully present (R-03)
- Refuse to fuse statements with `UNSAFE` or `REINDEX` semantics (R-12)
- Refuse to author a squash that subsumes migrations with `DownAsync` overrides, absent the explicit opt-out (R-07)

**Escalate** (human approves before proceeding):
- Prune replaced migrations from the active source tree (R-15) — operator confirms via the audit-aware tool, never automatic
- Apply squash operations against null-checksum ledger history (R-01) — explicit `--accept-unverified` flag required
- Strand environments via `--accept-stranding` (R-15) — explicit flag, audit log entry on use
- Author-time opt-out for `DownAsync` migrations in a squash (R-07) — explicit flag, the language is heavy

**Forbidden** (never, regardless of context):
- Rewrite an existing ledger row's checksum (R-01) — pre-existing nulls are tolerated; non-null is immutable
- Delete archived migrations (R-14) — archive is append-only from the squash feature's perspective
- Auto-detect squash opportunities and ship them without author authorship (out of scope)
- Apply runtime fusion to user-authored migrations on the hot path (parser/dispatcher fusion at runtime is explicitly *not* the design — fusion is a build-time tool)

## Out of Scope

- **`DownAsync` of a squash itself.** A squash migration's `DownAsync` throws `RollbackNotSupportedException`. Rollback across a squash boundary requires backup restore. Industry standard; see research Finding 8.
- **Cross-assembly squashes.** v1 supports squash within a single migration assembly only. Cross-assembly composition is fragile (Django squash is per-app for the same reason); deferred until a concrete consumer demand emerges.
- **Snapshot-based generation for non-OpenSearch providers (Phase 3).** Postgres/Mongo/Couchbase/Aerospike consumers can hand-author squashes using the Theme 1 scaffolding; per-provider snapshot mechanisms are deferred to a later research-and-propose pass.
- **Aerospike squash generation.** Aerospike's schema is so thin (namespaces + sets + secondary indexes) that the tooling investment may not be justified at any scale. Likely permanent scope exclusion; revisit only if a concrete user need surfaces.
- **Auto-detection of squash opportunities.** The tool fuses or generates only when an author asks. No "you've got 100 migrations, here's a suggested squash" advisor.
- **Squash of squashes (re-squashing).** Django explicitly forbids re-squashing a still-`replaces`-bearing migration. Hyperbee follows the same rule: a squash's `Replaces` set is final until the squash is itself pruned (R-15), at which point the squash becomes a regular `Migration` and can be subsumed by a new squash.
- **Runtime-side fusion** (collapsing migrations at apply time). Fusion is build-time only; the runtime applies what's on disk.

## Decisions & Open Questions

### Decided

- **Up-only** — Rollback across a squash boundary requires backup restore. Matches industry practice (Django, Flyway, Prisma all Up-only for squashes/baselines). Composing N inverses has no general clean answer. *Influences:* R-07, Out-of-scope, Trust Boundaries.
- **Django-style `Replaces=[…]` is the canonical partial-squash mechanism** — Per research Finding 2, the only design idea in the field that doesn't require hand-syncing every environment. *Influences:* R-02, R-03, R-04.
- **Per-migration checksum (Phase 1 scaffolding) lands before squash tooling** — Adopting now while ledger rowcount is small is much cheaper than retrofitting later. R-01 ships independently of any generator. *Influences:* R-01, R-02, R-13, R-15.
- **OpenSearch is the v1 generator target** — Per research Finding 7, the only provider with an AST that makes Django-style fusion feasible. Other providers can hand-author baselines using the scaffolding. *Influences:* R-09–R-12; defers Phase 3 snapshot generation.
- **Data migrations require explicit author marking** — Default for unmarked data ops is "refuse to squash" until the author chooses elidable / preserve / replay-on-fresh-only (Django's pattern, plus a third mode). The universal blind spot deserves explicit handling, not a silent default. *Influences:* R-06.
- **Replaced files archive (not delete) on prune** — Provenance preservation matters; git history alone is insufficient because operators don't `git log --follow`. *Influences:* R-14, R-15.
- **Round-trip verification before squash commit is mandatory** — The integrity story for squashes; bug-finding mechanism for fusion rules. *Influences:* R-13.
- **Sample migrations remain small (≤10 per provider) as the realistic short-term target** — Squash is forward-looking infrastructure; designing for hypothetical massive migration counts is over-engineering. Phase 1 lands now; Phase 2 ships when consumer demand justifies it. *Influences:* prioritization (R-09 pushes to "Must" for v1; later phases are deferred).

### Open

1. **Attribute shape: extend `[Migration]` or introduce `[RollupMigration]`?**
   - Status: exploring
   - Reason: Extension is simpler to discover; new attribute is cleaner separation. Trade is parser/runner complexity vs. authorial clarity.
   - Leaning: Extension via `[Migration(version, Replaces = new[]{…})]` — fewer surface-area changes; squashes are a kind of migration, not a separate concept. *Guess.*
   - Depends on: `/nop:propose` evaluation against ADR-0009 (record-ID derivation) and existing attribute usage.
   - Influences: R-02, R-05, R-06.

2. **Exact checksum scope per provider** (R-01)
   - Status: uncertain
   - Reason: Each provider's "the bytes that drive `UpAsync`" is different — OpenSearch is `statements.json`, Postgres is the SQL file, code-bearing migrations need IL or source hashing.
   - Leaning: Provider-supplies-the-hash via an extension to `IMigrationRecordStore` or a new `IChecksumStrategy<TMigration>`. Default strategy: SHA-256 over the migration's *resource* contents for resource-based migrations; reflection-based name+version-only fallback for code-only migrations (acknowledging the latter is weaker).
   - Depends on: `/nop:propose` per-provider evaluation.
   - Influences: R-01, R-13, R-15.

3. **Profiles interaction with `Replaces`** (R-02)
   - Status: deferred
   - Reason: A squash with `Profiles=["prod"]` replacing migrations with mixed profiles is ambiguous. Refuse, fuse only same-profile, or carry the union?
   - Leaning: Refuse; surface a load-time error if the squash's profiles don't match every replaced migration's profiles. *Guess; could be wrong if cross-profile squash is a real use case.*
   - Depends on: User input; current sample migrations don't exercise profiles much.
   - Influences: R-02, R-03.

4. **Baseline as a separate concept vs. a squash with `Replaces` over the whole prior history** (R-05)
   - Status: exploring
   - Reason: A "baseline" migration is conceptually "the state at version N" — equivalent to a squash whose `Replaces` lists every prior migration. Worth modeling as distinct, or just a special case?
   - Leaning: Model as a distinct kind via `RecordKind.Baseline`; Authors signal with `[Migration(version, IsBaseline = true)]` or a separate attribute. Distinct kind is useful for audit queries even if mechanically similar.
   - Depends on: `/nop:propose`.
   - Influences: R-05.

5. **Cross-assembly squash support — permanently out of scope or v2 candidate?**
   - Status: deferred
   - Reason: Django squash is per-app for a reason; cross-app squashing is fragile. But hyperbee's assembly model isn't quite the same as Django's app model.
   - Leaning: v1 single-assembly only; revisit only if a concrete consumer asks. Documented as out-of-scope; not a permanent prohibition.
   - Influences: Out-of-scope.

6. **Aerospike squash generation — invest in tooling or leave to hand-authored baselines?**
   - Status: deferred
   - Reason: Aerospike's schema is so thin that hand-authored baselines are a reasonable steady state.
   - Leaning: Permanent scope exclusion. Document as "use hand-authored baselines for Aerospike."
   - Influences: Out-of-scope.

7. **The audit source for R-15 (`--prune`'s "list of known environment ledgers")**
   - Status: uncertain
   - Reason: How does the operator hand the tool a list of environments? Connection strings (sensitive), exported ledger dumps (snapshot drift), CI-tracked manifest (overhead)?
   - Leaning: Support all three formats; CI-tracked manifest as the recommended default because it's least-trust (no live credentials needed).
   - Depends on: `/nop:propose`.
   - Influences: R-15.

8. **R-16 directory hash — implement now or defer to Phase 4?**
   - Status: deferred
   - Reason: `atlas.sum`'s exact mechanism is non-trivial in .NET embedded-resource contexts; getting it wrong is worse than not having it.
   - Leaning: Defer to Phase 4 enrichment; v1 lives without it. The `Could` priority on R-16 reflects this.
   - Influences: R-16.
