# ADR-0021: Migration Record Carries Content Checksum

**Status:** Accepted
**Date:** 2026-05-04
**Related design:** [docs/design/migration-squashing.md](../design/migration-squashing.md)
**Related ADRs:** ADR-0003 (Provider Record Store Contract), ADR-0019 (Replaces-Graph Mechanism)

## Context

`MigrationRecord` today is `(Id, RunOn)` only. The Id derives from `[Migration].Version` plus the class name (per ADR-0009), so the ledger row encodes the *identity* of what was applied but not the *content*.

Squashes (per ADR-0019) introduce a new operational mode: the runner auto-marks a squash as applied when its `Replaces` set is fully present in the ledger, *without re-running the squash's `UpAsync`*. This is the Django pattern that lets mixed-environment fleets catch up automatically. But it requires the runner to trust that the ledger rows for the replaced versions actually correspond to the migrations as the squash author understood them — not a hand-edited variant or a stale row from a partially-rolled-back attempt.

Surveyed tools that support squash all carry per-record checksums (Flyway's `flyway_schema_history.checksum`, Liquibase's `databasechangelog.md5sum`, Prisma's `_prisma_migrations.checksum`). The lone outlier is EF Core, which records only the migration name in `__EFMigrationsHistory` — and EF Core is also the lone outlier in lacking first-class squash, which is not a coincidence.

Hyperbee's ledger is small today (sample migrations cap at ~10 per provider). Adding a checksum field now, while ledger row counts are minimal, is significantly cheaper than retrofitting once production environments accumulate thousands of pre-checksum rows.

## Decision

`MigrationRecord` is extended with two additive fields:

```csharp
public sealed record MigrationRecord
{
    public string Id { get; init; } = default!;
    public DateTimeOffset RunOn { get; init; }

    // NEW
    public string? Checksum { get; init; }                          // SHA-256 hex; null = pre-checksum era
    public MigrationRecordKind Kind { get; init; } = MigrationRecordKind.Migration;
}

public enum MigrationRecordKind
{
    Migration = 0,
    Squash    = 1,
    Baseline  = 2,
}
```

**`Checksum` is SHA-256 hex over the migration's effective body bytes.** The exact "effective body" definition is provider-pluggable via an extension point. Default contract:

- For resource-based migrations (those whose `UpAsync` calls `*ResourceRunner.AllSqlFromAsync` / `StatementsFromAsync` / `DocumentsFromAsync`): the SHA-256 is over the concatenated, sorted-by-name resource contents of the migration's embedded resources. This makes the checksum sensitive to any change in the SQL/JSON/statements that drive the migration.
- For code-only migrations (those whose `UpAsync` is hand-authored against the provider client directly): the default is SHA-256 over the migration class's full type name + version. This is acknowledged to be weaker — it catches version/name changes but not body changes — and authors who want stronger integrity for code-only migrations can implement a custom `IChecksumStrategy<TMigration>` that hashes whatever they consider authoritative.

**Checksum is computed and written on every `IMigrationRecordStore.WriteAsync` going forward.** Existing pre-this-ADR ledger rows have null `Checksum` and `Kind = Migration` (the default). The runner tolerates null checksums on already-applied rows ("pre-checksum era") but never *writes* a null checksum on a new row.

**Operations that depend on checksum integrity refuse to act against null-checksum history without an explicit `--accept-unverified` flag.** Specifically, ADR-0019's auto-mark behavior (skipping `UpAsync` when all `Replaces` are present) requires that the replaced rows have non-null checksums matching what the squash expects. If any are null and the operator hasn't opted in, the runner refuses the auto-mark and demands explicit acknowledgement that the integrity check is being skipped.

**Provider record stores extend their schemas additively.** Postgres adds two columns; OpenSearch and MongoDB are JSON-shaped and additive; Couchbase and Aerospike likewise. The ADR-0003 contract is *extended*, not broken — implementations that don't yet read the new fields get a default value (null Checksum, Kind = Migration) on read, and the runner is tolerant of missing fields.

## Consequences

**Positive:**
- Squash auto-mark behavior (per ADR-0019) has cryptographic backing for "the row records what we think it records."
- Audit queries gain a discriminator (`Kind`) for telling squashes from regular migrations from baselines.
- Hyperbee joins the surveyed-tool norm; only EF Core's ledger lacks per-record checksums, and that absence is the central blocker for first-class squash in EF Core's roadmap.
- Costs nothing for consumers who don't use squashes — `Checksum` is computed on write but never read.

**Negative:**
- Provider implementations must extend their schemas. The change is non-breaking but requires code in every provider; first release after this ADR is accepted will need each provider's record store updated.
- The default checksum strategy for code-only migrations is acknowledged-weaker. Documenting this honestly may surprise authors who expect uniform integrity.
- "Pre-checksum era" null tolerance is a trust gap. Consumers who upgrade an existing deployment will have a long tail of null-checksum rows; the runner has to tolerate them, but the tolerance is itself a potential surprise.

**Neutral:**
- Checksum scope ("what bytes contribute") is per-provider. Consumers writing custom checksum strategies for code-only migrations should follow the default contract: hash whatever the migration deterministically does, not anything like timestamps or environment-specific values.
- The `Kind` enum is small (3 values) but the discriminator buys clarity in audit queries — `SELECT * FROM migrations WHERE kind = 'squash'` becomes trivially answerable.

## Alternatives Considered

- **Skip the checksum entirely; trust `Id` alone** — rejected. ADR-0019's auto-mark depends on integrity of the replaced rows; without checksum, "the row records the migration as authored" cannot be verified. Every other surveyed tool that supports squash carries checksums for this reason.
- **Use a non-cryptographic hash (CRC32, xxHash)** — rejected. Faster but offers no integrity guarantee against malicious or accidental tampering. SHA-256 cost is negligible at migration-write frequency.
- **Refuse to load null-checksum rows entirely** — rejected. Existing deployments would break on upgrade. Tolerating nulls as "pre-checksum era" with explicit opt-in for integrity-sensitive operations is the migration-friendly path.
- **Compute checksum at discovery time and never store it** — rejected. Storage is the point: we want to detect when an *applied* migration's body has been changed since it was journaled, not just check current code against itself.

## Amendments from Assessment 0007 (2026-05-05)

The full `/nop:assess` ([0007](../research/0007-migration-squashing-destructive-assessment.md)) surfaced two record-store integrity gaps the original ADR didn't close.

### A1 (P0-8): `Kind` / `Replaces` consistency enforcement

Per finding IR-N3: original ADR-0019 reconciliation didn't reference `Kind`. The runner could treat `Kind = Migration` with non-empty `Replaces` as a squash, OR `Kind = Squash` with empty `Replaces` as regular — either path undermines ledger integrity. A ledger-write attacker (or buggy migration) could promote a regular migration row to squash retroactively without changing checksum.

**Write-time enforcement.** Provider record stores enforce on every `WriteAsync`:

```csharp
if (record.Kind == MigrationRecordKind.Squash && (record.Replaces is null || record.Replaces.Count == 0))
    throw new MigrationLedgerIntegrityException(
        $"Record {record.Id}: Kind=Squash but Replaces is empty.");

if (record.Kind == MigrationRecordKind.Migration && record.Replaces is { Count: > 0 })
    throw new MigrationLedgerIntegrityException(
        $"Record {record.Id}: Kind=Migration but Replaces is non-empty. Use Kind=Squash.");
```

**Read-time enforcement.** Inconsistent rows raise `MigrationLedgerIntegrityException` at load — hard refusal, never silent acceptance. Pre-amendment rows with `Kind = Migration` (the default) and empty `Replaces` read clean.

### A2 (P2-3): Old canonicalizer-version retention

Per finding PM-5 + IR amendment: forensic reconstruction after a canonicalizer regression requires that old canonicalizer-versions remain runnable. If a major refactor changes `ISnapshotCanonicalizer` interface signatures three years out, the artifact's pinned canonicalizer-version becomes unrunnable.

- Each canonicalizer-version retained as a separate frozen package (similar to Roslyn language-version retention).
- Squash artifact header records `canonicalizer-version: <provider>/<version>` (e.g., `postgres/1.2.0`).
- Major-version refactors ship a back-compat shim or refuse to load older artifacts (`CanonicalizerVersionUnsupportedException` with clear remediation).

Phase 2 enrichment; v1 ships with one canonicalizer-version per provider so the retention machinery isn't yet load-bearing.

## References

- Research: [docs/research/0005-migration-squashing.md](../research/0005-migration-squashing.md), Findings 5, 7
- Requirements: [docs/requirements/migration-squashing.md](../requirements/migration-squashing.md), R-01, R-05
- Design: [docs/design/migration-squashing.md](../design/migration-squashing.md), Decision 4
- **Assessment 0007 (drives A1, A2 above):** [docs/research/0007-migration-squashing-destructive-assessment.md](../research/0007-migration-squashing-destructive-assessment.md)
- Related: [`MigrationRecord.cs`](../../src/Hyperbee.Migrations/MigrationRecord.cs), [`IMigrationRecordStore.cs`](../../src/Hyperbee.Migrations/IMigrationRecordStore.cs)
