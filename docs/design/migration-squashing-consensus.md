# Consensus Design: Migration Squash (Multi-Provider Pressure-Test)

**Status:** Superseded — folded into [ADR-0019 destructive-model reframe](../decisions/0019-migration-squash-replaces-graph.md) on 2026-05-05
**Date:** 2026-05-05
**Inputs:** [Canonical design](migration-squashing.md) + [Assessment 0006](../research/0006-migration-squashing-assessment.md) + Round 1 friction analyses from 6 advocates (Aerospike, OpenSearch, MongoDB, Postgres, Couchbase, EF Core)
**Disposition:** Cross-cutting universal amendments U1–U11 + per-provider clarifications

> **NOTE 2026-05-05.** This consensus document captured the multi-advocate
> ratification of the **additive** squash model (originals stay during deprecation
> window). On the same day, after operator goal clarification, the design was
> reframed to a **destructive** Flyway/Atlas-style codegen-and-replace model.
> The cross-cutting amendments documented here (U1 `WritePrecondition`, U2
> realtime read obligation, U4 `MigrationApplyMode`, U5 DDL/data split, U6 lock
> TTL, U7 async-build barrier, U8 raw-bytes checksum, U9 per-provider snapshot
> scope, U10 EF migration bridge, U11 statements.json symmetry) all carry forward
> into the destructive model verbatim. The R-08 "originals stay" rule is the
> only consensus item that does NOT survive the reframe — it has been deleted
> and replaced with the fleet readiness check at squash creation. All other
> consensus content remains valid as cross-cutting design constraints. See
> ADR-0019 for the authoritative current decision; this document is retained for
> traceability of the multi-advocate analysis.

---

## Universal amendments (all advocates support)

### U1: Abstract CAS contract — `WritePrecondition` over per-provider tokens

**Friction sources:** A-N1 (Aerospike `gen`), O-N1 (OpenSearch `OpType=Create` first-write semantics), M-N1 (Mongo standalone duplicate-key vs. RS transaction), C-N2 (Couchbase `Insert` + `DocumentExistsException`), P-N4 (Postgres lock+PK).

**Consensus:** ADR-0021's auto-mark CAS spec lifts to an **abstract `WritePrecondition`** on `IMigrationRecordStore.WriteAsync`:

```csharp
public abstract record WritePrecondition
{
    public sealed record None : WritePrecondition;
    public sealed record MustNotExist : WritePrecondition;
    public sealed record MustMatchVersion(object OpaqueToken) : WritePrecondition;
}

public enum WriteOutcome
{
    Created,                  // expected single-writer success
    AlreadyExistsBenign,      // Insert hit duplicate-key; row matches our intent (checksum equals); treated as no-op success
    PreconditionFailed,       // hard error: row exists with different content
}
```

Per-provider implementation:

| Provider | `MustNotExist` | `MustMatchVersion(token)` |
|----------|----------------|---------------------------|
| OpenSearch | `OpType=Create` (409 → benign if checksum matches) | `if_seq_no` + `if_primary_term` (token = `(seq, term)`) |
| Aerospike | `WritePolicy.RecordExistsAction = CREATE_ONLY` | `WritePolicy.generation = N`, `generationPolicy = EXPECT_GEN_EQUAL` |
| MongoDB (standalone) | `InsertOne`; catch `DuplicateKey` → benign-or-hard classification | `findOneAndReplace` with `_id`+version filter |
| MongoDB (replica set, when `IClientSessionHandle` available) | Transactional `InsertOne` with read+write inside a session | Same with version filter |
| Couchbase | `Insert` (fail-if-exists) + classify `DocumentExistsException` | `Replace(key, value, cas)` |
| Postgres | `INSERT ... ON CONFLICT DO NOTHING` returning rowcount | `UPDATE ... WHERE checksum = $expected` returning rowcount |

The auto-mark write uses **`MustNotExist`**. Second writer's `AlreadyExistsBenign` outcome (with checksum equality verified) is no-op success; `PreconditionFailed` is a hard error.

### U2: `LoadAppliedVersionsAsync` realtime obligation

**Friction sources:** O-N3 (OpenSearch refresh interval), M-N2 (Mongo replica-set read concern), C-N5 (Couchbase mutation tokens).

**Consensus:** The bulk-existence read MUST use realtime point-lookup APIs, never eventually-consistent search.

```csharp
public interface IMigrationRecordStore
{
    Task<IReadOnlySet<string>> LoadAppliedVersionsAsync(
        IEnumerable<string> candidateIds,
        CancellationToken ct);
}
```

Per-provider:
- **OpenSearch:** `_mget` with `realtime: true` (default), explicit ID list. NOT `_search`.
- **MongoDB:** `find({_id: {$in: ids}})` with `ReadConcern.Majority` + `ReadPreference.Primary` on replica sets; `ReadConcern.Local` on standalone.
- **Couchbase:** KV `Get` per ID (or batch `MultiGet`), with `ScanConsistency.RequestPlus` if subsequent N1QL queries hit the ledger.
- **Postgres:** `SELECT record_id FROM ledger WHERE record_id = ANY($1)` — single round trip.
- **Aerospike:** `BatchGet` with strong consistency.

The signature takes **the candidate set** (not "all applied") so providers don't materialize large ledgers unnecessarily.

### U3: P0-2 strengthens from warning to hard refusal-to-start

**Friction sources:** every advocate endorsed this; O-N6 framed it as the "single safety net" for premature original deletion.

**Consensus:** Discovery validates that every value in any squash's `Replaces` resolves to a discovered migration descriptor in the loaded assemblies. If any value does not resolve, the runner refuses to start with `MigrationLoadException` naming the missing version, the squash that requires it, and the remediation ("the original migration must remain in source until `--prune` audit confirms fleet readiness").

Validation is **assembly-only at load time**. Reconciliation-time tolerance for "in ledger but not in assembly" is a separate (looser) check that fires only if the strict-subset path is reached and an original is missing — that's already the fail-loud path; no change needed.

### U4: `MigrationApplyMode` enum (richer than `IsFreshInstall` boolean)

**Friction sources:** M-N7 (Mongo richer context need), A-N3 (Aerospike fresh-install ambiguity).

**Consensus:** Replace IR-N3's `MigrationContext.IsFreshInstall` (bool) with:

```csharp
public enum MigrationApplyMode
{
    Fresh,          // no Replaces versions present in ledger; squash body running for the first time
    PartialCatchUp, // some but not all Replaces present; this migration is an unreplaced original being run
    // Auto-marked migrations never reach UpAsync, so no enum value needed for that case
}

public sealed class MigrationContext
{
    public MigrationApplyMode ApplyMode { get; init; }
    public bool IsFreshInstall => ApplyMode == MigrationApplyMode.Fresh; // back-compat sugar
    // ... existing context fields
}
```

`PartialCatchUp` lets authors gate operations that *should* re-run during catch-up (idempotent backfills) vs. operations safe only on fresh install (initial seeding). The `IsFreshInstall` getter is preserved for ergonomics; `ApplyMode` is the canonical signal.

### U5: DDL vs. data — `SquashHints` applies to data only; DDL is always preserved

**Friction sources:** E-N4 (EF Core advocate's explicit ask), implicit in P-N8 (Postgres SQL detector spec), C-N1 (Couchbase N1QL data verbs).

**Consensus:** ADR-0019 Decision 6 clarifies: **DDL is always preserved verbatim in rollup bodies. `SquashHints` (Elidable / Preserve / ReplayOnFreshOnly) applies only to data operations.** Vendor-specific DDL (Postgres triggers, RLS, partitions; OpenSearch ingest pipelines; MongoDB validators; Couchbase view definitions) is preserved by default — no annotation required.

Per-provider data-op verb lists (for the deferred Phase 2 heuristic detector):
- **Postgres:** `INSERT`, `UPDATE`, `DELETE`, `TRUNCATE`, `MERGE`, `COPY ... FROM`, `SELECT INTO`, `CREATE TABLE AS SELECT`.
- **MongoDB:** `insertOne/Many`, `updateOne/Many`, `deleteOne/Many`, `bulkWrite` (mutation operations only), `findOneAndUpdate/Replace/Delete`, aggregation pipelines containing `$out` or `$merge`.
- **Couchbase:** `INSERT INTO`, `UPSERT INTO`, `UPDATE`, `DELETE`, `MERGE INTO`.
- **OpenSearch:** `REINDEX` (data movement; opaque to fusion per existing design); `_bulk` index/update/delete operations.
- **Aerospike:** UDF-based mutations, `INSERT INTO` (AQL subset), batch put operations.

### U6: TTL-based locks must auto-renew or be configurably-long during partial-catch-up

**Friction sources:** A-N2 (Aerospike), C-N7 (Couchbase).

**Consensus:** ADR-0019 Consequences add: providers using TTL-based locks must either (a) auto-renew during reconciliation (matches existing Aerospike auto-renewing-lock pattern), or (b) document a `LockMaxLifetime ≥ EstimatedRollupCatchupDuration × 2` invariant. The runner must abort reconciliation cleanly on lock loss — never continue without serialization.

### U7: Async-build barrier during partial-catch-up

**Friction sources:** A-N6 (Aerospike SI build), C-N4 (Couchbase GSI build queue), implicit O-N3 (OpenSearch refresh).

**Consensus:** ADR-0019 Consequences add: providers with asynchronous background operations (Aerospike secondary indexes, Couchbase GSI build queue, OpenSearch refresh interval) MUST honour their per-migration completion barriers during partial-catch-up. Specifically: `WriteAsync` for a migration that creates a non-synchronous resource MUST block until the resource is ready. Skipping readiness polls in catch-up mode is forbidden.

Per-provider concrete:
- Aerospike: existing `info("sindex")` polling until `state=RW` before `WriteAsync` returns.
- Couchbase: `WaitUntilReadyAsync` on indexes before `WriteAsync` returns.
- OpenSearch: `?refresh=wait_for` or explicit `_refresh` call before reconciliation continues.

### U8: Checksum is over RAW resource bytes (pre-substitution)

**Friction sources:** C-N6 (Couchbase parameterized statements).

**Consensus:** ADR-0021 explicit clause: "The checksum is computed over the raw resource bytes as they appear in the assembly. Per-environment substitution (typed options, configuration values) is NOT included. The checksum captures the migration as authored, not as executed in any specific environment."

### U9: Snapshot fidelity is per-provider, not universal

**Friction sources:** C-N (Couchbase explicit "no Phase 2 generator"), M-N3 (Mongo strategy under-specified), O-N (OpenSearch hybrid AST + REST), implicit Aerospike.

**Consensus:** Each provider's Phase 2 strategy declares an **explicit out-of-scope manifest** alongside its capture spec. The CLI surfaces it on use:

```
$ dotnet hyperbee-migrations squash --provider mongodb --range 1000-1100
[mongodb] Strategy: IntrospectionSnapshotStrategy
[mongodb] In scope: collections (options, validators, view defs), indexes (all options), TTL.
[mongodb] OUT OF SCOPE: sharding metadata, GridFS, change streams, server-side functions, roles/users.
[mongodb] If your migration sequence used any out-of-scope features, hand-author or extend the strategy.
[mongodb] Continue? [y/N]
```

The "comprehensive snapshot" claim is removed from the design; replaced with "scope-explicit snapshot."

### U10: EF Core migration bridge — separate guide

**Friction sources:** E-N3.

**Consensus:** Add `docs/guides/migrating-from-ef-core.md` (deferred deliverable; not v1 blocker but tracked):
- How to bridge a `__EFMigrationsHistory` table to `MigrationRecord`
- Recommended workflow: synchronize fleet to known EF version → introduce hyperbee `[Migration(N, Replaces=[…all-prior-EF-versions…])]` baseline → use `--accept-unverified-version` allowlist for the EF-era nulls
- Honest framing: hyperbee is migration-first, not model-first; refugees recalibrate from `Add-Migration` ergonomics

### U11: Strategy output symmetry — emit `statements.json`, not code-only

**Friction sources:** M-N over-served (Mongo proposed code-only); proposed shift to symmetric `statements.json`.

**Consensus:** Phase 2 strategies for NoSQL providers (MongoDB, Couchbase) emit `statements.json` resources (parseable by their existing Parlot grammars) rather than code-only Migration classes. Symmetric with Postgres `.sql` resources. Rationale: post-editable by the author; integrates with existing resource pipeline; consistent operator experience.

OpenSearch AST fusion strategy already produces `statements.json`-shaped output via the existing AST → serialization pipeline. Aligned.

---

## Provider-specific clarifications

### Aerospike

- Hand-authored is the steady state. **No Phase 2 generator** unless a consumer asks.
- Default checksum strategy notes the weak-checksum fallback for code-only migrations (custom `IChecksumStrategy<TMigration>` available for stronger integrity).
- `MigrationApplyMode.Fresh` = ledger-empty, NOT database-empty. (Aerospike namespaces are config-time.)
- Document Aerospike-specific guidance: data-bearing originals should also gate on `set` emptiness via `info("sets/...")` if not idempotent against pre-existing data.

### OpenSearch

- Auto-mark write semantics clarified: `OpType=Create` for first write (409 → benign if checksum matches); `if_seq_no`/`if_primary_term` only on update-after-existing-write retries.
- ADR-0018 cross-index hazard: auto-mark must be preceded by lock-aliveness check (re-read lock doc, verify `_seq_no`+`_primary_term` match values captured at acquisition). On mismatch → `LockLostException`, abort.
- ADR-0021 auto-mark contract notes within-ledger races serialized at primary-shard for the row's `_id`; cross-index hazards (lock rotation) require U1's `MustNotExist` + the lock-aliveness check.
- ISM policy / template / ingest pipeline drift: ADR-0021 Consequences explicit — auto-mark guarantees only that `Replaces` ledger rows exist with matching checksums, NOT that current cluster state matches post-migration intent.
- AWS Managed `AssumeIndicesExist=true` interaction: rollup `UpAsync` (fresh-install path) has same resource-creation requirements as the original chain — operators in restricted environments must verify executability or use snapshot baseline.
- ADR-0020 strengthens for OpenSearch specifically: even with R-19 statement-level inverses on originals, rollback across squash boundary remains unsupported. Originals' Down preserved during deprecation window for forward catch-up only.
- Phase 2 strategy: AST fusion preferred (preserves intent); REST snapshot top-up for resources outside the AST (ingest pipelines, role mappings). Hybrid.

### MongoDB

- `IntrospectionSnapshotStrategy` explicit scope:
  - **Captured:** collections (capped/size/max, timeseries config, clusteredIndex, collation, validator/validationLevel/validationAction, viewOn+pipeline, write concern), indexes (all options including partial filter, TTL, collation, hidden, wildcard).
  - **Out of scope:** sharding metadata, GridFS, change streams, server-side functions, roles/users.
- JSON Schema validators normalized before checksum: sort `properties` keys alphabetically, sort `required` arrays, canonicalize `bsonType` (single string when length-1 array). Documented in ADR-0021's MongoDB-specific note.
- Topology-aware auto-mark: standalone uses U1's `MustNotExist` via duplicate-key; replica sets (when `client.Cluster.Description.Type == ClusterType.ReplicaSet`) use transactional read+write inside `IClientSessionHandle`.
- `LoadAppliedVersionsAsync` reads with `ReadConcern.Majority` + `ReadPreference.Primary` on replica sets; `ReadConcern.Local` on standalone.
- Strategy emits `statements.json` (Parlot-parseable Mongo-shell-like statements), symmetric with Postgres.

### Postgres

- `PgDumpSnapshotStrategy` post-processing pipeline (per P-Postgres advocate's spec):
  1. Strip `SET` preamble (incl. dangerous `SELECT pg_catalog.set_config('search_path', '', false)`).
  2. Strip blank/comment-only lines (idempotent normalization).
  3. Extract `CREATE EXTENSION` to separate `*.prerequisites.sql` resource (operator runs once, requires elevated role).
  4. Detect/refuse `CREATE INDEX CONCURRENTLY` (cannot run in transaction).
  5. Validate no role refs survive (regex for `OWNER TO`, `SET ROLE`, `TO <ident>` in CREATE POLICY).
  6. Emit UTF-8 LF-line-ended SQL for hash stability.
- `pg_dump` invocation: `--schema-only --no-owner --no-privileges --no-comments --no-publications --no-subscriptions --no-security-labels --quote-all-identifiers`.
- Round-trip verification (Phase 2): **dump-vs-dump in-process diff**, not apgdiff/pg_diff (both fragile/abandoned). Run originals→container A, rollup→container B, `pg_dump --schema-only` both, normalize, byte-compare.
- Strict-subset partial-catch-up: each migration in its own transaction (matches existing provider behavior).
- Recommend (separate tracking ticket): replace lock-table with `pg_advisory_lock(<hash-of-LockName>)` for cleaner crash-recovery. Out of rollup scope; flagged.
- `kind smallint NOT NULL DEFAULT 0` ledger column gets `CHECK (kind IN (0,1,2))` constraint (per Postgres advocate's plan-time ask).
- Multiple `.sql` files per migration: hash uses ordinal byte-by-byte invariant-culture sort of resource names (cross-platform stability).

### Couchbase

- **No Phase 2 generator** unless a consumer demands one. Hand-authored is the steady state.
- If a generator ever ships: explicit partial — `system:indexes` + `system:scopes` + `system:collections` + bucket settings via management REST. **Out of scope:** FTS indexes, Eventing functions, Analytics views, XDCR, server-side authentication.
- Auto-mark uses U1's `MustNotExist` via `Insert` + `DocumentExistsException` classification.
- Mutation token consistency: writes during reconciliation use ≥`Majority` durability; subsequent N1QL queries against ledger use `ScanConsistency.RequestPlus`.
- `MigrationApplyMode.Fresh` = "ledger empty after 7-state bootstrap completes," NOT "database virgin."
- Bucket-creating rollups: documented bootstrap-ordering rule — ledger lives in a *separate management bucket* OR bucket-creating migrations are non-rollup-eligible and run before any squash chain.

### EF Core (refugee experience)

- "Comparison to EF Core" section in the design doc (or top-level guide) names the model-snapshot absence as deliberate framework difference. Hyperbee = "EF's `migrationBuilder.Sql(...)` escape hatch all the way down."
- ADR-0020 Positive Consequence: "Original migrations' `DownAsync` implementations remain functional for environments that have not yet auto-marked the squash. Down across the squash boundary is what's lost; per-original Down is preserved during the deprecation window."
- `Replaces = new long[] {…}` array tedious for large squashes; flag analyzer/code-fix that auto-generates from discovered versions in a range as v1.1 polish.
- Error-message friction: `--accept-unverified-version` error must include exact CLI snippet to copy-paste with all problematic versions enumerated.
- DDL vs. data clarification (U5) directly addresses E-N4.

---

## Required ADR / artifact edits (deltas beyond Assessment 0006's already-required edits)

| Artifact | Additional edit |
|----------|-----------------|
| ADR-0019 | (a) U4 `MigrationApplyMode` enum replacing IR-N3's bool. (b) U5 DDL-vs-data clarification. (c) U6 lock TTL + auto-renewal invariant. (d) U7 async-build barrier invariant. (e) U9 per-provider snapshot scope language. (f) U10 EF migration bridge guide commitment. |
| ADR-0020 | EF positive consequence about per-original Down preservation. |
| ADR-0021 | (a) U1 `WritePrecondition` abstract contract + per-provider implementation table. (b) U2 `LoadAppliedVersionsAsync(candidateIds)` realtime obligation. (c) U8 raw-resource-bytes-pre-substitution clause. (d) MongoDB JSON Schema normalization clause. (e) OpenSearch lock-aliveness check + within-ledger CAS clause. |
| Design doc | (a) Provider-specific clarifications section (per-provider). (b) Per-provider data-op verb lists. (c) Pseudocode update reflects U1+U2. (d) Strategy output symmetry: NoSQL strategies emit `statements.json`. |
| `IMigrationRecordStore` | New API: `WriteAsync(record, WritePrecondition, ct) → WriteOutcome` and `LoadAppliedVersionsAsync(candidateIds, ct)`. |
| `MigrationContext` | New: `ApplyMode` property of `MigrationApplyMode` enum; `IsFreshInstall` becomes back-compat sugar. |

---

## Open items for Round 3 ratification

These are points where Round 1 outputs *imply* consensus but no advocate explicitly affirmed:

1. **Postgres**: confirm `INSERT ... ON CONFLICT DO NOTHING` returning rowcount maps cleanly to `WritePrecondition.MustNotExist`.
2. **MongoDB**: confirm topology auto-detection via `client.Cluster.Description.Type` is a stable API across driver versions.
3. **Couchbase**: confirm "ledger lives in separate management bucket" doesn't require schema changes for existing Couchbase consumers.
4. **OpenSearch**: confirm lock-aliveness check fits within existing `OpenSearchRecordStore.CreateLockAsync` + `LockHandle` flow without re-architecting.
5. **Aerospike**: confirm "code-only checksum is documented-weaker" framing is acceptable rather than an integrity gap requiring v1 work.
6. **EF Core**: confirm the migration bridge guide is a v1.1 deliverable, not a v1 blocker (i.e., we ship v1 without it).
