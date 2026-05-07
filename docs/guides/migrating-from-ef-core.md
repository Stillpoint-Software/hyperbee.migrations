# Migrating from Entity Framework Core to Hyperbee.Migrations

**Audience:** teams adopting Hyperbee.Migrations from an existing EF Core
deployment that uses `dotnet ef migrations add` + `__EFMigrationsHistory`.

**TL;DR:** synchronize the fleet to a known EF Core version first, then
introduce a Hyperbee baseline migration that uses `Replaces` to absorb the
EF history. The fleet runs the runner once to auto-mark the baseline, and
new migrations land normally afterward. EF Core stays on disk for the
historical record but no longer drives schema changes.

---

## Why bridge instead of cut over?

EF Core's `__EFMigrationsHistory` is a single-table ledger of applied
migration ids (string). Hyperbee.Migrations uses `Migrations` with
`(record_id, run_on, checksum, kind, replaces[])`. The two ledgers don't
share a row format, so you can't simply rename the table and run.

But the ROW SHAPE difference is the only barrier. The applied-state
information — "the schema is at version N" — is the same. Hyperbee's
`Replaces` graph (per ADR-0019) was designed exactly to bridge this case:
introduce a Hyperbee migration that says "I subsume EF migrations
[A..B]", and on first runner pass the Hyperbee runner auto-marks it
because the equivalent state already exists in the database.

## Pre-requisites

1. **All environments at the same EF Core version.** Run
   `dotnet ef migrations script` on each environment and compare. If they
   differ, bring them in sync via EF Core's normal apply path before
   proceeding.
2. **A note of the highest EF Core migration applied.** This becomes the
   bridge baseline.
3. **Hyperbee.Migrations v3.0+ provider package added** to your project
   alongside EF Core. Both can coexist while you transition.

## Step 1 — Add Hyperbee tables (without touching EF)

Run the Hyperbee.Migrations runner once with no migrations declared. It
performs the additive `ALTER TABLE ADD COLUMN IF NOT EXISTS` /
`CREATE TABLE IF NOT EXISTS` schema migration that creates the
`migrations` ledger table. EF Core's `__EFMigrationsHistory` is left
untouched.

```csharp
services.AddPostgresMigrations( config =>
{
    // No assemblies registered yet — runner only ensures schema.
    config.LedgerTableName = "migrations";
} );
```

After this, you have two ledger tables side by side:

```
__EFMigrationsHistory  (EF, untouched)
migrations             (Hyperbee, empty)
```

## Step 2 — Introduce the bridge baseline

Add a single Hyperbee migration whose version is one HIGHER than your
highest EF migration's numeric prefix and whose `Replaces` enumerates the
EF migration ids you're absorbing. EF migration ids look like
`20240315120000_AddUserTable`; pick a distinct numeric scheme for Hyperbee
that won't collide.

For example, if your EF history runs through `20240901120000_AddOrders`:

```csharp
[Migration( 100000000, Replaces = new long[]
{
    20231001120000, // initial-schema
    20231115120000, // add-user-roles
    20240315120000, // add-user-table
    // ... all prior EF versions enumerated by their numeric prefix
    20240901120000  // add-orders
} )]
public class Hyperbee_BridgeBaseline : Migration
{
    public override Task UpAsync( CancellationToken ct = default )
    {
        // Body never runs in mature environments — the runner auto-marks
        // because every version in Replaces is already present in the
        // ledger via the synthetic-mapping step below.
        //
        // For fresh installs (no ledger rows): apply the FULL EF schema
        // here, e.g., by reading a captured pg_dump --schema-only as of
        // the bridge point.
        return Task.CompletedTask;
    }
}
```

> **Important:** the values in `Replaces` must match what the Hyperbee
> runner sees in the `migrations` table after Step 3. They aren't EF
> migration ids — they're the synthetic Hyperbee record ids you'll write
> in Step 3.

## Step 3 — Synthetic-map the EF history into Hyperbee's ledger

Run a one-time data migration that copies the EF history into Hyperbee's
ledger as `Kind=Migration` rows. The script below handles Postgres; adapt
the keyspace for your provider.

```sql
-- Map __EFMigrationsHistory rows to Hyperbee migrations rows.
INSERT INTO public.migrations (record_id, run_on, kind, replaces)
SELECT
    'record.' ||
        regexp_replace(migration_id, '^(\d+)_.*', '\1') || '.' ||
        regexp_replace(migration_id, '^\d+_(.*)$', '\1'),
    NOW(),
    0,
    ARRAY[]::bigint[]
FROM __EFMigrationsHistory
ON CONFLICT (record_id) DO NOTHING;
```

Each EF row becomes a Hyperbee row with:

- `record_id`: `record.<numeric-prefix>.<name-suffix>` (Hyperbee's
  default convention — see `DefaultMigrationConventions.cs`).
- `kind`: `0` (Migration).
- `replaces`: empty.
- `checksum`: NULL (pre-checksum-era; the integrity check accepts this).

Now update your `Hyperbee_BridgeBaseline.Replaces` array to reference
those numeric prefixes (the values you regexp-extracted) — that's what
the Hyperbee runner actually compares against.

## Step 4 — First runner pass — bridge auto-marks

Run the Hyperbee runner. It discovers `Hyperbee_BridgeBaseline`,
classifies as a squash (Replaces non-empty), runs the reconciliation:

- For each version v in `Replaces`, it asks the store
  `LoadAppliedVersionsAsync` whether the corresponding `record_id` exists.
  All do (you wrote them in Step 3).
- Result: **mature environment, auto-mark.** The runner writes the
  `Hyperbee_BridgeBaseline` row with `Kind=Squash` and the `Replaces`
  array. No `UpAsync` body runs.

The `__EFMigrationsHistory` table is now operationally redundant. Hyperbee
won't write to it. Optionally drop it after a stabilization period (or
keep it for forensic reasons).

## Step 5 — Add new migrations the Hyperbee way

```csharp
[Migration( 100000001 )]
public class FirstPostBridgeMigration : Migration
{
    public override async Task UpAsync( CancellationToken ct = default )
    {
        // Use Hyperbee.Migrations.Providers.Postgres.Resources.PostgresResourceRunner
        // for SQL files, or write SQL directly via NpgsqlConnection.
    }
}
```

From here forward, `dotnet ef migrations add` is no longer needed for
schema work in this service. EF Core stays in use for runtime querying
(LINQ-to-SQL); only the migration tooling switches.

## Operational concerns

### Don't run EF migrations after the bridge

After Step 4, `dotnet ef database update` would re-evaluate
`__EFMigrationsHistory` — which is now stale (Hyperbee owns the schema).
Disable EF's automatic migration discovery in your service host:

```csharp
services.AddDbContext<MyDbContext>( options =>
    options
        .UseNpgsql( connectionString )
        // Don't auto-migrate on startup. Hyperbee owns schema changes now.
        // .UseAutomaticMigrations is OFF (it's the default in EF Core 8+).
);
```

### Mixed-version fleet hazard

If half your fleet has run the bridge and the other half hasn't, EF
migrations against the un-bridged half will write to
`__EFMigrationsHistory` — but Hyperbee's ledger on the bridged half won't
see those writes. Sync the fleet before bridging: every environment
through the same EF version, then everywhere runs the bridge in lockstep.

### Rolling back the bridge

The bridge introduces a Hyperbee row with `Kind=Squash`. Per ADR-0020
squashes are up-only; rolling back to "EF owns schema" is unsupported
once the bridge is in. The recovery path is **backup-restore** to a state
before the bridge.

If you anticipate rolling back, defer the bridge until you're confident.

### Allowlisting EF-era nulls

The Hyperbee runner's integrity check accepts `Checksum=null` rows
unconditionally (pre-checksum-era is part of the contract). Your synthetic
EF rows have `Checksum=null` and read clean.

If you later set `MigrationOptions.RequireAllRowsChecksummed = true`
(future flag) for tighter governance, re-checksum the EF-era rows via a
maintenance migration before flipping the option.

## See also

- [Squashing migrations operator guide](./squashing-migrations.md)
- [Upgrade guide v2 → v3](./upgrading-from-v2.md)
- [ADR-0019 — Migration squash via Replaces graph](../decisions/0019-migration-squash-replaces-graph.md)
- [ADR-0021 — MigrationRecord checksum](../decisions/0021-migration-record-checksum.md)
