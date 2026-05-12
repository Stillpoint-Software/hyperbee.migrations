# Upgrading from Hyperbee.Migrations v2 to v3

**Audience:** application owners and DBAs whose service references
`Hyperbee.Migrations` v2.x and is upgrading to v3.0.

**TL;DR:** the upgrade is mostly transparent. Existing migrations keep
working. Existing `*.statements.json` resources keep working. The two breaking
changes (`IMigrationRecordStore` interface methods, provider record-store
schemas) are protected by safe back-compat paths so consumers compile and run
unchanged. The one operational hazard is mixed-version fleets — don't run v2
and v3 simultaneously against the same ledger.

---

## What stays the same

- **Existing migrations work unchanged.** A `[Migration(version)]` declaration
  with no `Replaces`/`ReplacesRange` is a regular migration in v3 just as it
  was in v2.
- **Existing `*.statements.json` resources work unchanged.** The legacy
  JSON-array loader is preserved; the new `.statements` script form is an
  additive option (per
  [ADR-0022](../decisions/0022-script-format-resource-migrations.md)).
- **Existing `dotnet build` / `dotnet test` flows work unchanged.** No new
  required dependencies; no changes to project shape.
- **Provider DI registration is unchanged.** Each provider's
  `Add*Migrations(opts => ...)` extension still takes the same options.
- **Existing test harnesses work unchanged.** v2 record-store mocks compile
  on v3 because the new `IMigrationRecordStore` methods ship with safe
  default-interface-method (DIM) implementations.

## What you must do

Nothing — for most consumers. Update the package reference to
`Hyperbee.Migrations 3.0.0` (and the matching provider package). The first
time the runner starts against an existing ledger it performs an idempotent
schema migration (additive columns / bins / fields); pre-existing rows
read clean.

## What changed (and why your code probably still compiles)

### 1. `IMigrationRecordStore` gained three methods

```csharp
Task<WriteOutcome> WriteAsync(MigrationRecord record, WritePrecondition precondition = None, CancellationToken ct = default);
Task<IReadOnlySet<string>> IntersectWithAppliedAsync(IEnumerable<string> candidateIds, CancellationToken ct = default);
Task<IReadOnlySet<long>> IntersectWithSquashedAsync(IEnumerable<long> versions, CancellationToken ct = default);
```

All three ship with DIM defaults so v2 record-store implementations compile
unchanged. The defaults are degraded — the new `WriteAsync` overload
delegates to legacy `WriteAsync(string)` (so `Checksum`/`Kind` are dropped on
custom stores until they override); `IntersectWithAppliedAsync` falls back to
a per-id `ExistsAsync` loop; `IntersectWithSquashedAsync` returns an empty set.

**You only need to override these if you ship a custom
`IMigrationRecordStore`.** All five shipped providers (Aerospike, Couchbase,
MongoDB, OpenSearch, Postgres) provide real overrides — no action required.

If you ship a custom store and want squash support:

```csharp
public sealed class MyRecordStore : IMigrationRecordStore
{
    // ... existing v2 methods ...

    public async Task<WriteOutcome> WriteAsync(
        MigrationRecord record,
        WritePrecondition precondition,
        CancellationToken cancellationToken)
    {
        record.EnsureLedgerIntegrity(); // refuse inconsistent Kind/Replaces

        // your insert logic, persisting Checksum / Kind / Replaces
        // observe precondition.MustNotExist for concurrent-runner idempotency
        // return WriteOutcome.Created or AlreadyExistsBenign or PreconditionFailed
    }

    // Bulk "which of these candidates exist?" probe. Replaces N ExistsAsync
    // round trips with one. Input: the IDs your reflection scan discovered.
    // Output: the subset already in the ledger. The runner subtracts the
    // result from the candidate set to get "what still needs to run."
    public async Task<IReadOnlySet<string>> IntersectWithAppliedAsync(
        IEnumerable<string> candidateIds,
        CancellationToken cancellationToken)
    {
        // single round-trip realtime read
        // Postgres: WHERE record_id = ANY(ids)
        // MongoDB:  find({ _id: { $in: ids } })
        // etc.
    }

    // Bulk "which of these versions are already covered by an applied
    // squash?" probe. After a squash applies (one row with
    // Kind=Squash, Replaces=[1000..2999]), a fresh installer's reflection
    // scan still finds the original Migration(1000) / Migration(1001) /
    // ... classes in the assembly - they have to: existing fleet members
    // applied them individually and the ledger rows are still there for
    // forensic history. This method answers "of these old versions, which
    // are satisfied transitively by some applied squash's Replaces array?"
    // so the fresh installer can skip them and only run the squash itself.
    // Transitive because squashes can stack: 18000 replaces [9000, 3000..]
    // and 9000 replaces [1000..2999] - version 1500 is covered through the
    // chain, not directly. Implementations walk the squash graph.
    public async Task<IReadOnlySet<long>> IntersectWithSquashedAsync(
        IEnumerable<long> versions,
        CancellationToken cancellationToken)
    {
        // transitive squash satisfaction:
        // return versions that appear in some Squash row's Replaces array
    }
}
```

**Together, these two methods give the runner an `O(1)`-round-trip view of
"what still needs to run":**

```
to_run = discovered − IntersectWithAppliedAsync(discovered) − IntersectWithSquashedAsync(remaining)
```

Without them the equivalent computation costs `O(discovered)` round trips,
which dominates bootstrap latency on fleets with hundreds of migrations.

### 2. Provider record-store schemas gained `Checksum` + `Kind` (+ `Replaces` for Postgres)

The schema migration is automatic and idempotent on first v3 apply.
Pre-existing rows read as `Checksum=null, Kind=Migration, Replaces=[]` and
pass integrity validation.

- **Postgres** — first `InitializeAsync` runs idempotent
  `ALTER TABLE ADD COLUMN IF NOT EXISTS` for `checksum`, `kind`
  (with `CHECK (kind IN (0,1,2))`), and `replaces` (`bigint[]`).
- **Aerospike** — record bins are additive; pre-existing records have sparse
  bins until rewritten.
- **Couchbase** — JSON document fields are additive; pre-existing documents
  deserialize to defaults.
- **MongoDB** — BSON document fields are additive; same shape.
- **OpenSearch** — first `InitializeAsync` applies an additive
  `PUT _mapping` patch to add `kind` (byte) and `replaces` (long[]) fields.
  IAM-aware: warns and proceeds if the deploy role lacks
  `indices:admin/mapping`.

You can manually verify the schema migration by inspecting your provider's
ledger before and after the first v3 apply.

## Operational hazards

### Mixed-version fleet — don't do this

Running `Hyperbee.Migrations 2.x` and `3.0` simultaneously against the same
ledger is unsupported. v2 doesn't know about `Kind=Squash` rows; if v3 writes
a squash row and v2 then encounters it, the behavior is undefined.

**Recipe:**

1. Deploy v3 to all environments (rolling deploys are fine — v3 reads v2 rows
   transparently).
2. After all environments are on v3, optionally start authoring squashes.

The two-phase fleet readiness gate is the safety net for the
deploy-then-squash workflow; see
[ADR-0019 amendment A2](../decisions/0019-migration-squash-replaces-graph.md).

### Squash is operationally one-way

Once a squash is committed:

- The original migration source files are removed from the migrations folder.
- The ledger has the squash row with `Kind=Squash, Replaces=[...]`.

Rolling back to v2 against a squashed ledger is unsupported. The documented
recovery is **backup-restore**: restore the database from a backup that
pre-dates the squash, downgrade the package, redeploy.

If you anticipate needing to roll back, defer adopting squash for the affected
environments until you've verified the v3 deploy in production.

### Mid-range environments

If an environment's ledger is mid-range with respect to a squash range — i.e.,
the ledger has applied SOME but not ALL of the replaced versions — the runner
raises `MidRangeSquashException`. Three documented recovery paths:

1. **Restore from backup** — preferred when the partial state was caused by a
   bug or accident.
2. **Re-introduce the missing migrations from version control** — apply them,
   then the squash auto-marks normally.
3. **`dotnet hyperbee-migrations recover from-mid-range`** — the last-resort
   escape hatch. Requires a deterministic acknowledgement token derived from
   `(env-name, squash-version, missing-versions)` so accidental copy-paste
   from a sibling environment is rejected. **Use only when the live data
   state has been externally verified to match the squashed schema.**

The CLI verb ships in a follow-up to v3.0; the runtime token-verification
helper (`Hyperbee.Migrations.Squash.RecoveryAcknowledgement`) is available
today for tooling and runbooks.

## Compatibility matrix

| Concern                                                              | v2 → v3 behavior                                                                       |
| -------------------------------------------------------------------- | -------------------------------------------------------------------------------------- |
| Existing `[Migration(v)]` declarations                               | Work unchanged                                                                         |
| Existing `*.statements.json` resources                               | Work unchanged (legacy loader)                                                         |
| Existing custom `IMigrationRecordStore`                              | Compiles and runs unchanged via DIM defaults; degraded squash support until overridden |
| Existing ledger rows                                                 | Read clean (`Checksum=null, Kind=Migration`)                                           |
| `MigrationRecord` consumers reading `Checksum` / `Kind` / `Replaces` | New properties exist; null-safe defaults apply                                         |
| Mixed v2/v3 fleet against same ledger                                | **Unsupported** — deploy v3 everywhere first                                           |
| Squash → rollback to v2                                              | **Unsupported** — backup-restore is the recovery                                       |

## See also

- [ADR-0019 — Migration squash via Replaces graph](../decisions/0019-migration-squash-replaces-graph.md)
- [ADR-0020 — Squashes are up-only](../decisions/0020-squashes-are-up-only.md)
- [ADR-0021 — MigrationRecord checksum](../decisions/0021-migration-record-checksum.md)
- [ADR-0022 — Script-format resource migrations](../decisions/0022-script-format-resource-migrations.md)
- [CHANGELOG entry for 3.0](../../CHANGELOG.md)
