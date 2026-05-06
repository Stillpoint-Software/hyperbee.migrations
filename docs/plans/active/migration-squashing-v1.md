# Plan: Migration Squashing v1 (Postgres + Universal Scaffolding + Script-Format)

**Status:** Active
**Created:** 2026-05-05
**Branch:** `devs/bfarmer/migration-squashing-v1` (to be created in Phase 0)

**Inputs:**
- Requirements: [docs/requirements/migration-squashing.md](../../requirements/migration-squashing.md)
- Design (canonical): [docs/design/migration-squashing.md](../../design/migration-squashing.md)
- Consensus + hardening: [docs/design/migration-squashing-consensus-destructive.md](../../design/migration-squashing-consensus-destructive.md)
- Research: [0005](../../research/0005-migration-squashing.md), [0006 (historical, additive)](../../research/0006-migration-squashing-assessment.md), [0007 (destructive, drives this plan)](../../research/0007-migration-squashing-destructive-assessment.md)
- EF Core reference: [docs/research/ef-core-squash-reference.md](../../research/ef-core-squash-reference.md)
- Implementation examples: [Postgres path-finder](../../design/migration-squashing-example-postgres.md), [Aerospike](../../design/migration-squashing-example-aerospike.md), [OpenSearch](../../design/migration-squashing-example-opensearch.md), [MongoDB](../../design/migration-squashing-example-mongodb.md), [Couchbase](../../design/migration-squashing-couchbase-example.md)
- ADRs: 0019 (squash mechanism + 19 amendments), 0020 (up-only), 0021 (checksum + 2 amendments), 0022 (script-format resources, NEW)

## Velocity calibration

This plan is sized to the maintainer's actual velocity:
- Aerospike provider (with auto-renewing lock + Parlot grammar) shipped in **1 day**
- Couchbase provider (most complex, 7-state bootstrapper + N1QL grammar) shipped in **under 1 week**
- OpenSearch provider (richest grammar, 21-statement AST, full hardening) shipped in **~3 weeks**

Squash v1 is bigger than any single provider — universal core scaffolding + Postgres codegen + cross-provider script-format work + CLI. Realistic estimate: **4-6 weeks** of focused work over phases.

The plan uses **vertical slices**: each phase is independently demoable + testable. Phases 0-3 ship on their own as ledger-scaffolding-only infrastructure (no squash CLI, no codegen) — useful even before Postgres codegen lands.

## Backward Compatibility — v3.0 release with DIM defaults

This feature ships as **Hyperbee.Migrations v3.0** (current is v2.x). The squash work introduces one breaking surface (`IMigrationRecordStore` gains methods) and several additive surfaces (additional `MigrationRecord` properties, `MigrationAttribute` parameters, new types). Strategy:

### Major version bump (v3.0)

- `version.json` bumps from 2.x to 3.0 at v1 release. Semver does the work: consumers see "v2 → v3" and read the migration guide.
- Per-package release notes flag the breaking surface explicitly.
- v2.x branch remains in maintenance mode for security patches; new feature work lives on v3.

### Default Interface Methods on `IMigrationRecordStore`

The three new methods (`WriteAsync(MigrationRecord, WritePrecondition, ct)`, `LoadAppliedVersionsAsync(candidateIds, ct)`, `LoadSatisfyingRowsAsync(versions, ct)`) ship with **safe DIM implementations** that delegate to the existing v2 methods. Custom record-store implementations work unchanged but get degraded behavior:

- No write-time `Kind`/`Replaces` integrity check (per ADR-0021 A1)
- No realtime-bulk-read optimization (falls back to per-id `ExistsAsync` loop)
- No re-squash transitivity (mature envs that auto-marked an inner squash will fail with `MidRangeSquashException` against an outer squash)

The 5 shipped providers (Postgres, Aerospike, Couchbase, MongoDB, OpenSearch) all override the DIM defaults with proper implementations during Phase 1 + Phase 3. Custom record stores opt into squash support by overriding; opt out by doing nothing.

### Schema migrations are non-breaking

Provider record-store schema changes are additive and idempotent:

- **Postgres:** `ALTER TABLE hyperbee.migrations ADD COLUMN ... IF NOT EXISTS` for `checksum text NULL` and `kind smallint NOT NULL DEFAULT 0` plus `CHECK (kind IN (0,1,2))`. Pre-existing rows read clean (`Checksum=null, Kind=Migration`).
- **Aerospike, Couchbase, MongoDB:** sparse-bin / JSON document; absent fields read as null.
- **OpenSearch:** ledger index mapping additive `PUT _mapping`. ADR-0018 lock+ledger split unchanged.

### Squash is operationally one-way

Once a squash is committed, the original migration source files are removed (per ADR-0019 destructive model). **Rolling back hyperbee.migrations to v2 against a squashed ledger is not supported.** The migration guide documents this clearly. Operators who need to rollback after squashing must restore the database from a backup taken before the squash ran.

### Mixed-version fleet hazard during rollout

Running a v2 app and a v3 app against the same ledger simultaneously is hazardous in one specific scenario: if v3 has emitted a squash row but the v2 app's source tree predates the squash (still has originals), v2 will see "missing migrations" against a partially-applied ledger. Mitigated by ADR-0019 A2 (two-phase fleet readiness gate — v3 records `expected-fleet-versions` in the squash artifact; runner refuses on stale envs at deploy time). Operators must roll out v3 to all envs before squashing.

### Migration guide ships in Phase 8

`docs/guides/upgrading-from-v2.md` covers schema migration, custom record-store override recipes, the one-way squash property, and the mixed-fleet hazard. See Phase 8 Task 8.7.

---

## Objective

Ship the v1 destructive-model migration-squash feature satisfying:

- Universal scaffolding for all 5 providers (Replaces graph, MigrationRecord checksum, MigrationRecordKind, runner reconciliation with re-squash transitivity, MigrationLedgerIntegrityException, MigrationApplyMode)
- Postgres v1 codegen (`PgDumpSnapshotStrategy` with all hardening per ADR-0019 amendments)
- Script-format resource support across **all 5 providers** per ADR-0022 (backward-compatible with existing `.statements.json`)
- `dotnet hyperbee-migrations squash` CLI verb with fleet readiness check
- `recover from-mid-range` subcommand with deterministic token gate
- Verification round with snapshot A caching + parallel A/B capture + container lifecycle on failure
- Generation determinism CI gate (C12)
- ITopologySignature with schema versioning per A14
- All 9 P0 + 10 P1 amendments from Assessment 0007

**Non-goals (deferred to v1.1 / v1.2):**
- Aerospike `InfoSnapshotStrategy` (v1.1)
- MongoDB `IntrospectionSnapshotStrategy` (v1.1)
- Couchbase `HybridStrategy` (v1.2)
- OpenSearch `RestStateDiffStrategy` (v1.2)
- `--seal-history` retroactive checksum tool (Phase 2)

## Style Reference

Citations follow the OpenSearch-provider plan precedent (≥10 file:line refs across patterns).

### Pattern 1 — Provider record store extension (ADR-0003, ADR-0021 + A1)

- **Existing record-store shape:** [PostgresRecordStore.cs](../../../src/Hyperbee.Migrations.Providers.Postgres/PostgresRecordStore.cs) — two-table model (ledger + lock) with `WriteAsync(MigrationRecord)`/`ReadAsync(id)`/`DeleteAsync(id)`/`ExistsAsync(id)`/`InitializeAsync()`/`CreateLockAsync()`. The record-store contract is provider-native; the shape varies (relational table vs JSON document) but the methods are uniform per ADR-0003.
- **Additive extension pattern:** Aerospike, Couchbase, MongoDB, OpenSearch all use document-shaped storage; adding `Checksum` (string) + `Kind` (small int / enum) bins/fields is mechanically trivial. Postgres adds two columns via idempotent `ALTER TABLE` in `CreateMigrationTable()`. **All five providers use identical contract surface; storage shape varies.**
- **Read-time integrity check:** new — every record store enforces `Kind == Squash ⟺ Replaces non-empty` on read; throws `MigrationLedgerIntegrityException` on mismatch. Per ADR-0021 A1.

### Pattern 2 — Multi-statement Parlot grammar lift (ADR-0001, ADR-0022)

- **Statement-by-statement grammar:** [OpenSearchStatementParser.cs](../../../src/Hyperbee.Migrations.Providers.OpenSearch/Internal/Grammar/OpenSearchStatementParser.cs) — already statement-by-statement; entry point parses one statement string. Lifting to multi-statement script entry adds ~10 lines of grammar (top-level `script` rule consuming whitespace, comments, statement, semicolon, repeat).
- **Existing comment patterns:** OpenSearch grammar handles inline `--` line comments. ADR-0022 universalizes to `--`/`//`/`/* */` across all 4 NoSQL providers.
- **Body-source delegation:** [OpenSearchStatementParser.cs body grammar at lines 141-170](../../../src/Hyperbee.Migrations.Providers.OpenSearch/Internal/Grammar/OpenSearchStatementParser.cs#L141-L170) — Forms 1/2/3 from ADR-0017 unchanged in script form; Form 4 (inline brace-balanced `WITH BODY {...}`) and `BODIES` header are additions.

### Pattern 3 — Reflection-based discovery + attribute extension (ADR-0004, ADR-0019 attribute amendments)

- **Existing discovery:** [MigrationRunner.cs:160-189](../../../src/Hyperbee.Migrations/MigrationRunner.cs#L160-L189) — reflection over `Assemblies`, projects `[Migration]` attribute metadata, sorts by `Version` per `Direction`. Adding `Replaces` (long[]) + `ReplacesRange` (string) parameters is trivial; resolution of `ReplacesRange` to a sorted version set happens at discovery time.
- **Validation at discovery:** new — every value in resolved `Replaces` must point to a discovered migration descriptor (or be a load-time error per ADR-0019 A2 / R-02).

### Pattern 4 — DI registration with composite descriptor (ADR-0006, ADR-0019 A11)

- **Existing two-overload entry:** [Postgres ServiceCollectionExtensions.cs](../../../src/Hyperbee.Migrations.Providers.Postgres/ServiceCollectionExtensions.cs) — `AddPostgresMigrations(...)` registers options, record store, runner, resource runner. v1 adds `ISquashStrategy` registration; for Postgres = real strategy, for others = `NullSquashStrategy` returning `Unsupported`.
- **Composite descriptor pattern:** new — `ISquashStrategy` registration takes a single composite of (`ITopologySignature`, `IDataOpClassifier`, `ISquashGenerator`, `ISquashVerifier`, `ISnapshotCanonicalizer`). `NotImplementedException` from any component fails registration validation.

---

## Phases

| Phase | Theme | Vertical slice | Demo gate |
|---|---|---|---|
| **0** | Foundation + Infrastructure | Test fixtures, branch, codebase audit, no breaking changes | Existing 75/75 OpenSearch + provider unit tests still green |
| **1** | Universal Ledger Scaffolding | MigrationRecord + MigrationRecordKind + Checksum + integrity exception across all 5 providers | Existing migrations work; new fields readable; integrity check refuses tampered rows |
| **2** | MigrationAttribute + Discovery | `Replaces`, `ReplacesRange`, load-time validation, MigrationApplyMode | New `[Migration(Replaces=...)]` declared migrations discoverable; invalid refs fail loudly |
| **3** | Universal Reconciliation Logic | Auto-mark / fresh-install / mid-range branches; re-squash transitivity; LoadAppliedVersionsAsync realtime obligation | Mature env auto-marks a hand-authored squash; fresh env runs body; mid-range raises `MidRangeSquashException` |
| **4** | Script-Format Resource Support | ADR-0022 — multi-statement script grammar lift across all 4 NoSQL providers; Postgres `.sql` aliased | Existing JSON-array migrations still work; new `.statements` script files parseable for all 5 |
| **5** | ISquashStrategy Contract + NullSquashStrategy | Composite descriptor; ITopologySignature with schema versioning; IDataOpClassifier + non-determinism scan; per-provider Null implementations | All 5 providers register; squash CLI invocation against non-Postgres returns clean refusal (not ServiceNotFound) |
| **6** | Postgres v1 Squash Generator | `PgDumpSnapshotStrategy` (path-finder) with statement classifier, server-version-matched container, post-processing pipeline, sequence setval, CONCURRENTLY stripping | End-to-end: real Postgres squash generates valid `Squash_M.sql` + summary that passes verification round |
| **7** | Squash CLI + Fleet Readiness + Verification | `squash` verb; fleet manifest schema; `--squash-overrides` structured fields; two-phase fleet gate; verification with snapshot A caching + parallel A/B + container lifecycle | Operator runs full workflow end-to-end against a sample Postgres app |
| **8** | Recovery + Determinism CI + Docs | `recover from-mid-range` subcommand with deterministic token; C12 generation determinism CI; operator guide; CHANGELOG | All P0+P1 amendments verified by automated tests; release-ready |

Phases are **sequential at the gate level** but tasks within a phase can be parallelized when they touch independent providers.

---

## Phase 0 — Foundation + Infrastructure

**Objective:** Audit existing code; create branch + plan snapshot tag; ensure no breaking changes baseline; set up squash-specific test fixtures.

### Task 0.1 — Codebase audit ☑

Read each provider's record store + runner and confirm the existing surface meets the assumptions in the design:

- [x] `MigrationRecord` is an immutable record with `Id` + `RunOn` only (no checksum, no kind)
- [x] `IMigrationRecordStore` method count audit — **DIVERGENCE FOUND:** 6 methods, not 5. Plus `WriteAsync(string)` not `WriteAsync(MigrationRecord)` — Phase 1 must extend contract. See Audit Appendix below.
- [x] `MigrationRunner.DiscoverMigrations()` reflection respects `[Migration]` attribute Version + Profiles
- [x] All 4 NoSQL providers use `*ResourceRunner.StatementsFromAsync()` for `statements.json` resources
- [x] Postgres uses `PostgresResourceRunner.AllSqlFromAsync()` for `*.sql` resources

**Output:** Audit Appendix written below the Status section, with file:line refs for all 9 assumptions. One real divergence (A2.1) drives a Phase 1 task amendment; all other assumptions confirmed.

### Task 0.2 — Branch + snapshot ☑

- [x] Branch `devs/bfarmer/provider-squash` created from main (per user — branch name differs from plan's draft `devs/bfarmer/migration-squashing-v1`)
- [x] Tag `migration-squashing-v1-baseline` placed at main HEAD `e7a099e` before any squash work
- [x] Baseline test suite confirmed green: 356/356 unit tests pass on net10. Build clean (0 errors, 36 pre-existing warnings out of scope). Integration tests not re-run (require Docker; out-of-scope for Phase 0 baseline confirmation).

### Task 0.3 — Test fixtures for squash ☑

- [x] New test project `Hyperbee.Migrations.Squash.Tests` skeleton created (csproj + assembly init scaffold + per-provider stub test classes)
- [x] Reuses existing Testcontainers infrastructure via project reference
- [x] Compiles + 0 tests skip (skeleton has no test methods yet; tests fill in during phases 1-3)

**Phase 0 Completion Criteria:**
- [x] Audit appendix written with file:line refs
- [x] Branch created, baseline tagged
- [x] Test fixture skeleton compiles
- [x] Existing test suite still green (356/356 unit; integration deferred)

---

## Phase 1 — Universal Ledger Scaffolding

**Objective:** Extend `MigrationRecord` with `Checksum` + `Kind`; add `MigrationLedgerIntegrityException`; ensure all 5 provider record stores read+write the new fields additively. **No squash logic yet** — this phase ships ledger field plumbing only; existing migrations and runtimes work unchanged.

**ADR compliance:** ADR-0003 (additive contract), ADR-0021 (with A1 + A2 amendments).

### Task 1.1 — Core types + DIM defaults on `IMigrationRecordStore`

- [ ] `MigrationRecord` gains `string? Checksum` + `MigrationRecordKind Kind` properties (init-only; existing constructors unchanged)
- [ ] `MigrationRecordKind` enum: `Migration = 0`, `Squash = 1`, `Baseline = 2`
- [ ] `MigrationLedgerIntegrityException` (new) — derives from a core-level base provider exception type; thrown by record stores on Kind/Replaces inconsistency per ADR-0021 A1
- [ ] **`IMigrationRecordStore` gains three new methods, each with DIM (default interface method) implementations** that preserve v2 behavior for custom implementations:
  - `Task<WriteOutcome> WriteAsync(MigrationRecord record, WritePrecondition precondition, CancellationToken ct = default)` — DIM default: ignore precondition + checksum/kind; delegate to existing `WriteAsync(record.Id)`; return `WriteOutcome.Created`
  - `Task<IReadOnlySet<string>> LoadAppliedVersionsAsync(IEnumerable<string> candidateIds, CancellationToken ct = default)` — DIM default: per-id `ExistsAsync` loop (degraded; no realtime-bulk-read optimization)
  - `Task<IReadOnlySet<long>> LoadSatisfyingRowsAsync(IEnumerable<long> versions, CancellationToken ct = default)` — DIM default: only direct id matches (no transitive squash satisfaction; mature envs that auto-marked an inner squash will fail `MidRangeSquashException` against an outer squash if their custom store hasn't overridden this)
- [ ] `IMigrationRecordStore.WriteAsync` contract (new overload) documented to enforce `Kind == Squash ⟺ Replaces non-empty` at write time — read+write enforcement
- [ ] XML doc on each new DIM method explicitly notes "shipped providers override; custom implementations should override for full squash support; default behavior preserves v2 semantics"

**Cross-provider participation check:** No provider-specific code; pure core types. The 5 shipped providers override the DIM defaults in Tasks 1.2-1.6 + Phase 3 Task 3.1/3.3. ✓

**Backward compatibility:** Custom `IMigrationRecordStore` implementations compile and run unchanged on v3.0; they receive safe-but-degraded behavior until they opt in by overriding the DIM defaults. See top-of-plan Backward Compatibility section.

### Task 1.2 — Postgres record store update

- [ ] `PostgresRecordStore.CreateMigrationTable()` adds idempotent `ALTER TABLE ... ADD COLUMN IF NOT EXISTS checksum text NULL`, `kind smallint NOT NULL DEFAULT 0`, `CHECK (kind IN (0,1,2))`
- [ ] `WriteAsync` populates `Checksum` + `Kind`; enforces consistency rule (raises `MigrationLedgerIntegrityException` on `Kind == Squash` with empty `Replaces` etc.)
- [ ] `ReadAsync` returns full `MigrationRecord`; raises `MigrationLedgerIntegrityException` on inconsistent rows
- [ ] Pre-existing rows with null Checksum + Kind=0 read clean

**Tests:**
- [ ] Existing migrations apply; checksum populated
- [ ] Pre-checksum-era row read clean
- [ ] Inconsistent row (kind=Squash, Replaces=[]) raises integrity exception
- [ ] Inconsistent row (kind=Migration, Replaces=[1000,1010]) raises integrity exception

### Task 1.3 — Aerospike record store update

- [ ] `AerospikeRecordStore` writes `Checksum` + `Kind` bins; enforces consistency
- [ ] Pre-existing records (sparse bins, missing Checksum) read clean
- [ ] Same test matrix as Postgres

### Task 1.4 — Couchbase record store update

- [ ] `CouchbaseRecordStore` JSON document gains `Checksum` + `Kind` fields; additive
- [ ] Same test matrix

### Task 1.5 — MongoDB record store update

- [ ] `MongoDBRecordStore` document additive update
- [ ] Same test matrix

### Task 1.6 — OpenSearch record store update

- [ ] `OpenSearchRecordStore` ledger index mapping gets `Checksum` (keyword) + `Kind` (byte) — additive per ADR-0018 split-ledger-and-lock-indices invariant
- [ ] Same test matrix
- [ ] **Cross-index hazard check:** ADR-0018 split means lock and ledger live in separate indices. Confirm new fields go to ledger only; lock unchanged.

### Task 1.7 — Default IChecksumStrategy implementations

- [ ] `IChecksumStrategy<TMigration>` interface (new) — single method `Task<string> ComputeAsync(IMigration migration, CancellationToken)`
- [ ] Default implementations per ADR-0021:
  - **Resource-based migration:** SHA-256 over concatenated, sorted-by-name resource bytes
  - **Code-only migration (fallback):** SHA-256 over `(typeof(migration).FullName ‖ migration.Version)` bytes — documented-weaker
- [ ] Wired into `MigrationRunner.WriteRecordAsync()`

**Phase 1 Completion Criteria:**
- [ ] All 5 provider record stores updated with consistency checks
- [ ] Default checksum strategies wired
- [ ] Existing test suite still green (no migrations broken)
- [ ] Per-provider integrity exception tests pass
- [ ] Pre-checksum-era ledger rows read clean across all providers

**Demo:** Run a normal migration on each provider; verify ledger row has populated Checksum + Kind=Migration. Manually corrupt a row to Kind=Squash with empty Replaces; verify next read raises `MigrationLedgerIntegrityException`.

---

## Phase 2 — MigrationAttribute + Discovery

**Objective:** Extend `[Migration]` with `Replaces` + `ReplacesRange`; load-time validation; `MigrationApplyMode` enum + `MigrationContext` extension. Ship without reconciliation logic — discovery only.

**ADR compliance:** ADR-0004 (reflection-based discovery), ADR-0009 (record IDs), ADR-0019 amendments A6 (transitivity rule capture, runtime in Phase 3).

### Task 2.1 — Attribute extension

- [ ] `MigrationAttribute` gains `long[] Replaces { get; init; }` + `string ReplacesRange { get; init; }`
- [ ] Both default empty/null; existing `[Migration(version)]` declarations unaffected
- [ ] `XML <summary>` doc updates per existing convention

### Task 2.2 — ReplacesRange parsing

- [ ] Parlot grammar (or simple parser — single rule) for `"1000-1500, 1700, 1800-1850"` syntax
- [ ] Resolves at discovery time against assembly's actual `[Migration]` versions in inclusive range
- [ ] Single resolved sorted version set per migration; combinable with `Replaces` array
- [ ] Empty `Replaces` AND empty `ReplacesRange` → migration is regular (not a squash)

### Task 2.3 — Discovery validation

- [ ] `MigrationRunner.DiscoverMigrations()` validates each squash-shaped migration's resolved Replaces set:
  - Every value resolves to a discovered migration descriptor in the assembly OR an existing ledger row (deferred to Phase 3 reconciliation; load-time check is assembly-only per ADR-0019 A2)
  - No self-reference (`v ∉ Replaces` for migration's own Version)
  - No duplicates (normalized to set; warning if dedup occurred)
- [ ] Failure raises `MigrationLoadException` naming missing version + the rollup that requires it (per ADR-0019)

**Cross-provider participation check:** Pure core; no provider-specific code. ✓

### Task 2.4 — `MigrationApplyMode` + `MigrationContext`

- [ ] `MigrationApplyMode` enum: `Fresh`, `PartialCatchUp`
- [ ] `MigrationContext.ApplyMode` property; `MigrationContext.IsFreshInstall` back-compat sugar (returns `ApplyMode == Fresh`)
- [ ] Runner sets `ApplyMode` before invoking `UpAsync` based on reconciliation classification

**Cross-provider participation check:** Pure core; runner sets mode regardless of provider. ✓

**Phase 2 Completion Criteria:**
- [ ] Attribute extension shipped + tested
- [ ] `ReplacesRange` resolves correctly for ranges, singletons, mixed
- [ ] Self-reference / duplicate / missing-version load-time errors fire
- [ ] `MigrationApplyMode` available to all migration `UpAsync` callers
- [ ] Existing migrations (no Replaces) continue to discover and run unchanged

**Demo:** Author writes `[Migration(2000, ReplacesRange = "1000-1140")]` migration class; load-time validation passes when versions exist; fails loudly with named missing version when one is missing.

---

## Phase 3 — Universal Reconciliation Logic

**Objective:** Implement runner-side reconciliation with auto-mark / fresh-install / mid-range branches; `LoadAppliedVersionsAsync` realtime obligation per provider; re-squash transitivity rule.

**ADR compliance:** ADR-0019 (especially A6 transitivity, A17 Kind/Replaces consistency), C2 verification (deferred to Phase 7).

### Task 3.1 — `LoadAppliedVersionsAsync` realtime per provider

Add `IMigrationRecordStore.LoadAppliedVersionsAsync(IEnumerable<string> candidateIds, CancellationToken ct) → IReadOnlySet<string>` returning the subset present in the ledger.

Per-provider implementation per consensus C6 + C9 + Round 1b:
- [ ] **Postgres:** single `SELECT record_id FROM ledger WHERE record_id = ANY($1)`
- [ ] **Aerospike:** `BatchGet` with strong consistency
- [ ] **Couchbase:** `MultiGet` with mutation token consistency (`ScanConsistency.RequestPlus` semantics for any subsequent N1QL queries)
- [ ] **MongoDB:** `find({_id: {$in: ids}})` with `ReadConcern.Majority` + `ReadPreference.Primary` on RS, `ReadConcern.Local` on standalone (topology-aware)
- [ ] **OpenSearch:** `_mget` with realtime=true (NOT `_search`); ADR-0018 split — reads ledger index only

**Cross-provider participation check:** Each provider implements per its native realtime primitive. The contract surface is uniform; the implementation varies. ✓

### Task 3.2 — Reconciliation pseudocode in `MigrationRunner`

Per ADR-0019 amended pseudocode:

```csharp
// For each discovered migration with non-empty resolved Replaces
var replacedIds = squash.Replaces.Select(IdFor);
var satisfied = await store.LoadSatisfyingRowsAsync(replacedIds, ct);
// LoadSatisfyingRowsAsync: returns versions where row.Kind == Migration AND row.Id == version
//                        OR row.Kind == Squash AND version ∈ row.Replaces

if (satisfied.Count == squash.Replaces.Count) {
    // MATURE — auto-mark
    await store.WriteAsync(squashRecord, WritePrecondition.MustNotExist);
    continue;
}
if (satisfied.Count == 0) {
    // FRESH — run UpAsync with ApplyMode.Fresh
    ...
}
// strict subset — MID-RANGE
throw new MidRangeSquashException(...)
```

- [ ] `LoadSatisfyingRowsAsync` interface added (transitivity-aware variant of `LoadAppliedVersionsAsync`)
- [ ] Per-provider implementation: same realtime primitive but reads `Replaces` field of squash rows for transitive matching
- [ ] Reconciliation in `MigrationRunner.RunAsync()` updated

**Cross-provider participation check:** Per provider, but logic is identical at the runner level. ✓

### Task 3.3 — `WritePrecondition` API

Per consensus U1:

- [ ] `WritePrecondition` abstract record with `None`, `MustNotExist`, `MustMatchVersion(object opaqueToken)` variants
- [ ] `WriteOutcome` enum: `Created`, `AlreadyExistsBenign`, `PreconditionFailed`
- [ ] `IMigrationRecordStore.WriteAsync(record, precondition, ct) → WriteOutcome` overload (preserves existing void-returning overload for back-compat)
- [ ] Per-provider implementation:
  - **Postgres:** `INSERT ... ON CONFLICT DO NOTHING` rowcount → Created or AlreadyExistsBenign (re-read to verify checksum match)
  - **Aerospike:** `WritePolicy.RecordExistsAction.CREATE_ONLY` → catch `KEY_EXISTS_ERROR` for AlreadyExistsBenign
  - **Couchbase:** `Insert(key, value)` → catch `DocumentExistsException`
  - **MongoDB (standalone):** `InsertOne` → catch `MongoWriteException` `DuplicateKey`
  - **MongoDB (RS):** transactional `InsertOne` inside `IClientSessionHandle`
  - **OpenSearch:** `OpType=Create` → 409 → AlreadyExistsBenign (re-read to verify)

### Task 3.4 — `MidRangeSquashException`

- [ ] New exception type derived from base provider exception
- [ ] Carries `SquashVersion`, `MissingVersions: long[]`, `AppliedVersions: long[]`
- [ ] Default message lists missing versions + 3 documented recovery paths (backup-restore, re-introduce-from-git, `recover from-mid-range` Phase 8)

### Task 3.5 — Hand-authored squash test corpus

To exercise reconciliation without the codegen (which lands in Phase 6):

- [ ] Add synthetic test migration set: `Migration_1000`, `Migration_1010`, `Migration_1020`, plus a hand-authored `Squash_2000` with `Replaces=[1000,1010,1020]` and a body that recreates the equivalent state
- [ ] Per-provider integration tests:
  - Mature env (all 3 originals applied) → auto-marks Squash_2000 without running body
  - Fresh env (empty ledger) → runs Squash_2000 body; ApplyMode.Fresh
  - Mid-range env (1000,1010 applied; 1020 missing) → raises `MidRangeSquashException`
- [ ] Re-squash transitivity test: `Squash_3000` with `Replaces=[1500..2500]` where `Squash_2000` is in that range; mature env that auto-marked Squash_2000 should also auto-mark Squash_3000

**Cross-provider participation check:** Synthetic test corpus runs against all 5 providers; same assertions; per-provider testcontainer fixtures. ✓

**Phase 3 Completion Criteria:**
- [ ] `LoadSatisfyingRowsAsync` implemented across all 5 providers
- [ ] Reconciliation pseudocode in runner; auto-mark/fresh-install/mid-range branches all exercised
- [ ] `WritePrecondition` + `WriteOutcome` per-provider implementations validated
- [ ] Re-squash transitivity test passes
- [ ] No regressions in existing migration test suite

**Demo:** Author writes `Squash_2000.cs` by hand; runs against three test environments (production-clone, fresh, mid-range); auto-mark/UpAsync/exception fire correctly.

---

## Phase 4 — Script-Format Resource Support (ADR-0022)

**Objective:** Universal script format (`.statements`) for the 4 NoSQL providers; Postgres `.sql` aliased to `.statements`; backward-compatible with existing `.statements.json`.

**ADR compliance:** ADR-0001 (Parlot), ADR-0002 (resource pattern, amended), ADR-0017 (body-source grammar, amended), ADR-0022.

**Riskiest task in this phase:** Task 4.4 — OpenSearch grammar lift (richest grammar with 21 statement types and `BODIES` header). Recommend prototyping this first as a spike; the lift pattern from OpenSearch transfers cleanly to Aerospike/Couchbase/MongoDB.

### Task 4.1 — Resource loader format detection

- [ ] `ResourceRunner` base class detects format by extension at resource-iteration time
- [ ] `*.statements.json` → existing JSON-array loader (legacy)
- [ ] `*.statements` → new script loader
- [ ] `*.sql` → script loader (Postgres native, semantics already match)
- [ ] Both loaders produce the same AST stream into the dispatcher

**Cross-provider participation check:** Pure core; uniform behavior. ✓

### Task 4.2 — Aerospike grammar lift

- [ ] Lift `AerospikeStatementParser` to multi-statement entry rule (`script ::= (comment | statement ';' | whitespace)*`)
- [ ] Add `--`/`//` line comments + `/* */` block comments
- [ ] Statement terminator: `;`
- [ ] No `BODIES` header needed (Aerospike doesn't use body-source forms)
- [ ] Tests: hand-authored `.statements` files parse identically to equivalent `.statements.json`

### Task 4.3 — Couchbase grammar lift

- [ ] Lift `CouchbaseStatementParser` to multi-statement entry rule
- [ ] Comments + terminator + body forms
- [ ] N1QL native `--` comments already supported; `//` and `/* */` added
- [ ] Tests parallel Aerospike

### Task 4.4 — OpenSearch grammar lift (recommend prototype first)

- [ ] **PROTOTYPE FIRST:** spike a small `.statements` file with all body-source forms (`@path`, inline `{...}`, `BODIES` header `$name`); confirm grammar handles
- [ ] Lift `OpenSearchStatementParser` to multi-statement entry rule
- [ ] `BODIES { name: @path | name: {...} }` header parsing
- [ ] Form 4 (inline brace-balanced `WITH BODY {...}`) — brace-balanced consumption respecting JSON string escaping
- [ ] All 21 statement types continue to parse identically to JSON-array form
- [ ] Tests: convert one of the existing OpenSearch sample migrations (e.g., 9000-ForwardAttachmentLifecycle) to script form; verify byte-identical AST

**Cross-provider participation check:** OpenSearch lift is the richest; pattern transfers to others. ✓

### Task 4.5 — MongoDB grammar lift

- [ ] Lift `MongoStatementParser` to multi-statement entry rule
- [ ] `;` terminator (Mongo-shell native)
- [ ] `//` line comments (Mongo-shell native); `--` and `/* */` added
- [ ] Tests parallel pattern

### Task 4.6 — Postgres `.statements` alias + canonical formatter contract

- [ ] `PostgresResourceRunner` accepts `.statements` extension as alias for `.sql` — same loader; same parser
- [ ] `ISnapshotCanonicalizer.EmitScript()` interface added (script-emission contract)
- [ ] Postgres canonical formatter: one statement per line, single-space token sep, explicit `;`, alphabetically-sorted commutative modifiers, LF, UTF-8 no BOM

### Task 4.7 — Sample migration conversions (representative subset)

- [ ] Convert one existing sample per provider (e.g., `1000-CreateInitialIndex.statements.json` → `1000-CreateInitialIndex.statements`); verify byte-identical apply
- [ ] Add a *new* sample per provider authored in script form to demonstrate ergonomics
- [ ] Documentation update: `runners/samples/.../README.md` shows both forms

**Cross-provider participation check:** Each provider gets one converted + one new sample. ✓

**Phase 4 Completion Criteria:**
- [ ] All 5 providers parse `.statements` scripts; parse output AST-equivalent to JSON-array form
- [ ] Backward-compat: existing `.statements.json` files continue to apply unchanged
- [ ] Sample migrations exist in both forms; round-trip verified
- [ ] Determinism: parse + re-emit + re-parse produces identical AST (Phase 8 CI test)

**Demo:** Operator authors a fresh OpenSearch migration in `.statements` script form using `--`, `/* */`, and inline `WITH BODY {...}`; runs cleanly against the test container.

---

## Phase 5 — `ISquashStrategy` Contract + `NullSquashStrategy`

**Objective:** Define the universal strategy contract; ship `NullSquashStrategy` for Aerospike/Couchbase/MongoDB/OpenSearch; ship `ITopologySignature` with schema versioning; ship `IDataOpClassifier` interface (Postgres impl in Phase 6, others Phase 2+).

**ADR compliance:** ADR-0019 (especially A11 composite descriptor, A14 topology schema versioning), ADR-0006 (DI registration).

### Task 5.1 — `ISquashStrategy` + `SquashGenerationResult`

- [ ] `ISquashStrategy` interface with `GenerateAsync(ISquashGenerationContext, IReadOnlyList<MigrationDescriptor>, SquashGenerationOptions, CancellationToken)`
- [ ] `SquashGenerationResult` discriminated union: `Generated(ResourceContent, ContentKind, Encoding, Replaces[], Diagnostics, Topology)`, `Failed(Detail, Cause?)` — note `Unsupported` removed per A11
- [ ] `ContentKind` enum: `SqlText`, `CSharpSource`, `CanonicalJson`, `OpaqueBinary`
- [ ] `ContentEncoding` enum: `Utf8`, `Utf8Bom`, `Raw`

### Task 5.2 — `ITopologySignature` with schema versioning

- [ ] `ITopologySignature` interface: `int SchemaVersion`, `string ProviderId`, `IReadOnlyDictionary<string, string> Properties`, `bool IsCompatibleWith(other, out reason)`
- [ ] Per ADR-0019 A14: when a provider adds a new topology axis, signatures evolve via `SchemaVersion` bump + back-compat shim in `IsCompatibleWith`
- [ ] Documented contract: signature changes require ADR documenting back-compat

### Task 5.3 — `IDataOpClassifier`

- [ ] `IDataOpClassifier` interface with `DataOpClassification Classify(StatementOrCallSite)`
- [ ] `DataOpClassification` record: `IsDataOp`, `RequiresPreservation`, `IsUnclassified`, `RequiresAnnotation`, `EmissionHint`
- [ ] **Non-determinism scan rules** baked into framework helper (per ADR-0019 A8): scans for `DateTime.Now/UtcNow`, `Guid.NewGuid()`, `Random` sans seed, `Environment.MachineName/UserName`, `Stopwatch.GetTimestamp()`, `Process.Id`, `Activity.Current?.TraceId`, etc.
- [ ] Whitelist approach: any unrecognized invocation pattern → `IsUnclassified=true` (default-deny; safer error direction)

### Task 5.4 — Composite descriptor registration

Per ADR-0019 A11:

- [ ] `SquashStrategyDescriptor` record with all 5 components: `ITopologySignature`, `IDataOpClassifier`, `ISquashGenerator`, `ISquashVerifier`, `ISnapshotCanonicalizer`
- [ ] DI registration: `services.AddPostgresMigrations(opts => opts.UseSquash(strategy))` takes a single composite; framework validates all 5 components present + non-null at registration time
- [ ] `NotImplementedException` from any component → fails registration with descriptive error

### Task 5.5 — `NullSquashStrategy` shipped per non-v1 provider

For Aerospike, Couchbase, MongoDB, OpenSearch:

- [ ] `NullSquashStrategy` returns `SquashGenerationResult.Failed("Squash codegen for {provider} ships in v1.{1|2}; see roadmap")`
- [ ] **NOT** "hand-author" guidance per ADR-0019 A11 (Unsupported deletion)
- [ ] CLI invocation `dotnet hyperbee-migrations squash --provider {nonpostgres}` produces clean refusal message pointing at roadmap (not `ServiceNotFound`)

**Cross-provider participation check:** Each non-Postgres provider registers `NullSquashStrategy`; uniform behavior. ✓

### Task 5.6 — Sequencing per A7 in ADRs

- [ ] Per-provider doc updates noting v1.1/v1.2 phase for their codegen
- [ ] CHANGELOG.md entries

**Phase 5 Completion Criteria:**
- [ ] `ISquashStrategy` contract shipped
- [ ] All 5 providers register a strategy (Postgres = real, others = Null)
- [ ] CLI invocation against non-Postgres returns clean refusal naming the roadmap phase
- [ ] No service resolution failures

**Demo:** Run `dotnet hyperbee-migrations squash --provider mongodb --range 1000-1500`; receive clean refusal: "MongoDB squash codegen ships in v1.1; see release roadmap. Current options: continue applying migrations individually."

---

## Phase 6 — Postgres v1 Squash Generator (path-finder)

**Objective:** Concrete `PgDumpSnapshotStrategy` implementation with all hardening: server-version-matched container, `pg_dump` post-processing pipeline, statement classifier, sequence `setval` post-emission, `CREATE INDEX CONCURRENTLY` stripping, in-process dump-vs-dump verification, IDataOpClassifier with non-determinism scan, mandatory `[DataMigration]` annotation.

**ADR compliance:** ADR-0019 (especially A1 no-skip-verify, A4 cache+parallel, A5 mandatory `[DataMigration]`, A8 non-determinism scan, A10 server-version-matched container), ADR-0021 (checksum scope), ADR-0022 (script-form output).

**🟡 Task 6.3 was originally flagged as the riskiest task in this plan. Spike was completed 2026-05-06 (`spikes/postgres-classifier/SPIKE_REPORT.md`).** Risk classification revised from High to **Moderate** after the spike. Calibration findings:

- Spike prototype: ~570 LOC achieved 88.4% classification (61/69 statements) on real pg_dump 16.13 output on first attempt; spike target was ≥80%.
- Two trivially fixable failure modes identified (~60 LOC total): pg_dump 16+ `\restrict`/`\unrestrict` psql directives and `ALTER INDEX ... ATTACH PARTITION`.
- Three substantive findings the plan didn't anticipate (F3 pg_dump rewrites function-body dollar tags to `$_$`; F4 inline PRIMARY KEY emitted as separate `ALTER TABLE ADD CONSTRAINT`; F6 dollar-quote authoring rules — F6 lifted into ADR-0022 amendment A1).
- **Estimate revised:** original 600-1000 LOC for the whole task underweighted `IDataOpClassifier` (separate scan over user code, ~200-400 LOC) and verification-harness production hardening (~200-300 LOC). New estimate: **1200-1750 LOC / 5-7 days** for full Task 6.3 + 6.4 + 6.5 + 6.6 production work. v1 total revised to **5-7 weeks**.

### Task 6.1 — `PostgresTopologySignature`

- [ ] Records `{server_major, server_minor, extensions[], collation_provider, locale_provider, server_encoding}`
- [ ] `IsCompatibleWith` enforces: server_major equality, extension set equality, collation/locale provider equality
- [ ] `SchemaVersion = 1`
- [ ] `CaptureAsync(NpgsqlConnection)` reads from server

### Task 6.2 — `PostgresDataOpClassifier`

- [ ] Roslyn AST scanner over migration source files (single-compilation-per-assembly per A8 → PA-4 fix)
- [ ] Detects DDL keywords: `CREATE`, `ALTER`, `DROP`, `COMMENT ON`, `GRANT`, `REVOKE`
- [ ] Detects DML keywords: `INSERT`, `UPDATE`, `DELETE`, `TRUNCATE`, `MERGE`, `COPY ... FROM`, `SELECT INTO`, `CREATE TABLE AS SELECT`
- [ ] Conservative: `DO $$` blocks containing DML keywords flagged as `RequiresPreservation=true`; functions containing DML in their body classified as DDL (definition is structural)
- [ ] Non-determinism scan helper (from Phase 5) wired in
- [ ] **Mandatory `[DataMigration]` annotation per A5:** when heuristic detects suspected DML on a migration class lacking either `[DataMigration]` or `[StructuralOnly]`, classifier returns `RequiresAnnotation=true`; squash refuses with diagnostic naming the migration

**Test corpus:** synthetic Postgres migrations with mixed DDL/DML/DO blocks/function definitions/non-deterministic patterns; assert classifier verdicts.

### Task 6.3 — Postgres statement classifier (🟡 MODERATE RISK; spike completed 2026-05-06)

- [x] **Spike completed:** prototype splitter + classifier + Testcontainers harness vs real pg_dump 16.13 output. 88.4% classification rate; risk reclassified High → Moderate. See `spikes/postgres-classifier/SPIKE_REPORT.md`.
- [ ] Productionize splitter (~200 LOC): port spike `PostgresStatementSplitter` and add `\<directive>` strip pre-pass for pg_dump 16+ `\restrict`/`\unrestrict`.
- [ ] Productionize classifier (~500-700 LOC): port spike `PostgresStatementClassifier` and extend to:
  - `CREATE TABLE` (with all column types, generated, identity, defaults, constraints)
  - `CREATE INDEX` (B-tree, hash, GiST, GIN, partial, expression, covering, unique)
  - `CREATE TRIGGER`, `CREATE VIEW`, `CREATE MATERIALIZED VIEW`
  - `CREATE FUNCTION`, `CREATE PROCEDURE`, `CREATE TYPE`, `CREATE DOMAIN`
  - `CREATE POLICY`, `ALTER TABLE ENABLE ROW LEVEL SECURITY`
  - `CREATE EXTENSION` (extracted to prerequisites.sql)
  - `CREATE SEQUENCE`, identity-owned sequences
  - Partitioning declarations (RANGE/LIST/HASH)
  - `ALTER TABLE ... ATTACH PARTITION`
  - **`ALTER INDEX ... ATTACH PARTITION`** (added per spike finding)
  - `DROP X` family (spike didn't exercise; squash diff emits DROP for disappearing objects)
  - `ALTER TABLE ... ADD CONSTRAINT` recognized as distinct from generic ALTER (per spike finding F4)
- [ ] Per-(kind, name) set diff producing typed delta primitives
- [ ] Statement classifier output is the input to canonical formatter

### Task 6.4 — `PostgresSnapshotCanonicalizer`

- [ ] Post-processing pipeline per ADR-0019 amendment in design (Postgres examples):
  - Strip `SET` preamble (per spike finding F5: ~10 SET statements at dump head, version-dependent)
  - Strip `\restrict` / `\unrestrict` psql directives (per spike finding F1: emitted by pg_dump 16+)
  - Strip `SELECT pg_catalog.set_config('search_path', '', false);`
  - Normalize line endings to LF, encoding UTF-8 no BOM
  - Collapse blank lines; trim trailing whitespace
  - Normalize function-body dollar tags to a canonical form before hashing (per spike finding F3: pg_dump rewrites all input tags to `$_$`; canonicalizer must hash tag-stripped body to be deterministic across re-dumps)
  - Extract `CREATE EXTENSION` to separate `prerequisites.sql`
  - Detect / refuse `CREATE INDEX CONCURRENTLY` (per A2 — pg_dump --schema-only doesn't emit it; defense-in-depth)
- [ ] `EmitScript()` for canonical script form output (per ADR-0022)
- [ ] Determinism CI test (per Phase 8 C12)

### Task 6.5 — `PgDumpSnapshotStrategy`

- [ ] Spins server-version-matched ephemeral Postgres container per A10 (single canonical image; runs `pg_dump` inside via `docker exec`)
- [ ] Apply migrations < N (residual head) → snapshot A
- [ ] Apply migrations [N..M] → snapshot B
- [ ] Sequence `last_value` capture (`SELECT last_value FROM <seq>` for every sequence in B; emits `setval(...)` post-emission)
- [ ] Statement classifier diff → typed delta
- [ ] Emits canonical Postgres `Squash_M.sql` + `Squash_M.dataops.sql` + `Squash_M.prerequisites.sql` + `Squash_M.summary.md`

### Task 6.6 — `PostgresSquashVerifier`

- [ ] In-process dump-vs-dump byte-compare per A4
- [ ] Spin third container OR reuse Container A residual-head state per A4 (cache + parallel A/B)
- [ ] Apply generated squash to verifier container; pg_dump; canonicalize; byte-compare against B
- [ ] Container lifecycle on failure per A18: tear down by default; retain only with `--keep-failed-container`; debug summary always written to `./squash-debug/<timestamp>/`
- [ ] `try/finally` for Ctrl-C cleanliness

**Phase 6 Completion Criteria:**
- [ ] End-to-end Postgres squash codegen produces verifiable artifacts
- [ ] `[DataMigration]` annotation enforcement validated
- [ ] Non-determinism scan refuses migrations with `DateTime.UtcNow` etc.
- [ ] Sequence `setval` post-emission works for non-default `last_value`
- [ ] CONCURRENTLY-stripping is deliberate and documented
- [ ] Verification round detects canonicalization regressions

**Demo:** Real Postgres app with 18 sample migrations (mirroring the Postgres walkthrough example); operator runs squash; verification passes; emitted Squash_2000.* files apply cleanly to fresh container; bytes match.

---

## Phase 7 — Squash CLI + Fleet Readiness + Verification

**Objective:** Ship the `dotnet hyperbee-migrations squash` verb; fleet manifest schema; `--squash-overrides` structured fields with 30-day default expiry; two-phase fleet readiness gate; verification with snapshot A caching + parallel A/B + container lifecycle on failure; deterministic-token gate (UI side; cmd surface side in Phase 8).

**ADR compliance:** ADR-0019 (A2 fleet gate, A4 caching+parallel, A9 structured overrides, A15 30-day expiry, A18 container lifecycle).

### Task 7.1 — CLI verb skeleton

- [ ] New project `Hyperbee.Migrations.Cli` (or extend existing if present) with `dotnet hyperbee-migrations squash --provider <p> --range <a>-<b> --fleet-manifest <path> --output <dir>`
- [ ] Provider dispatch via DI; resolves `ISquashStrategy` per provider
- [ ] Help text + error formatting

### Task 7.2 — Fleet manifest schema

- [ ] `fleet.yml` schema parser (YamlDotNet)
- [ ] Per-env fields: `name`, `connection` (env-var-substituted), optional `ledger-export`, `topology` (provider-specific)
- [ ] `squash-overrides` structured-fields block per ADR-0019 A9:
  - `accept-stranding` per-env list with `ticket-id` (regex-validated), `owner` (git-author-validated), `reason` (free text), `expires` (default 30 days, max 90 per A15)
  - Provider-specific override sections (e.g., `postgres: {}`, `mongodb: { target-topology }`)

### Task 7.3 — Fleet readiness check (Phase 1 of two-phase gate)

- [ ] Parallel env probing via `Parallel.ForEachAsync(maxParallelism: 8)` per A6 (PA-6 redesign)
- [ ] Per-env: load `LoadAppliedVersionsAsync(allCandidateIds)`; compute `maxAppliedVersion`; classify as `<N` / `[N..M)` / `≥M`
- [ ] Refuse with `MidRangeFleetException` listing offending envs + per-env first-missing version + last-applied version

### Task 7.4 — Squash artifact header + Phase 2 of fleet gate

- [ ] Squash artifact header (in C# class XML doc comment + a sidecar `Squash_M.metadata.json`):
  - `replaces: 1000..1170 (18 versions)`
  - `topology: { ... }`
  - `canonicalizer-version: postgres/1.0.0`
  - `expected-fleet-versions: { env: minVersion, ... }`
  - `max-staleness-window: 30d`
  - `squash-overrides: { ... }`
  - `codegen-tool-version: hyperbee-migrations/1.0.0`
- [ ] Runner reads metadata at deploy time; per A2: refuse on env not in `expected-fleet-versions` (`UnregisteredEnvironmentException`); refuse on env's actual version below recorded minimum AND env hasn't moved within staleness window (`StaleFleetMemberException`)

### Task 7.5 — Verification round with snapshot caching + parallel A/B

Per A4:

- [ ] Snapshot A cache: keyed by `hash(provider, residual-head-version-set, canonicalizer-version, topology-signature, image-version)`
- [ ] First squash regeneration pays full cost; subsequent regenerations skip Container A entirely
- [ ] A and B captured in parallel via `Task.WhenAll` over independent containers
- [ ] Container reuse for verification: Container A's residual-head state reused as verification base
- [ ] `--keep-failed-container` flag for debug; default tear down on failure; `try/finally` for Ctrl-C cleanliness; debug summary always written to `./squash-debug/<timestamp>/`

### Task 7.6 — Per-env stranding reasons

- [ ] `--accept-stranding=name1,name2` requires paired `--reason-stranding=name=<≥20 chars>` per env per A11
- [ ] CLI refuses without one reason per name; logs to audit trail

### Task 7.7 — Source file removal at squash creation

- [ ] After successful generation + verification, remove original migration source files from the migrations folder
- [ ] Idempotent: re-running squash with same range refuses (cells already removed) unless `--regenerate` opt-in

**Phase 7 Completion Criteria:**
- [ ] End-to-end CLI workflow: spin codegen, fleet readiness, generate, verify, remove originals, write artifacts
- [ ] Mid-range env at squash creation refuses cleanly
- [ ] Mid-range env at deploy time raises `StaleFleetMemberException`
- [ ] Stranded envs require per-env reasons
- [ ] Override expiry enforced (CI-equivalent test refuses expired overrides)

**Demo:** Operator runs squash CLI end-to-end against the sample Postgres app; mature env auto-marks at deploy; fresh dev container provisions in ~5s; mid-range env hits exception with clear remediation.

---

## Phase 8 — Recovery + Determinism CI + Documentation

**Objective:** `recover from-mid-range` subcommand with deterministic token gate; C12 generation determinism CI test; operator guide; CHANGELOG; release readiness.

**ADR compliance:** ADR-0019 (A3 recover subcommand, A16 generation determinism gate), ADR-0022 (script-format determinism).

### Task 8.1 — `recover from-mid-range` subcommand

Per ADR-0019 A3:

- [ ] Separate verb: `dotnet hyperbee-migrations recover from-mid-range`
- [ ] Required args: `--env=<name>`, `--accept-data-corruption-risk=<token>`, `--ticket-id=<>`, `--reason=<≥20 chars>`
- [ ] Token = `SHA-256(env-name ‖ squash-version ‖ missing-versions-set)[:12]` — deterministic per (env, squash, gap), reproducible across retries
- [ ] Audit trail records all four args
- [ ] Documented as "last resort, DBA-supervised, post-incident only"
- [ ] Backup-restore remains documented primary recovery

### Task 8.2 — C12 Generation determinism CI gate

- [ ] CI test per provider: run `squash --range R` twice in fresh ephemeral containers; assert byte-equal:
  - `Squash_M.sql` body
  - `Squash_M.dataops.sql`
  - `Squash_M.prerequisites.sql`
  - `Squash_M.summary.md`
  - Topology signature
- [ ] Postgres: real test in v1
- [ ] Aerospike/Couchbase/MongoDB/OpenSearch: stub tests against `NullSquashStrategy` (gate fires when their strategies ship)
- [ ] Sources of nondeterminism eliminated by canonicalization: timestamps in artifact headers (canonical form is "Generated YYYY-MM-DD" not full timestamp), GUIDs in codegen output (none allowed), container UUIDs/port assignments (excluded from artifact), dictionary iteration order (sorted)

### Task 8.3 — Round-trip determinism CI gate (per ADR-0022)

- [ ] CI test per provider: parse `.statements` script + re-emit via canonical formatter + re-parse + assert AST-equivalent
- [ ] Catches canonical-formatter regressions

### Task 8.4 — Operator guide

- [ ] New doc `docs/guides/squashing-migrations.md`:
  - When to squash (when fresh-env provisioning > 30s, when migration count > 50)
  - Authoring a squash (CLI invocation, fleet manifest, override block)
  - Reviewing a squash PR (read summary.md, not artifact bytes)
  - Recovering from `MidRangeSquashException` (3 paths)
  - Recovering from `StaleFleetMemberException`
  - Roadmap (v1 = Postgres only; v1.1 / v1.2 sequencing)

### Task 8.5 — EF Core migration bridge guide (per Assessment 0007 cross-cutting)

- [ ] `docs/guides/migrating-from-ef-core.md` per consensus open item
- [ ] How to bridge `__EFMigrationsHistory` to `MigrationRecord`
- [ ] Recommended workflow: synchronize fleet to known EF version → introduce hyperbee `[Migration(N, Replaces=[…all-prior-EF-versions…])]` baseline → `--accept-unverified-version` allowlist for EF-era nulls

### Task 8.6 — CHANGELOG + version bump to v3.0

- [ ] CHANGELOG.md entry for v3.0 squash feature
- [ ] `version.json` bumps from 2.x to **3.0** (semver-appropriate per Backward Compatibility section at top of plan)
- [ ] Release notes draft framing the two breaking changes:
  1. `IMigrationRecordStore` gains three methods (with safe DIM defaults so custom implementations work unchanged but get degraded behavior)
  2. Provider record-store schemas gain `Checksum` + `Kind` fields (additive; idempotent migration)
- [ ] Release notes also frame: squash is operationally one-way; rolling back hyperbee.migrations to v2 against a squashed ledger is unsupported

### Task 8.7 — v2 → v3 Upgrade Migration Guide

- [ ] New doc `docs/guides/upgrading-from-v2.md`:
  - **Schema migration** — automatic and idempotent on first v3 apply; pre-existing rows read clean (`Checksum=null, Kind=Migration`)
  - **Custom `IMigrationRecordStore` implementations** — DIM defaults preserve v2 behavior; recipe for opting into squash support by overriding `WriteAsync(MigrationRecord, WritePrecondition, ct)`, `LoadAppliedVersionsAsync`, `LoadSatisfyingRowsAsync`
  - **Mixed-version fleet hazard** — don't run v2 and v3 against the same ledger simultaneously; deploy v3 to all envs before squashing; ADR-0019 A2 two-phase fleet readiness gate is the safety net
  - **Squash is operationally one-way** — once committed, original migration source files are removed; rollback to v2 unsupported; backup-restore is the recovery path if needed
  - **What stays the same** — existing migrations (no `Replaces`) work unchanged; existing `*.statements.json` resources work unchanged (legacy loader); existing `dotnet build` / `dotnet test` flows work unchanged
- [ ] Cross-link from CHANGELOG.md and from each provider's README.md

**Phase 8 Completion Criteria:**
- [ ] All 9 P0 + 10 P1 amendments verified by automated tests
- [ ] Operator can read the guide and run a squash end-to-end without external help
- [ ] Determinism CI gates green on Postgres
- [ ] CHANGELOG complete with v3.0 framing
- [ ] Upgrade guide ships covering DIM defaults, schema migration, mixed-fleet hazard, one-way squash

**Demo:** Release-readiness review: walk through guides end-to-end with a fresh developer; they author a squash without help.

---

## Cross-Provider Participation Matrix

Tasks that look provider-shaped get explicit per-provider notes here. Tasks that are pure core (where the cross-provider lens is trivially "no provider-specific code") aren't repeated.

| Task | Aerospike | Couchbase | MongoDB | OpenSearch | Postgres |
|---|---|---|---|---|---|
| 1.1 (core types) | n/a | n/a | n/a | n/a | n/a |
| 1.2-1.6 (record store extension) | sparse bins; trivial additive | JSON doc; trivial | JSON doc; trivial | ledger index mapping bump (ADR-0018 split-aware) | two ALTER TABLEs + CHECK constraint |
| 1.7 (default checksum) | resource-bytes hash | resource-bytes hash | resource-bytes hash | resource-bytes hash incl. body refs | resource-bytes hash for `.sql` |
| 3.1 (LoadAppliedVersionsAsync realtime) | BatchGet strong consistency | MultiGet + mutation token | find with ReadConcern.Majority on RS, Local on standalone | _mget realtime=true | SELECT ... WHERE record_id = ANY |
| 3.3 (WritePrecondition) | RecordExistsAction.CREATE_ONLY | Insert + DocumentExistsException | InsertOne + DuplicateKey (standalone) / transaction (RS) | OpType=Create | INSERT ON CONFLICT DO NOTHING |
| 3.5 (synthetic squash test corpus) | runs in test container | runs in 7-state bootstrap container | runs in standalone+RS containers | runs in single-node + multi-node containers | runs in standard Postgres container |
| 4.2-4.5 (script-format grammar lift) | AQL subset multi-statement | N1QL subset multi-statement | Mongo-shell-like multi-statement | 21-statement AST multi-statement (richest, prototype first) | n/a (already script form) |
| 4.6 (Postgres alias) | n/a | n/a | n/a | n/a | `.sql` ↔ `.statements` aliased |
| 5.5 (NullSquashStrategy) | shipped | shipped | shipped | shipped | n/a (real strategy in Phase 6) |
| 6.* (Postgres squash codegen) | n/a (v1.1) | n/a (v1.2) | n/a (v1.1) | n/a (v1.2) | full implementation |
| 7.4 (artifact header expected-fleet-versions) | n/a in v1 | n/a | n/a | n/a | recorded for Postgres squashes |
| 8.2 (C12 determinism CI) | stub test only | stub | stub | stub | full real test |

---

## Phase Dependencies

```
Phase 0 (Foundation)
  ↓
Phase 1 (Universal Ledger Scaffolding)
  ↓
Phase 2 (Attribute + Discovery)         ─┐
                                          ├──> Phase 3 needs both
Phase 4 (Script-Format Resource Support) ─┘    (independent of Phase 4)
  ↓
Phase 3 (Reconciliation)
  ↓
Phase 5 (Strategy Contract)
  ↓
Phase 6 (Postgres Squash Generator) ── Riskiest; consider 2-day spike at start
  ↓
Phase 7 (CLI + Fleet + Verification)
  ↓
Phase 8 (Recovery + CI + Docs)
```

Phase 4 (script-format) is independent of Phases 1-3 (ledger + reconciliation) and can start in parallel after Phase 0. **For a single developer, sequential execution is the safer path** — Phase 4 surfaces grammar edge cases that may inform Phase 6 codegen work.

---

## ADR Compliance Matrix

Each task's compliance with the ADRs that constrain it:

| Phase | ADRs honored |
|---|---|
| 0 | (none — audit only) |
| 1 | ADR-0003 (additive ledger contract), ADR-0021 + A1+A2 |
| 2 | ADR-0004 (reflection discovery), ADR-0009 (record IDs), ADR-0019 |
| 3 | ADR-0019 (especially A6 transitivity, A17 consistency), ADR-0021 A1, consensus C2/U1/U2 |
| 4 | ADR-0001 (Parlot), ADR-0002 (resource pattern, amended), ADR-0017 (body sources, amended), ADR-0022 |
| 5 | ADR-0006 (DI), ADR-0019 (A11 composite, A14 topology versioning) |
| 6 | ADR-0019 (A1 no skip-verify, A4 cache+parallel, A5 mandatory annotation, A8 non-determinism, A10 server-matched container, A18 container lifecycle), ADR-0021 |
| 7 | ADR-0019 (A2 fleet gate, A9 structured overrides, A15 30-day expiry) |
| 8 | ADR-0019 (A3 recover, A16 determinism gate), ADR-0022 (script determinism) |

Every task has its constraint set. `/nop:implement`'s Reflect step checks compliance per task.

---

## Test Plan Summary

| Phase | Test types |
|---|---|
| 0 | Existing 75/75 OpenSearch + provider unit suites continue to pass |
| 1 | Per-provider integrity check unit + integration tests; pre-checksum-era reads |
| 2 | Discovery validation tests (self-ref, missing version, duplicates); ApplyMode wiring |
| 3 | Synthetic squash corpus per provider (auto-mark / fresh / mid-range); re-squash transitivity |
| 4 | Round-trip parse-emit-parse equivalence per provider; legacy JSON-array compat |
| 5 | NullSquashStrategy refusal CLI tests; composite descriptor validation |
| 6 | Postgres real-pg_dump corpus tests; classifier verdict tests; canonicalizer determinism; verifier byte-compare |
| 7 | End-to-end Postgres squash workflow; fleet readiness with mid-range env; override expiry; container lifecycle |
| 8 | C12 generation determinism CI; round-trip determinism; recover-token gate |

---

## Risk Register

| Risk | Phase | Mitigation |
|---|---|---|
| ~~🔴~~ 🟡 Postgres statement classifier complexity (Task 6.3) | 6 | **Spike completed 2026-05-06** (`spikes/postgres-classifier/SPIKE_REPORT.md`). 88.4% classification on first attempt; risk reclassified High → Moderate. |
| OpenSearch grammar lift edge cases (Task 4.4) | 4 | Prototype first; pattern transfers to other 3 NoSQL providers |
| ADR-0018 split-ledger-and-lock cross-index hazards in OpenSearch ledger update (Task 1.6) | 1 | Confirm new fields go to ledger only; lock unchanged |
| Snapshot A cache key incomplete (cache miss → wrong A) | 7 | Key includes `topology-signature` + `image-version` per IR refinement; CI determinism test catches |
| `[DataMigration]` heuristic false negatives (silent data drop) | 6 | Mandatory annotation per A5 (refuses unmarked DML); classifier whitelist approach |
| Verification round container leak | 7 | `try/finally` per A18; CI test for Ctrl-C handling |
| Sequence `setval` edge cases (identity columns, sequences referenced by views) | 6 | Test corpus includes mixed sequence kinds; conservative emission for ambiguous cases |
| Velocity overrun (4-6 weeks → 8+ weeks) | all | Phase 4 (script-format) can ship as v1 even if Phases 5-8 slip; provides ergonomics value standalone |

---

## Recommended Next Step

**Phase 0 ☑ complete (2026-05-06). Phase 6 Task 6.3 spike ☑ complete (2026-05-06).** Next: Phase 1 — Universal Ledger Scaffolding (~4-5 days, ~650 LOC across 5 providers). The spike confirmed Phase 6 is tractable; Phase 1 is the next blocking milestone.

Plan stays a living document throughout execution. `/nop:implement` updates checkboxes, status, and learnings; the plan file is the source of truth.

---

## Phase 0 Audit Appendix (Task 0.1 output)

Codebase audit verifying the design's assumptions against current HEAD (`migration-squashing-v1-baseline` tag = `e7a099e` on main). Performed 2026-05-06 via Explore sub-agent over the relevant provider files.

| # | Assumption | Status | Evidence |
|---|---|---|---|
| A1 | `MigrationRecord` is `(Id, RunOn)` only | **CONFIRMED** | [`MigrationRecord.cs:5-9`](../../../src/Hyperbee.Migrations/MigrationRecord.cs#L5-L9) — `string Id` + `DateTimeOffset RunOn` only |
| A2 | `IMigrationRecordStore` has 5 methods | **DIVERGENCE** | [`IMigrationRecordStore.cs:7-16`](../../../src/Hyperbee.Migrations/IMigrationRecordStore.cs#L7-L16) — interface actually has **6** methods: `InitializeAsync`, `CreateLockAsync`, `ExistsAsync`, `ReadAsync`, `DeleteAsync`, `WriteAsync`. Plan was off by one (forgot `DeleteAsync`). Minor; no design-shape change. |
| A2.1 | **`WriteAsync` takes a `MigrationRecord`** | **DIVERGENCE — API change required in Phase 1** | [`IMigrationRecordStore.cs:15`](../../../src/Hyperbee.Migrations/IMigrationRecordStore.cs#L15) — actual signature is `Task WriteAsync(string recordId)`. Record stores construct the `MigrationRecord` internally. **Phase 1 must extend the interface** to accept a `MigrationRecord` (or add an overload). Runner call sites at [`MigrationRunner.cs:125,221`](../../../src/Hyperbee.Migrations/MigrationRunner.cs#L125) currently pass only the recordId. |
| A3 | `MigrationRunner.DiscoverMigrations()` projects `[Migration].Version + Profiles` | **CONFIRMED** | [`MigrationRunner.cs:160-189`](../../../src/Hyperbee.Migrations/MigrationRunner.cs#L160-L189) — reflection over assemblies; projects `MigrationAttribute` (line 168); orders by `Version` per `Direction` (line 172) |
| A4 | All 4 NoSQL providers expose `*ResourceRunner.StatementsFromAsync()` | **CONFIRMED** | Aerospike [`AerospikeResourceRunner.cs:30,45`](../../../src/Hyperbee.Migrations.Providers.Aerospike/Resources/AerospikeResourceRunner.cs#L30); Couchbase [`CouchbaseResourceRunner.cs:39,54`](../../../src/Hyperbee.Migrations.Providers.Couchbase/Resources/CouchbaseResourceRunner.cs#L39); MongoDB [`MongoDBResourceRunner.cs:27,42`](../../../src/Hyperbee.Migrations.Providers.MongoDB/Resources/MongoDBResourceRunner.cs#L27); OpenSearch [`OpenSearchResourceRunner.cs:59,68`](../../../src/Hyperbee.Migrations.Providers.OpenSearch/Resources/OpenSearchResourceRunner.cs#L59). All four use identical `StatementsFromAsync(string)` + `StatementsFromAsync(string[], TimeSpan?)` overloads. |
| A5 | Postgres uses `PostgresResourceRunner.AllSqlFromAsync()` | **CONFIRMED** | [`PostgresResourceRunner.cs:77-82`](../../../src/Hyperbee.Migrations.Providers.Postgres/Resources/PostgresResourceRunner.cs#L77-L82) — `AllSqlFromAsync(CancellationToken)` + overload. Loads `*.sql` resources. |
| A6 | `MigrationAttribute` shape (Phase 2 baseline) | **EXPECTED EXTENSION** | [`MigrationAttribute.cs:4-31`](../../../src/Hyperbee.Migrations/MigrationAttribute.cs#L4-L31) — currently has `Version, Profiles, StartMethod, StopMethod, Journal, Cron`. Plan adds `Replaces` (long[]) + `ReplacesRange` (string) per ADR-0019 in Phase 2. Cron + lifecycle properties unaffected. |
| A7 | Per-provider `WriteAsync`/`ReadAsync` locations | **CONFIRMED** | Aerospike [`AerospikeRecordStore.cs:158,184`](../../../src/Hyperbee.Migrations.Providers.Aerospike/AerospikeRecordStore.cs#L158); Postgres [`PostgresRecordStore.cs:93,118`](../../../src/Hyperbee.Migrations.Providers.Postgres/PostgresRecordStore.cs#L93); MongoDB [`MongoDBRecordStore.cs:97`](../../../src/Hyperbee.Migrations.Providers.MongoDB/MongoDBRecordStore.cs#L97); Couchbase + OpenSearch implement `IMigrationRecordStore` directly. **Per A2.1, Phase 1 needs to extend signatures across all 5 stores in lockstep.** |
| A8 | Test container infrastructure exists | **CONFIRMED** | [`tests/Hyperbee.Migrations.Integration.Tests/Container/`](../../../tests/Hyperbee.Migrations.Integration.Tests/Container/) — per-provider `*TestContainer` + `*MigrationContainer` classes. `HYPERBEE_TESTS_PROVIDERS_ONLY` env var scopes startup. Squash tests can reuse via [AssemblyInitialize] inheritance. |
| A9 | Test project conventions: MSTest + FluentAssertions + NSubstitute | **CONFIRMED** | [`Hyperbee.Migrations.Tests.csproj:14-24`](../../../tests/Hyperbee.Migrations.Tests/Hyperbee.Migrations.Tests.csproj#L14) — MSTest 18.x + FluentAssertions + NSubstitute. Multi-target net8/9/10 inherited from `Directory.Build.props`. |

### Plan adjustments triggered by audit

1. **Phase 1 Task 1.1 amended** (was: "MigrationRecord gains Checksum + Kind"): now also includes **API shape change** — `IMigrationRecordStore.WriteAsync(MigrationRecord record, WritePrecondition precondition, CancellationToken ct) → WriteOutcome`. Existing `WriteAsync(string)` overload preserved for backward-compatibility (runner constructs a default record with `Kind=Migration, Checksum=null` when called via the legacy overload).
2. **Phase 1 Tasks 1.2-1.6 (per-provider record store updates) amended:** each provider implements both the new record-bearing overload and continues to honor the legacy string-only overload. Existing test suite continues to pass unchanged.
3. **No additional phases or scope changes** triggered by the audit. The 9-phase structure stands.

---

## Status

**Phase 0: ☑ COMPLETE 2026-05-06**

**Completion summary:**
- Branch `devs/bfarmer/provider-squash` created from `main` (commit `e7a099e`).
- Tag `migration-squashing-v1-baseline` placed on main HEAD before any squash work.
- Baseline build green: 0 errors, 36 pre-existing warnings (testcontainers obsolete-ctor + MSTEST style nags from prior provider work; out of scope for this feature).
- Baseline unit tests green: **356/356 pass on net10** (Hyperbee.Migrations.Tests).
- Foundation commit `aeec2d6` lands all 23 design artifacts (ADRs 0019-0022, design docs, consensus, requirements, plan, research artifacts, EF Core reference). 12,230 insertions; 0 production code changes.
- Codebase audit complete (Task 0.1) — see Audit Appendix above. **One real divergence (A2.1):** `WriteAsync` API takes `string recordId` not `MigrationRecord`; Phase 1 must extend the contract.
- Test project skeleton `Hyperbee.Migrations.Squash.Tests` created (Task 0.3).
- Existing integration test suite NOT re-run at baseline (requires Docker; existing 75/75 OpenSearch suite was green at session prior; out-of-scope re-validation).

**Phase 6 Task 6.3 Spike: ☑ COMPLETE 2026-05-06**

- Spike artifacts at `spikes/postgres-classifier/` (commit `9e5f45e`).
- Real `pg_dump 16.13 --schema-only` round-trip via Testcontainers Postgres 16-alpine.
- 88.4% classification rate on first attempt (61/69 statements; threshold was 80%).
- 8 unknowns identified in 2 well-bounded categories (~60 LOC fix scope total).
- Three substantive findings (F3 dollar-tag rewrites, F4 PRIMARY KEY extraction, F6 dollar-quote authoring rules).
- F6 lifted into ADR-0022 amendment A1 (Postgres dollar-quote authoring rules subsection).
- Phase 6 estimate revised: 600-1000 LOC → 1200-1750 LOC; 4-5 days → 5-7 days. v1 total: 4-6 weeks → 5-7 weeks.
- Phase 6 Task 6.3 risk classification: High → Moderate.

**Phase 1: ☑ COMPLETE 2026-05-06**

**Completion summary:**
- New core types: `MigrationRecordKind`, `WritePrecondition`, `WriteOutcome`, `MigrationLedgerIntegrityException`, `IChecksumStrategy`, `DefaultChecksumStrategy`.
- `MigrationRecord` extended with `Checksum`, `Kind`, `Replaces` (concrete `long[]` for clean cross-provider serialization; `IMigrationRecord.Replaces` exposes `IReadOnlyList<long>`).
- `IMigrationRecordStore` extended with three DIM-defaulted methods: `WriteAsync(MigrationRecord, WritePrecondition, CT) → WriteOutcome`, `LoadAppliedVersionsAsync`, `LoadSatisfyingRowsAsync`. v2 record stores compile and run unchanged via the DIM defaults.
- All 5 provider record stores override `WriteAsync(MigrationRecord, ...)` with realtime semantics:
  - Postgres: idempotent `ALTER TABLE ADD COLUMN IF NOT EXISTS` + `INSERT ... ON CONFLICT DO NOTHING/UPDATE` + `bigint[]` array literal.
  - Aerospike: `RecordExistsAction.CREATE_ONLY` for MustNotExist; checksum-equality re-check on `KEY_EXISTS_ERROR`.
  - Couchbase: `InsertAsync` with `DocumentExistsException` recovery; `UpsertAsync` for None.
  - MongoDB: `InsertOneAsync` with `MongoWriteException`/`DuplicateKey` recovery; `ReplaceOneAsync(IsUpsert)` for None.
  - OpenSearch: `OpType.Create` for MustNotExist with `409` recovery; ledger index strict mapping extended with `kind`/`replaces` fields + idempotent `PUT _mapping` patch for v2-era indices.
- `MigrationRunner` routes journal writes through the record-bearing overload (v3) with computed checksum.
- `OpenSearchMigrationRecord.Checksum` removed (now inherited from base per ADR-0021); `LedgerIndexInitStep` mapping updated.
- Cross-provider integrity tests in `Hyperbee.Migrations.Squash.Tests.LedgerIntegrityTests`: 6/6 pass.
- Existing core unit tests: **356/356 pass on net8/9/10** (RunnerTests fake updated to mirror DIM behavior; cron-write assertion updated to v3 record-bearing overload).
- Build clean across all 5 providers + tests; 0 errors, 36 pre-existing warnings.

**Phase 2: ☑ COMPLETE 2026-05-06**

**Completion summary:**
- `MigrationAttribute` extended with `long[] Replaces` and `string ReplacesRange` (both default empty/null; existing `[Migration(version)]` declarations unaffected).
- `ReplacesRangeParser` (internal) parses `"1000-1500, 1700, 1800-1850"` syntax to `SortedSet<long>` with whitespace tolerance, format-error messaging, and reversed-range detection.
- `MigrationLoadException` (new) raised at discovery time for: non-existent versions in Replaces/ReplacesRange (subset rule per ADR-0019), self-reference, and unresolved range endpoints.
- `MigrationRunner.DiscoverMigrations()` reorganized into a two-pass shape: raw discovery + duplicate check first, then per-descriptor `ResolveReplaces` against the in-scope assembly version set. The `MigrationDescriptor` carries the resolved sorted version set for Phase 3 reconciliation to consume.
- `MigrationApplyMode` enum (`Fresh`/`PartialCatchUp`) plus `MigrationContext` with AsyncLocal `Current`, scoped `Push` activation, and `IsFreshInstall` back-compat sugar.
- Runner classifies ledger state once at start (`IsLedgerEmptyAsync` probe) and pushes a `MigrationContext` scope around each `UpAsync` call. Phase 3 reconciliation will refine per-migration classification for squash rows.
- 11 new Phase 2 tests in `MigrationAttributeReplacesTests`: parser, discovery validation (non-existent / self-reference / range resolution), and `MigrationContext` push/pop semantics. All 17 squash tests pass (6 Phase 3 placeholders skipped).
- Existing 356 core unit tests: still green on net8/9/10.
- Build clean: 0 errors.

Phase 3: ☐ pending
Phase 3: ☐ pending
Phase 4: ☐ pending
Phase 5: ☐ pending
Phase 6: ☐ pending
Phase 7: ☐ pending
Phase 8: ☐ pending
