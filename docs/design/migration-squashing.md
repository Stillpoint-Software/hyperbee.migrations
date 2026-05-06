# Design: Migration Squashing for Hyperbee.Migrations

**Status:** Proposed (destructive-model reframe 2026-05-05; hardened with Assessment 0007 amendments 2026-05-05)
**Date:** 2026-05-04 (original) / 2026-05-05 (reframe + hardening)
**Requirements:** [docs/requirements/migration-squashing.md](../requirements/migration-squashing.md)
**Research:** [docs/research/0005-migration-squashing.md](../research/0005-migration-squashing.md)
**Multi-advocate consensus:** [docs/design/migration-squashing-consensus.md](migration-squashing-consensus.md) (cross-cutting amendments U1–U11)
**EF Core reference:** [docs/research/ef-core-squash-reference.md](../research/ef-core-squash-reference.md)
**Related ADRs:** ADR-0019 (Replaces-graph + destructive codegen), ADR-0020 (Up-only squashes), ADR-0021 (record checksum)

> **NOTE 2026-05-05 — Destructive model reframe.** The squash workflow described in
> later sections of this document was originally framed additively (originals stay
> in source during a deprecation window). After review, the operator's stated goals
> — clean up potentially thousands of old migrations; improve provisioning time;
> improve seeding clarity — were determined to be unachievable in the additive
> model. The design has been reframed to a **destructive Flyway/Atlas-style
> codegen-and-replace** workflow. The canonical decision is captured in
> [ADR-0019](../decisions/0019-migration-squash-replaces-graph.md). The sections
> below retain the additive-model exposition for historical context; readers should
> treat ADR-0019 as authoritative wherever the two diverge.

## Selected Approach (DESTRUCTIVE MODEL — 2026-05-05 reframe)

The squash is an **operator-initiated codegen action** that produces a single migration replacing a contiguous range of originals:

1. Operator selects range `[N..M]` and runs `dotnet hyperbee-migrations squash --range N-M --provider <p>`.
2. Tool spins ephemeral provider container, applies migrations `< N` to capture **snapshot A** (state at start of range).
3. Tool applies migrations `[N..M]` to capture **snapshot B** (state at end of range).
4. Tool diffs A and B via the provider's `ISquashStrategy` to produce the delta.
5. Tool runs **fleet readiness check** against a manifest of known environment ledgers — refuses if any environment is mid-range (between N and M).
6. If green, tool emits `Squash_M.cs` (with `[Migration(M+ε, ReplacesRange = "N-M")]`) and the diff resource, and **removes original migrations `[N..M]` from the source tree**.

Reconciliation when the deployed code reaches each environment:

- **Mature env** (had `[N..M]` applied before squash shipped): ledger contains rows for replaced versions → auto-mark, no body run.
- **Fresh env** (empty ledger; originals no longer in source): apply residual head (versions `< N`), then run squash body (the A→B delta).
- **Mid-range env**: refused with `MidRangeSquashException` (this should not occur if fleet readiness check was honored).

The Django-style `Replaces` graph remains for ledger-level auto-mark; what *changes* from the additive model is that the source tree is genuinely compacted and operators do not maintain a deprecation window. Per-original ledger history is preserved indefinitely as audit trail.

See [ADR-0019](../decisions/0019-migration-squash-replaces-graph.md) for the full decision, including the `ISquashStrategy` contract, the fleet-readiness manifest format, and the reconciliation pseudocode.

---

## Original Selected Approach (ADDITIVE — superseded 2026-05-05; retained for historical context)

**Universal Replaces-Graph Scaffolding (Candidate A) crossed with a deferred per-provider strategy plugin contract (Candidate C).** The runner-side mechanism (extend `[Migration]` with `Replaces`, add `Checksum` to `MigrationRecord`, auto-mark squashes when their replaces-set is fully present) is provider-agnostic and ships as the v1 universal floor. A per-provider `ISquashStrategy` plugin contract is *defined* in v1 but no provider is required to implement it. **Phase 2 first-target is Postgres** (mainstream in the .NET ecosystem; `pg_dump --schema-only` is mature snapshot tooling; output integrates directly with the existing `PostgresResourceRunner.AllSqlFromAsync` pattern); **MongoDB is the second target** (programmatic introspection via `listCollections`/`listIndexes`/validators). OpenSearch AST fusion remains a viable strategy for OpenSearch users who eventually need it but is not privileged. Other providers participate via hand-authored squashes using the universal scaffolding — the experience is identical for the *author* across providers; only the *generation tooling* varies.

This honours the user's "any provider can implement and works equally well" constraint at the layer that matters: the runner sees, journals, and reconciles a squash the same way regardless of provider, and the author writes a `[Migration(version, Replaces = …)]` class the same way regardless of provider.

## Fitness Evaluation Summary

Four candidates evaluated. Inline evaluation (not sub-agent) per the skill's guidance for ≤4 candidates.

| Candidate | Req. Compliance | ADR Compliance | Temporal | Interface | Scale | Design | Overall |
|-----------|-----------------|----------------|----------|-----------|-------|--------|---------|
| **A: Universal Hand-Authored Baselines** | 88% (all of Themes 1, 2, 4; Theme 3 deferred) | ✓ all | High | Small | Medium (human-time at 100+ migrations) | Clean | **Strong** |
| **B: Universal Snapshot Diff** | 75% (loses Theme 2's elidable mode; data migrations silently dropped) | ✓ all | Medium (snapshot mechanisms decay with vendor changes) | Large (silent data-migration loss) | Medium | Tangled (core couples to per-provider introspection) | Mixed |
| **C: Per-Provider Strategy Plugins** | 100% (Themes 1–4 all addressable) | ✓ all (extension on top of A) | Medium (per-provider strategies decay independently) | Medium (mixed-fidelity foot-gun: same CLI, different guarantees per provider) | High (each strategy scales by its own model) | Clean (plugin architecture) | Strong but premature for v1 |
| **D: Replay-Recorder** | 70% (no data-migration mode taxonomy; recording captures noise) | Risk to ADR-0011/0015 (transport-layer interception is a runtime concern, not parse-time) | Low (per-provider mutation-capture is fragile; secret-leak risk in recording) | Large (recording captures auth headers, request IDs, timing) | Low (recording overhead is N× original network volume) | Tangled (transport interceptors per provider) | Weak |

**Selection:** A and C have complementary strengths. A is the simplest and most universal; C provides the optional-automation ceiling that A lacks. Crossover: ship A's scaffolding now, define C's plugin contract as a forward-looking extension point but don't require any provider to implement it. The hybrid scores ~95% requirement compliance with A's temporal stability and C's design fitness — strictly better than either parent.

B is rejected on requirements compliance (loses elidable mode) and temporal fitness (snapshot decay).
D is rejected on ADR risk and interface fitness — the recording approach also raises a secret-leak hazard the team has explicitly designed against in OpenSearch (per OpenSearch assessment 0002's secret-scrubbing finding).

## Architecture

### Layer 1 — Universal Scaffolding (ships in v1, all providers)

Three additive changes to the core, none breaking any existing ADR:

#### 1.1 Extend `MigrationAttribute`

```csharp
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class MigrationAttribute : Attribute
{
    public long Version { get; }
    public string[] Profiles { get; init; } = Array.Empty<string>();

    // NEW: versions that this migration subsumes when applied as a squash.
    // Empty = regular migration. Non-empty = squash migration.
    public long[] Replaces { get; init; } = Array.Empty<long>();

    public MigrationAttribute(long version) => Version = version;
}
```

Per the user's directive, `Replaces` is a named optional parameter on the existing attribute. No new attribute introduced. Discovery via reflection in [`MigrationRunner.cs:160-189`](../../src/Hyperbee.Migrations/MigrationRunner.cs) needs only one additional projection of `attribute.Replaces` into the discovered descriptor.

#### 1.2 Extend `MigrationRecord`

```csharp
public sealed record MigrationRecord
{
    public string Id { get; init; } = default!;
    public DateTimeOffset RunOn { get; init; }

    // NEW
    public string? Checksum { get; init; }                      // SHA-256 hex; null = pre-checksum era
    public MigrationRecordKind Kind { get; init; } = MigrationRecordKind.Migration;
}

public enum MigrationRecordKind
{
    Migration = 0,
    Squash = 1,
    Baseline = 2,
}
```

Existing providers' `IMigrationRecordStore` implementations require minor changes — Postgres adds two columns, OpenSearch and MongoDB are JSON-shaped and additive, Couchbase and Aerospike likewise. ADR-0003's contract is *additively* extended; existing rows with null checksum and absent kind are tolerated.

#### 1.3 Reconciliation Logic in `MigrationRunner`

For each discovered migration with non-empty `Replaces`:

```csharp
// Pseudocode for the reconciliation pass
var allReplacesApplied = await migration.Replaces
    .All(async v => await store.ExistsAsync(IdFor(v)));

if (allReplacesApplied)
{
    // Auto-mark: write the squash record without running UpAsync.
    await store.WriteAsync(new MigrationRecord
    {
        Id = IdFor(migration.Version),
        RunOn = clock.UtcNow,
        Checksum = ComputeChecksum(migration),
        Kind = MigrationRecordKind.Squash
    });
    continue;
}

var partial = migration.Replaces.Any(v => store.Exists(IdFor(v)));
if (partial)
{
    // Strict subset — let the unreplaced originals run individually first.
    // The squash will be reconciled on a later pass.
    EnqueueOriginalsBeforeSquash(migration);
    continue;
}

// Fresh install — run normally.
await migration.UpAsync(/* ctx */);
await store.WriteAsync(/* with Kind = Squash, Checksum = … */);
```

This is provider-agnostic — every provider gets the same Django-style behavior automatically because everything routes through `IMigrationRecordStore`.

### Layer 2 — Optional Per-Provider Strategy Contract (defined in v1, implementations follow demand)

```csharp
public interface ISquashStrategy
{
    /// <summary>
    /// Author-time tooling: given a contiguous range of migrations,
    /// produce a single squash migration that subsumes them.
    /// May return Unsupported to signal "use hand-authored squashes."
    /// </summary>
    Task<SquashGenerationResult> GenerateAsync(
        IReadOnlyList<MigrationDescriptor> sourceRange,
        SquashGenerationOptions options,
        CancellationToken ct);
}

public abstract record SquashGenerationResult
{
    public sealed record Generated(string MigrationCode, IReadOnlyList<long> Replaces) : SquashGenerationResult;
    public sealed record Unsupported(string Reason) : SquashGenerationResult;
    public sealed record Failed(string Detail, Exception? Cause) : SquashGenerationResult;
}
```

In v1, every provider's DI registration registers `Unsupported("hand-author squashes for this provider")` by default. Providers may override with a real strategy when justified.

**Phase 2 strategy implementations, in priority order:**

1. **Postgres** (`PgDumpSnapshotStrategy`) — first target. Shells out to `pg_dump --schema-only --no-owner --no-privileges`, post-processes the output (strips comments, normalizes formatting), wraps it as a `Migration` class whose `UpAsync` invokes `PostgresResourceRunner.AllSqlFromAsync` over the embedded SQL resource. Round-trip verification (R-13) compares the live schema after applying originals vs. after applying the squash via `pg_dump` diff or `apgdiff`.
2. **MongoDB** (`IntrospectionSnapshotStrategy`) — second target. Programmatic introspection: `db.runCommand({listCollections: 1, options: 1})` for collection options + validators, `db.<col>.getIndexes()` for indexes, optional capture of role/permission state when consumer opts in. Emits a code-only Migration whose `UpAsync` invokes the MongoDB driver to recreate the captured state.
3. **OpenSearch** (`AstFusionStrategy`) — viable for OpenSearch users who hit the migration count. The AST advantage from research Finding 7 still applies; it's just no longer the privileged first-target because the user base hitting migration-count pain points is much smaller for OpenSearch than for Postgres.
4. **Couchbase, Aerospike** — no immediate Phase 2 plan. Hand-authored baselines remain the supported path; strategies may be added if/when a concrete consumer asks.

### Component Sketch

```
┌────────────────────────────────────────────────────────────────┐
│  AUTHOR (any provider)                                         │
│    [Migration(1100, Replaces = new[] { 1000, 1010, 1020 })]   │
│    public class ConsolidatedSetup : Migration { … }           │
└──────────────────────┬─────────────────────────────────────────┘
                       │ ships in source tree alongside originals
                       ▼
┌────────────────────────────────────────────────────────────────┐
│  CORE — MigrationRunner                                        │
│    DiscoverMigrations() → descriptors with Replaces            │
│    ReconcileAsync() → auto-mark | fresh-install | partial      │
│    Computes Checksum on every write                            │
└──────────────────────┬─────────────────────────────────────────┘
                       │ via existing IMigrationRecordStore
                       ▼
┌────────────────────────────────────────────────────────────────┐
│  PROVIDER (any) — record store                                 │
│    OpenSearch / Postgres / MongoDB / Couchbase / Aerospike    │
│    All gain Checksum + Kind columns (additively)              │
└────────────────────────────────────────────────────────────────┘

══════ Phase 2 (deferred) ══════════════════════════════════════════

┌────────────────────────────────────────────────────────────────┐
│  CLI: dotnet hyperbee-migrations squash                        │
│    Loads provider's ISquashStrategy                           │
│    OpenSearch: AST fusion via Internal/Middleware              │
│    Other providers: returns Unsupported by default            │
└────────────────────────────────────────────────────────────────┘
```

## Per-Provider Capability Assessment

The design must work across all five current providers — they represent the variance in schema model, statement grammar, resource model, bootstrap complexity, and Down-support depth that any future provider is likely to fall within. This section demonstrates the assessment.

### Variance matrix

| Capability | Aerospike | Couchbase | MongoDB | OpenSearch | Postgres |
|------------|-----------|-----------|---------|------------|----------|
| **Schema breadth** | Thin (namespaces, sets, secondary indexes) | Medium (buckets, scopes, collections, indexes) | Medium (collections, indexes, validators) | Rich (indices, mappings, templates, component templates, ISM policies, aliases, ingest pipelines) | Richest (tables, columns, indexes, FK, constraints, triggers, sequences, views, RLS) |
| **Statement grammar** | Partial (AQL subset, Parlot) | Partial (N1QL subset, Parlot) | Partial (Mongo shell-like, Parlot) | **Full AST** (21 statement types, Parlot) | None — raw SQL files |
| **Resource model** | `statements.json` + Parlot | `statements.json` + Parlot | `statements.json` + Parlot | `statements.json` + Parlot | Raw `.sql` files via `AllSqlFromAsync` |
| **Bootstrap complexity** | Custom index-ready polling | 7-state cluster bootstrapper | Minimal | State-machine over `IBootstrapStep[]` | Minimal |
| **Lock primitive** | Document-with-TTL (auto-renewing) | Mutex over key | CAS via `_id`+version | CAS via `if_seq_no`/`if_primary_term` (split lock/ledger per ADR-0018) | Row-with-lease in lock table |
| **Down support depth** | `DownAsync` virtual; rare | `DownAsync` virtual; rare | `DownAsync` virtual; rare | **Formal** (R-19 partial-rollback ledger, statement-level inverses, `OpenSearchPartialRollbackException`) | `DownAsync` virtual; reverse SQL |
| **Snapshot mechanism** | `info("namespaces")` + `info("sindex")` | `system:indexes` + scope/collection enumeration | `listCollections` + `listIndexes` + validators | `_cat/indices` + `GET _mapping` + ISM/template/alias/ingest APIs | `pg_dump --schema-only` |

### Phase 1 participation (universal scaffolding, all providers)

| Concern | Aerospike | Couchbase | MongoDB | OpenSearch | Postgres |
|---------|-----------|-----------|---------|------------|----------|
| **`Replaces` discovery** | Reflection-driven, identical for all | ✓ | ✓ | ✓ | ✓ | ✓ |
| **`Checksum` compute** (default = SHA-256 over resource bytes) | Hash `statements.json` | Hash `statements.json` | Hash `statements.json` | Hash `statements.json` (+ optional bodies/templates) | Hash concatenated `.sql` files |
| **`MigrationRecord` schema bump** (additive: `Checksum`, `Kind`) | Add fields to bin model | Add fields to JSON document | Add fields to JSON document | Add fields to ledger index mapping (separate from lock per ADR-0018) | Add two columns (`checksum text`, `kind smallint`) |
| **Auto-mark on full `Replaces`-set match** | `MigrationRunner` does the work; provider just persists | ✓ | ✓ | ✓ | ✓ | ✓ |
| **Pre-checksum-era null tolerance** | Provider read tolerates missing field | ✓ | ✓ | ✓ | ✓ (default null on existing rows) | ✓ |
| **Down refusal on squashes** (ADR-0020) | Reuses existing `Migration.DownAsync` pathway; framework refuses | ✓ | ✓ | ✓ (uses existing `RollbackNotSupportedException`) | ✓ | ✓ |

**Verdict for Phase 1:** No provider-specific contortions. Every provider implements the same additive ledger schema bump and gets auto-mark behavior for free via the runner. The Down-refusal logic reuses OpenSearch's existing `RollbackNotSupportedException` (already in the core via `Hyperbee.Migrations.Providers.OpenSearch.OpenSearchExceptions`); the type can move to core or be duplicated trivially.

### Phase 2 strategy fit (each provider can ship a generator when justified)

| Provider | Recommended strategy shape | Snapshot fidelity | Author burden without strategy | Phase 2 priority |
|----------|---------------------------|-------------------|-------------------------------|------------------|
| **Aerospike** | Either hand-authored (genuinely sufficient given schema thinness) OR minimal `InfoSnapshotStrategy` (`info("namespaces")` + `info("sindex")`) | Captures all queryable schema | Trivial — schema is so thin that 5–10 lines of code recreates it | Low — no plan unless a consumer asks |
| **Couchbase** | `IntrospectionSnapshotStrategy` (system:indexes + scope/collection enumeration) | Captures buckets, scopes, collections, indexes | Moderate — the bucket/scope/collection metadata is non-trivial to hand-write | Low–Medium — defer until consumer demand |
| **MongoDB** | `IntrospectionSnapshotStrategy` (listCollections + listIndexes + validators) | Captures collection options, indexes, JSON-schema validators | Moderate — validators in particular are easy to mis-transcribe | **Medium (Phase 2 second target)** |
| **OpenSearch** | `AstFusionStrategy` (preferred — exploits the existing AST per research Finding 7) OR `RestApiSnapshotStrategy` (alternative — `_cat/indices` + per-index `_mapping` + ISM/template/alias/ingest APIs) | Either approach: rich schema captured | High — the rich schema (templates, ISM policies, aliases) is tedious to hand-author | Lower — viable but smaller migration-bloat user base than Postgres |
| **Postgres** | `PgDumpSnapshotStrategy` (`pg_dump --schema-only --no-owner --no-privileges`) | Captures full relational schema | High — manual `pg_dump` works but doesn't integrate with verification or `Replaces` wiring | **High (Phase 2 first target)** |

The `ISquashStrategy` contract `GenerateAsync(sourceRange) -> Generated | Unsupported | Failed` is intentionally minimal so each provider can use whatever shape makes sense — pure introspection (Mongo, Couchbase), shell-out (Postgres), AST fusion (OpenSearch), or info-command-based (Aerospike). `Unsupported` is a first-class return — providers without an implementation are not penalized; their authors hand-write squashes using the universal scaffolding (Example 1).

### Cross-cutting design notes

- **The default `Checksum` strategy is resource-bytes hashing.** This works identically for the four NoSQL providers (all use `statements.json` resources) and for Postgres (raw `.sql` files). Code-only migrations across all providers fall back to the documented-weaker name+version strategy (per ADR-0021); authors who care can override per-provider via `IChecksumStrategy<TMigration>`.
- **The Down-refusal hard-edge (ADR-0020) reuses OpenSearch's existing `RollbackNotSupportedException`.** That type was provider-local; moving it to core (or aliasing in core) is a small cleanup with no functional change for the existing OpenSearch users.
- **The strict-subset partial-catch-up case (Example 4) works for all providers identically** because reconciliation is in `MigrationRunner`, which already handles per-migration ordering provider-agnostically. The `SquashHints.ReplayOnFreshOnly` mode (per Decision 6) is the safety valve for data-bearing migrations across all providers.
- **Round-trip verification (R-13) requires every provider to expose a `CaptureAsync` that produces a comparable state description.** The fidelity varies (Postgres can capture everything via `pg_dump`; Aerospike captures very little). The verification step compares whatever the provider can capture; consumers needing higher-fidelity verification can ship custom captures. **This is the non-trivial provider-specific concern in the design;** it's addressed by treating `IProviderSnapshot` as a separate optional contract from `ISquashStrategy` (a strategy may choose not to verify, in which case the author must verify manually).

### Constraint on `/nop:plan`

The downstream plan must apply this same cross-provider lens to every task. Specifically, every task in Phase 1's vertical slices must demonstrate:

1. How Aerospike, Couchbase, MongoDB, OpenSearch, and Postgres each absorb the change.
2. Where any provider requires a deviation, the deviation is documented and justified (likely an ADR addendum).
3. Test coverage spans at least two providers per slice — one rich-schema (Postgres or OpenSearch) and one thin-schema (Aerospike or MongoDB) — to catch shape assumptions early.

Plan tasks that pass the cross-provider lens trivially (e.g., "extend `[Migration]` with `Replaces`") need only a one-line confirmation; tasks that look provider-shaped (e.g., "add `Checksum` column to `MigrationRecord`") require an explicit per-provider participation note.

## Examples

### Example 1 — Hand-Authored Squash (the universal v1 experience)

A team has 30 migrations on a Postgres database that's accumulated since 2024. They want to roll up `1000..1100` into a single migration. They author:

```csharp
namespace MyApp.Migrations;

[Migration(version: 2000, Replaces = new long[] {
    1000, 1010, 1020, 1030, 1040,
    1050, 1060, 1070, 1080, 1090, 1100
})]
public class Baseline_2024 : Migration
{
    private readonly PostgresResourceRunner<Baseline_2024> _runner;
    public Baseline_2024(PostgresResourceRunner<Baseline_2024> runner) => _runner = runner;

    public override async Task UpAsync(CancellationToken ct = default)
    {
        // Hand-authored consolidated SQL — typically lifted from
        // pg_dump --schema-only of a clean test database that had
        // migrations 1000..1100 applied.
        await _runner.AllSqlFromAsync(typeof(Baseline_2024).Assembly, ct);
    }
}
```

The `Baseline_2024.sql` resource file contains the hand-authored consolidated DDL. Originals (`Migration_1000.cs` … `Migration_1100.cs`) **stay in the source tree** during the deprecation window (R-08).

### Example 2 — Reconciliation: Mature Environment (auto-mark, no body run)

Production has been on this codebase for two years; its ledger contains all 11 records for versions 1000..1100. Deploying with the new squash migration:

```
[INFO] MigrationRunner discovered 12 migrations (11 originals + 1 squash version 2000).
[INFO] Migration 2000 (Baseline_2024) declares Replaces=[1000, 1010, …, 1100].
[INFO] All 11 replaced versions present in ledger. Skipping UpAsync; recording as applied.
[INFO] Wrote ledger row: Id=Record.2000.baseline-2024, Kind=Squash, Checksum=a3f9…
[INFO] Reconciliation complete: 0 migrations executed, 1 auto-marked, 0 pending.
```

The production database is unmodified — the squash body never ran. The audit trail now contains both the original 11 records and a 12th record showing the squash is the canonical state going forward. Zero coordination, zero hand-stamping.

### Example 3 — Reconciliation: Fresh Install (run UpAsync)

A new developer clones the repo and starts a local Postgres container. Their ledger is empty:

```
[INFO] MigrationRunner discovered 12 migrations.
[INFO] Migration 2000 (Baseline_2024) declares Replaces=[1000, …, 1100].
[INFO] No replaced versions present. Running UpAsync (fresh-install fast-path).
[INFO] Applied 1 migration in 4.2s (vs. estimated 38s for the full original chain).
```

Fresh installs bypass the long original chain. The squash body runs once; ledger gets one row.

### Example 4 — Reconciliation: Partial Catch-Up (originals run first)

A staging environment is at version 1050 (six originals applied). Deploying the new release:

```
[INFO] MigrationRunner discovered 12 migrations.
[INFO] Migration 2000 (Baseline_2024) Replaces partially applied (6/11 in ledger).
[INFO] Running unreplaced originals 1060..1100 first.
[INFO] Applied migrations 1060, 1070, 1080, 1090, 1100 (5 migrations in 12.4s).
[INFO] Migration 2000 Replaces now fully satisfied (11/11 in ledger). Auto-marking.
[INFO] Wrote ledger row: Id=Record.2000.baseline-2024, Kind=Squash.
[INFO] Reconciliation complete: 5 executed, 1 auto-marked.
```

Staging catches up via the originals (which are still in the source tree), then auto-marks the squash. No coordination needed.

### Example 5 — Data Migration with Elidable Mode

A migration contains both schema and data operations. The author marks the data op explicitly:

```csharp
[Migration(version: 1075)]
public class BackfillUserDisplayNames : Migration
{
    public override async Task UpAsync(CancellationToken ct = default)
    {
        // Schema op — always preserved through squashes.
        await _runner.SqlFromAsync("AddDisplayNameColumn.sql", ct);

        // Data op — author declares it elidable: squashes on fresh installs
        // skip this because the rows it patches won't yet exist.
        await _runner.SqlFromAsync(
            "BackfillDisplayNamesFromLegacy.sql",
            new SquashHints { Elidable = true },
            ct);
    }
}
```

When this migration is later subsumed into a squash, the squash tooling preserves the schema op verbatim in the consolidated body and *omits* the back-fill op. The squash commit-time verification confirms the resulting schema matches but does *not* check data — the elidable contract makes that the author's deliberate choice.

In v1 (hand-authored squashes), the author is responsible for honouring `Elidable` when consolidating — the framework provides the mode taxonomy and the API to read it, but doesn't auto-fuse for non-AST providers. In Phase 2 (OpenSearch AST fusion), the fusion middleware reads `SquashHints` and acts on it.

### Example 6 — Phase 2: Postgres `pg_dump` Snapshot Strategy (first Phase 2 target)

For Postgres consumers who hit a 100-migration chain, the Phase 2 generation experience:

```bash
$ dotnet hyperbee-migrations squash \
    --provider postgres \
    --range 1000-1100 \
    --strategy pg-dump \
    --output Migrations/Baseline_2024.cs

[pg-dump] Spinning up ephemeral Postgres container (testcontainers).
[pg-dump] Applying 11 source migrations (1000..1100) to clean container.
[pg-dump] Apply phase: 32.4s (38 statements executed).
[pg-dump] Capturing schema via pg_dump --schema-only --no-owner --no-privileges.
[pg-dump] Post-processing: stripping comments, normalizing whitespace, scrubbing role refs.
[pg-dump] Output: 14.2 KB SQL → embedded as Baseline_2024.sql.
[verify] Spinning up second container; applying squash.
[verify] Apply phase: 4.1s (1 baseline statement).
[verify] Schema diff (apgdiff): 0 differences. ✓
[verify] Data ops detected in source range: 2 (migration 1042, migration 1078).
[verify] Both marked Elidable=true via SquashHints. Excluded from squash body.
[pg-dump] Wrote Migrations/Baseline_2024.cs with Replaces=[1000, 1010, …, 1100].
[pg-dump] Originals retained in Migrations/ until --prune confirms fleet has caught up.
```

The generated `Baseline_2024.cs` body looks identical to a hand-authored one (Example 1) — the only difference is that the SQL was machine-derived and the verification ran automatically:

```csharp
[Migration(version: 2000, Replaces = new long[] {
    1000, 1010, 1020, /* … */ 1100
})]
public class Baseline_2024 : Migration
{
    private readonly PostgresResourceRunner<Baseline_2024> _runner;
    public Baseline_2024(PostgresResourceRunner<Baseline_2024> runner) => _runner = runner;

    public override async Task UpAsync(CancellationToken ct = default)
        => await _runner.AllSqlFromAsync(typeof(Baseline_2024).Assembly, ct);
}
```

MongoDB users running the same CLI invocation in Phase 2 (target #2):

```bash
$ dotnet hyperbee-migrations squash --provider mongodb --range 1000-1100
[mongodb] Spinning up ephemeral MongoDB container.
[mongodb] Applying 11 source migrations to clean container.
[mongodb] Capturing state: 4 collections, 12 indexes, 2 validators.
[mongodb] Emitting code-only Migration whose UpAsync recreates captured state.
[verify] State diff: equivalent. ✓
```

Providers without a strategy implementation report it transparently:

```bash
$ dotnet hyperbee-migrations squash --provider couchbase --range 1000-1100
[couchbase] Provider strategy: Unsupported.
[couchbase] Reason: Couchbase provider has not registered an ISquashStrategy.
[couchbase] Recommendation: hand-author the squash migration. Capture current
            scope/collection/index state via Couchbase Query Workbench or
            `cbq -e` system catalog queries, declare Replaces=[1000..1100]
            on the new migration class.
```

Same CLI, same author-side ergonomics, transparent reporting of which providers have generators today vs. which require hand-authoring.

## Key Decisions

### Decision 1: Extend `[Migration]` with a `Replaces` parameter (resolves Open Question 1)

Per the user's directive: extend the existing attribute, do not introduce `[RollupMigration]`. The `Replaces` parameter is opt-in via named-arg syntax; existing migrations without `Replaces` see no change. Reflection discovery treats empty `Replaces` as "this is a regular migration" — no special-casing required at the call sites that do not opt in.

**Anchor:** ADR-0019 (to be written).

### Decision 2 (revised 2026-05-05 per Assessment 0007 P0-9): v1 ships **Postgres only**; v1.1/v1.2 sequenced

**Per Assessment 0007 IR-CP-4 (Red wins):** OpenSearch and Couchbase are High canonicalization risk per consensus C11; MongoDB is Medium-High. Shipping High-risk providers in v1 means real production migrations exercise the canonicalizer for the first time in customer destructive squashes. Right sequencing:

| Phase | Providers shipping `ISquashStrategy` |
|---|---|
| **v1** | Postgres only (`PgDumpSnapshotStrategy`) |
| **v1.1** (~3 months after v1) | Aerospike + MongoDB |
| **v1.2** (~6 months after v1) | Couchbase + OpenSearch |

v1 promotion gate: Postgres metrics under thresholds for ≥1 release cycle. See ADR-0019 amendment A7 for full criteria. Other providers ship `NullSquashStrategy` returning `Unsupported(...)` until their phase. Hand-authoring is **not** a documented fallback (per MD-8 deletion).

The remaining content of Decision 2 below documents the original v1-includes-strategy-contract framing; superseded by the sequencing above for v1 scope.

---

### Decision 2 (original framing, retained for context):

The Theme 3 requirements (R-09–R-12) as originally framed scoped v1 to OpenSearch generation. Per the user's "any provider can implement and works equally well" directive, that framing is too narrow. The revised position: every provider gets the *runner-side* mechanism in v1 (which is universal); every provider can ship a generator strategy when consumer demand justifies the per-provider implementation. v1 ships the contract; v2+ ships strategies.

Phase 2 first-target is **Postgres** because it's the mainstream target in the .NET ecosystem (the audience EF Core's open squash issue speaks to), `pg_dump --schema-only` is battle-tested snapshot tooling, and the output integrates directly with the existing `PostgresResourceRunner.AllSqlFromAsync` pattern. **MongoDB** is the second target — introspection-based snapshot via `listCollections`/`listIndexes`/validator-extraction; mainstream user base; relatively simple schema model. **OpenSearch AST fusion** remains an option for OpenSearch users who eventually hit the migration-count pain but is not privileged — its earlier "first" framing was an implementation-convenience argument (the AST already exists), not a user-value argument.

This treats R-09 as fulfilled differently than originally specified: the v1 generator-target is no provider — every provider gets universal scaffolding only; per-provider strategies ship in Phase 2 in priority order (Postgres, MongoDB, OpenSearch, then others as demand justifies).

**Anchor:** ADR-0019 (combined; the Replaces-graph mechanism is the universal contract).

### Decision 3: Up-only squashes

A squash migration's `DownAsync` throws `RollbackNotSupportedException`. Rollback across a squash boundary requires backup restore. Matches industry practice unanimously across Django, Flyway, Prisma. Composing N inverses has no general clean answer.

**Anchor:** ADR-0020 (to be written).

### Decision 4: `MigrationRecord.Checksum` is SHA-256 over the migration's effective body

Provider-pluggable via an extension point (`IChecksumStrategy<TMigration>` or equivalent), with a default of "SHA-256 over the migration class's resource bytes for resource-based migrations" and a documented weaker fallback for code-only migrations. Pre-checksum-era rows (null `Checksum`) are tolerated for already-applied migrations; squash-related operations that need integrity refuse to act against null-checksum history without an explicit `--accept-unverified` flag.

**Anchor:** ADR-0021 (to be written).

### Decision 5: Originals stay in the source tree until audit-aware prune

Replaced migrations remain alongside the squash until R-15's `--prune` tool confirms no environment is on the un-replaced path. This is non-negotiable — the partial-catch-up case in Example 4 fails without the originals being available.

**Anchor:** Captured in ADR-0019.

### Decision 6: Data migrations require explicit author marking via `SquashHints`

Three modes, default refuse-on-unmarked-data-ops at *generation* time (not author time — the author-time API requires only that data ops carry an explicit `SquashHints`):
- `Elidable = true` — drop on squash
- `Preserve = true` (default for data ops) — carry verbatim
- `ReplayOnFreshOnly = true` — runs only on R-03's fresh-install path

The taxonomy is borrowed from Django's `elidable` flag plus a third "fresh-install only" mode that handles the case where data ops should run on `UpAsync` but not be carried into the squash body.

**Anchor:** Captured in ADR-0019.

## Rejected Approaches

### B: Universal Snapshot Diff

Loses Theme 2's elidable-mode honesty (data migrations silently dropped); snapshot mechanisms decay with vendor changes; cannot capture vendor-specific features that the snapshot serializer doesn't know about. Research Finding 4 documents this as the universal blind spot of every tool that uses this strategy. Rejected.

### C alone (without A's universal scaffolding)

Plugin contract without a universal floor leaves providers without a strategy implementation in a worse state than today. Rejected as a v1; preserved as the Phase 2 architecture *on top of* A's scaffolding.

### D: Replay-Recorder

Per-provider mutation-capture is fragile (not all providers expose audit logs or proxy hooks cleanly); recording captures noise (auth headers, request IDs, timing); secret-leak risk conflicts with the OpenSearch provider's secret-scrubbing posture (per assessment 0002). Rejected.

## Risks and Open Questions

### Riskiest assumption

**The strict-subset partial-catch-up path (Example 4) is the design's most subtle case.** The assumption is that running unreplaced originals *before* the squash-marking happens preserves correctness. The risk: if an original's `UpAsync` is non-idempotent (e.g., it inserts seed data) and the env has *some* prior originals from earlier runs, running the unreplaced ones could conflict. The framework doesn't have a way to detect this.

Mitigation: the requirements R-06's elidable-mode taxonomy is the author's safety valve — data-bearing originals should be marked `ReplayOnFreshOnly` if they're not safe to run on a partial environment. The framework should surface a warning during the partial-catch-up case naming any data ops that lack explicit marking, so the operator can investigate before applying.

**Validation:** prototype the partial-catch-up path against the existing OpenSearch sample chain (1000..9001) modified to include explicit data ops, in a test that simulates an environment at version 5000.

### Other open items deferred to /nop:plan

- Exact API shape for `SquashHints` per provider (resource-file marker vs. attribute on a code statement)
- Per-provider checksum strategy contract details
- The `--prune` tool's audit source format (CI manifest vs. connection strings vs. exported ledger dumps — was Open Q 7 in requirements)
- Whether `ISquashStrategy` lives in core or in each provider package (probably core for the contract, each provider for the implementation)
- Directory integrity hash (R-16) — defer entirely to Phase 4 enrichment

## Recommended Next Steps

1. **Write ADRs 0019, 0020, 0021** (this skill emits them as Status: Proposed; they get accepted when the design is accepted).
2. **`/nop:red-blue` on this design.** Forward-looking infrastructure work with cross-provider scope warrants stress-testing before plan/implement.
3. **`/nop:plan` for Phase 1.** Decompose the universal scaffolding (Layer 1) into vertical slices: attribute extension, record store schema bumps per provider, reconciliation logic, partial-catch-up handling. Each slice is independently shippable and testable.
4. **Defer Phase 2 (OpenSearch AST fusion strategy implementation)** until Phase 1 has shipped and has at least one consumer asking for the generator. The architecture supports it; building it speculatively wastes calibration capacity.
