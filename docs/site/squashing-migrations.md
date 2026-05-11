# Squashing Migrations — Operator Guide

**Audience:** maintainers and DBAs of services that ship Hyperbee.Migrations
v3.0+ and want to compact a long migration history into a single
fast-applying migration.

**TL;DR:** when fresh-environment provisioning starts to feel slow because
the migration ledger has accumulated many entries, a squash compresses a
contiguous range into one synthetic migration whose body recreates the
equivalent end state. Mature environments auto-mark the squash without
running its body; fresh installs run it once. Postgres ships the v1 codegen;
the four NoSQL providers ship in v1.1 / v1.2.

---

## When to squash

Use a squash when **one of the following is true**:

- A fresh-environment provision (CI ephemeral, new tenant, recovery
  rehearsal) takes more than ~30 seconds running the historical migration
  set, **and** the migrations you'd compress have been deployed to all
  registered fleet environments.
- The migration ledger has more than ~50 rows that pre-date the most recent
  meaningful schema change.
- A particular migration's individual body is expensive (e.g., long-running
  data backfill) and re-running it on every fresh provision is no longer
  necessary because the equivalent state can be expressed in DDL.

**Don't squash if:**

- Any registered fleet environment hasn't yet applied the migrations you
  want to subsume — the two-phase fleet readiness gate (per ADR-0019 A2)
  refuses generation in this state and the right move is to bring the env
  forward first.
- You're in the middle of a v2 → v3 upgrade. Ship v3 to all environments
  first ([upgrade guide](./upgrading-from-v2.md)); then squash.
- You're considering it for an environment that'll be retired soon. The
  squash creation cost amortizes over future fresh provisions; if there
  won't be any, skip it.

## Authoring a squash

### 1. Verify all fleet members are at-or-past the upper bound

The `dotnet hyperbee-migrations squash` CLI runs this check automatically
when you supply a fleet manifest, but you can also probe manually:

```sql
-- Postgres: highest applied version per environment
SELECT MAX(CAST(SPLIT_PART(record_id, '.', 2) AS bigint)) AS max_version
FROM public.migrations
WHERE record_id ~ '^record\.\d+\.';
```

Every registered environment must report `max_version >= upper_bound`. If
any returns a value strictly inside `[lower_bound, upper_bound)` the squash
CLI raises `MidRangeFleetException` at generation time.

### 2. Write the fleet manifest

```yaml
fleet:
  - name: prod
    connection: ${POSTGRES_PROD_CONNECTION}
    topology:
      server-major: "16"
  - name: staging
    connection: ${POSTGRES_STAGING_CONNECTION}
  - name: qa
    connection: ${POSTGRES_QA_CONNECTION}

squash-overrides:
  accept-stranding:
    - environment: qa
      ticket-id: FLEET-1234
      owner: ops@example.com
      reason: "QA cluster is being rebuilt this quarter; no need to deploy here."
      expires: 2026-06-01
```

- `connection` supports `${ENV_VAR}` substitution from the process
  environment.
- `accept-stranding` lists environments that intentionally aren't expected
  to deploy this squash. Each entry requires a ticket-id (3–64 alphanumeric
  + dash + underscore), an owner, a reason ≥ 20 chars, and an expiry within
  90 days (default 30 if omitted) per ADR-0019 A15.

### 3. Run the squash CLI

```bash
dotnet hyperbee-migrations squash \
  --provider postgres \
  --connection "Host=prod-db;Database=app;Username=ops;Password=secret" \
  --range 1000-1500 \
  --output ./squash-out \
  --assembly ./bin/Release/net10.0/MyApp.Migrations.dll \
  --fleet-manifest ./fleet.yml
```

The CLI:

1. Loads the fleet manifest and runs the readiness check (refuses on
   mid-range members).
2. Captures the operator's live topology signature (server major +
   extensions + locale + encoding).
3. Spins an ephemeral `postgres:<major>-alpine` container matching the
   topology, applies the migrations through the upper bound via the
   assembly's `ApplyToDataSourceAsync` shim entry, and runs
   `pg_dump --schema-only`.
4. Canonicalizes the dump (strips preamble, normalizes line endings,
   refuses `CREATE INDEX CONCURRENTLY` per A2).
5. Captures `pg_sequences` last-values for a deterministic `setval(...)`
   post-block.
6. Emits three artifacts in `--output`:
   - `<name>.sql` — the canonical squash content.
   - `<name>.metadata.json` — sidecar metadata with topology, fleet versions, overrides, generation timestamp.
   - `<name>.summary.md` — human-readable summary + per-statement diagnostics.

### 4. Author the squash migration class

The `<name>.sql` file is the body of a new `[Migration]` class. Wire it
into your assembly:

```csharp
[Migration( 1500, ReplacesRange = "1000-1500" )]
public class Squash_1500 : Migration
{
    public override async Task UpAsync( CancellationToken ct = default )
    {
        // The squash body runs only on fresh installs; mature environments
        // auto-mark this migration via the Replaces graph (per ADR-0019).
        var sql = ResourceHelper.GetResource<Squash_1500>( "Squash_1500.sql" );
        // execute against your NpgsqlDataSource
    }
}
```

Embed `Squash_1500.sql` as a resource in your project's csproj
(`<EmbeddedResource Include="Migrations\Squash_1500.sql" />`).

### 5. Remove the original migration source files

After verifying the squash applies cleanly to a fresh container, delete
the migration source files for versions in the squash range. The squash
CLI's `--remove-originals` flag does this with an idempotency check (the
flag refuses if the originals are already gone unless you also pass
`--regenerate`).

The ledger row for the squash carries `Kind=Squash, Replaces=[1000..1500]`,
so mature environments running the v3 runner against a populated ledger
auto-mark the squash without needing the original source files.

## Reviewing a squash PR

Read the **`<name>.summary.md`** artifact, NOT the raw SQL. The summary
narrates:

- Range subsumed and topology captured.
- Per-statement diagnostics (non-determinism scan: any `now()`,
  `gen_random_uuid()`, etc.; classifier surprises).
- Sequence `setval(...)` block emission count.

Spot-check the `metadata.json` for the right `ExpectedFleetVersions` per
environment and a sane `MaxStalenessWindow`.

The raw SQL is generated; reviewing it line-by-line is rarely useful.

## Recovering from squash exceptions

### `MidRangeSquashException` — partial ledger coverage at deploy time

The runner raises this when an environment's ledger contains SOME but not
ALL of the versions a squash claims to subsume — typically because a
backup-restore brought the ledger to an awkward state, or because a
migration was manually marked applied. Three documented recovery paths
(per ADR-0019):

1. **Restore from backup** — preferred when the partial state was caused
   by an accident.
2. **Re-introduce the missing migrations from version control** — apply
   them, then the squash auto-marks normally on the next runner pass.
3. **`dotnet hyperbee-migrations recover from-mid-range`** — last-resort
   escape hatch. Requires:
   - The deterministic 12-character acknowledgement token derived from
     `(env-name, squash-version, missing-versions)`. Compute it externally
     and supply it via `--token`; the CLI rejects mismatches.
   - `--ticket-id`, `--owner`, `--reason ≥ 20 chars` for the audit trail.
   - **Only safe** when an external check confirmed the live data state
     matches the squashed schema.

### `StaleFleetMemberException` — environment too far behind at deploy time

The runner raises this when an environment's ledger is below its recorded
`ExpectedFleetVersions` minimum AND the squash's `MaxStalenessWindow`
(default 30 days) has elapsed. Recovery: bring the environment forward by
applying the missing migrations; or regenerate the squash with the current
fleet state (cheap when the snapshot A cache is warm per A4).

### `UnregisteredEnvironmentException` — env not in the manifest

The squash's `ExpectedFleetVersions` doesn't list the live environment
name. Either correct `MigrationOptions.EnvironmentName` to match an
existing entry, or regenerate the squash with the current manifest
including the new environment.

## Roadmap

| Provider | v1 status | Codegen ships in |
|---|---|---|
| Postgres | ✓ shipped | v1.0 |
| MongoDB | NullSquashStrategy refusal | v1.1 |
| Couchbase | NullSquashStrategy refusal | v1.1 |
| Aerospike | NullSquashStrategy refusal | v1.2 |
| OpenSearch | NullSquashStrategy refusal | v1.2 |

Operators on non-Postgres providers can adopt the universal scaffolding
(Replaces graph, ApplyMode context, fleet readiness gate types) today —
only the codegen verb is provider-gated.

## See also

- [ADR-0019 — Migration squash via Replaces graph](../decisions/0019-migration-squash-replaces-graph.md)
- [ADR-0020 — Squashes are up-only](../decisions/0020-squashes-are-up-only.md)
- [ADR-0021 — MigrationRecord checksum](../decisions/0021-migration-record-checksum.md)
- [ADR-0022 — Script-format resource migrations](../decisions/0022-script-format-resource-migrations.md)
- [Upgrade guide v2 → v3](./upgrading-from-v2.md)
- [Migrating from EF Core](./migrating-from-ef-core.md)
