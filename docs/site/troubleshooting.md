---
layout: default
title: Troubleshooting
nav_order: 15
---

# Troubleshooting

This page covers common runtime errors thrown by Hyperbee.Migrations and its providers, what they mean, and how to recover. Symptoms are organized by the exception or error message you are likely to see in logs.

If you are looking for design-time concepts, see [Concepts](concepts.md). For squash-specific recovery flows, see [Squashing Migrations](squashing-migrations.md).

---

## Symptom: `DuplicateMigrationException`

```
Hyperbee.Migrations.DuplicateMigrationException: Multiple migrations declare version 20260101001500.
```

**Cause:** Two `[Migration(version)]` attributes in the loaded assemblies declare the same version number, OR a migration class was renamed/moved without updating an existing ledger row that referenced it.

The runner refuses to proceed because version is the primary key for ordering and ledger lookup. Allowing duplicates would silently apply one and skip the other.

**Recovery:**

1. Search the assembly for the duplicated version:

   ```shell
   dotnet build 2>&1 | grep "20260101001500"
   ```

2. If the duplicate is unintentional, change one of the version numbers and rebuild. Version numbers are arbitrary `long` values; the convention is `yyyyMMddHHmmss`.

3. If a migration was renamed, the ledger may still reference the old class name. Inspect the ledger storage (provider-specific) and either:
   - Delete the stale ledger row so the renamed migration runs cleanly, OR
   - Update the row's `Name` column to match the new class name.

---

## Symptom: `MigrationLockUnavailableException`

```
Hyperbee.Migrations.MigrationLockUnavailableException: Could not acquire migration lock within 30s.
```

**Cause:** Another runner instance currently holds the global migration lock. The lock prevents concurrent runners from racing on the ledger.

This is expected when two pods/containers boot simultaneously and both attempt migrations. It is unexpected if a previous runner crashed without releasing the lock.

**Recovery:**

1. **Wait it out.** If a legitimate concurrent runner exists, the second runner will succeed once the first finishes. The lock is short-lived for healthy migrations.

2. **Wait for TTL expiry.** If the holder crashed, the lock will expire automatically. Defaults vary by provider:

   | Provider   | Default lock TTL |
   |------------|------------------|
   | Aerospike  | 60 seconds       |
   | Couchbase  | 5 minutes        |
   | PostgreSQL | 1 hour           |
   | OpenSearch | 5 minutes        |
   | MongoDB    | 5 minutes        |

   These are configurable via `MigrationOptions.LockMaxLifetime` (or provider-specific options).

3. **Force-release.** If you cannot wait for TTL, manually delete the lock document/row. **AUDIT FIRST** in production - confirm no live runner is holding it.

   - Aerospike: delete the record at `set=migrations_lock`, key=`migrations_lock`. See [Aerospike](aerospike.md).
   - Couchbase: delete the document `migrations:lock` from the configured bucket/scope. See [Couchbase](couchbase.md).
   - PostgreSQL: `DELETE FROM migrations_lock;`. See [PostgreSQL](postgresql.md).
   - OpenSearch: `DELETE /<lock-index>/_doc/migrations_lock`. See [OpenSearch](opensearch.md).
   - MongoDB: `db.migrations_lock.deleteOne({ _id: "migrations_lock" })`. See [MongoDB](mongodb.md).

---

## Symptom: `MigrationLedgerIntegrityException`

```
Hyperbee.Migrations.MigrationLedgerIntegrityException: Ledger row 20260301000000 has Kind=Squash but Replaces is empty.
```

**Cause:** A ledger row's `Kind` and `Replaces` columns are inconsistent. The runner validates ledger integrity at startup and refuses to proceed against a corrupted ledger.

Valid combinations:

| Kind        | Replaces             |
|-------------|----------------------|
| `Migration` | empty / null         |
| `Squash`    | non-empty (>= 1 row) |

This is almost always caused by an external mutator - a manual SQL/N1QL edit, a bad backfill script, or a partial restore.

**Recovery:**

1. Inspect the ledger and identify the offending row(s).
2. Determine the intended state by reviewing source control for the migration class.
3. Either:
   - **Restore the ledger from a known-good backup** (preferred for production), OR
   - **Write a code migration** that repairs the row in `UpAsync` using the provider's record store API. This keeps the repair auditable.

Do not silently `UPDATE` the ledger from an ad-hoc shell - the integrity check exists precisely to catch out-of-band mutations.

---

## Symptom: `MidRangeSquashException`

```
Hyperbee.Migrations.MidRangeSquashException: Squash 20260301000000 replaces [v1, v2, v3, v4] but ledger only contains [v1, v2].
```

**Cause:** A squash migration was applied (or partially applied) but the ledger contains only a subset of its `Replaces` set. This means the environment is in an inconsistent state - some originals ran, some did not, and the squash either ran or is about to.

This is a serious state and the runner stops to prevent further damage.

**Recovery:** There are three documented paths. Pick based on whether originals still exist in the assembly and whether the environment is production.

1. **Roll forward** (preferred when originals remain in the assembly). Use the recovery acknowledgement token to instruct the runner to converge to a consistent state. See [Squashing Migrations](squashing-migrations.md) for the token format and recovery flow.

2. **Roll back the squash and re-apply originals.** Only viable if the original migration classes still exist in the deployed assembly. Delete the squash row from the ledger, redeploy with the originals, and let the runner apply the missing ones.

3. **Reset the ledger and re-bootstrap.** Destructive - drops the ledger and re-applies everything from scratch. Only acceptable for ephemeral / non-production environments.

---

## Symptom: `StaleFleetMemberException`

```
Hyperbee.Migrations.StaleFleetMemberException: Environment 'eu-west-2-prod' has not reported in 47 days (window: 30 days).
```

**Cause:** A registered fleet environment has not run migrations within the configured staleness window (default 30 days). The runner refuses to proceed because the fleet manifest is no longer trustworthy - either the environment is dead, or its runner has been broken silently.

**Recovery:**

1. Confirm whether the environment is decommissioned:
   - **Yes, decommissioned.** Remove it from the fleet manifest and redeploy.
   - **No, still active.** Run the migration there to refresh its `LastSeen` timestamp. If that environment cannot run, investigate why - likely a CI/CD failure or a long-disabled deployment pipeline.

2. The staleness window is baked into the squash artifact (`SquashMetadata.MaxStalenessWindow`, default 30 days, set at generation time per [ADR-0019 amendment A15](https://github.com/Stillpoint-Software/hyperbee.migrations/blob/main/docs/decisions/0019-migration-squash-replaces-graph.md)). To adopt a different window, regenerate the squash with the desired `--max-staleness` value. Increasing the window suppresses the symptom but does not fix the underlying fleet-drift.

See [Multi-Provider Hosts](multi-provider-hosts.md) for fleet manifest details.

---

## Symptom: `UnregisteredEnvironmentException`

```
Hyperbee.Migrations.UnregisteredEnvironmentException: Environment 'us-east-1-canary' is not in the fleet manifest.
```

**Cause:** The runner started in an environment whose name is not listed in the fleet manifest. Fleet awareness is an opt-in safety mechanism; once enabled, only known environments are allowed to run migrations.

**Recovery:**

1. **Add the environment** to the manifest if it is legitimate, then redeploy. This is the standard path for new regions or new staging slots.

2. **Override with explicit acknowledgement.** Pass the documented acknowledgement token to the runner to bypass the check for a single boot. This is intended for emergency cutovers, not for routine use.

See [Multi-Provider Hosts](multi-provider-hosts.md) for manifest structure.

---

## Symptom: Aerospike "Operation not allowed at this time" (Error 22)

```
AerospikeException: Error 22: FORBIDDEN_OP
```

**Cause:** Server-side `FORBIDDEN_OP`. The most common trigger inside Hyperbee.Migrations is that the namespace has NSUP (namespace supervisor) disabled, which causes Aerospike to reject TTL'd writes. The migration lock and ledger rows are written with TTLs, so they are rejected outright.

**Recovery:**

- **Production / on-prem:** Set `nsup-period > 0` on the namespace in `aerospike.conf`:

  ```
  namespace test {
      nsup-period 120
      ...
  }
  ```

  Restart the Aerospike server.

- **Local Docker:** The `aerospike/aerospike-server` image's default config template only emits `nsup-period` when `DEFAULT_TTL` is set:

  ```shell
  docker run -e DEFAULT_TTL=86400 aerospike/aerospike-server
  ```

  Without this env var, the generated config has NSUP disabled and TTL'd writes will fail.

See [Aerospike](aerospike.md) for full configuration guidance.

---

## Symptom: OpenSearch "Cluster did not converge to GREEN within 180s"

```
OpenSearchClusterException: Cluster did not converge to GREEN within 180s.
```

**Cause:** A multi-node OpenSearch cluster failed to form. Common causes:

- Insufficient memory. Each node runs a JVM at minimum 512 MB; a 3-node cluster needs at least 1.5 GB free in addition to the OS and any other containers.
- Docker Desktop or container resource limits. Check the configured memory ceiling.
- Network discovery failure. Containers cannot reach each other on the discovery port (default 9300).

**Recovery:**

- **Local:** Check `docker stats` for OOMKilled nodes. Bump Docker Desktop's memory allocation. Verify the compose network has all nodes attached.

- **CI:** This fundamentally does not work on standard GitHub-hosted runners due to memory and CPU limits. Mark cluster-formation tests `[TestCategory("LocalOnly")]` and exclude them from the CI matrix. Run them on a self-hosted runner or locally only.

See [OpenSearch](opensearch.md) and [OpenSearch Template Propagation FAQ](opensearch-template-propagation-faq.md).

---

## Symptom: Couchbase "Scope not found in CB datastore"

```
CouchbaseException: Scope 'app' not found in CB datastore.
```

**Cause:** The bucket/scope/collection was created via the management API, but N1QL has not yet observed it. There is a propagation gap between cluster manager state and query service state.

The Couchbase provider waits up to 30s by default for N1QL visibility. If you still see this error, the gap is exceeding 30s on your cluster.

**Recovery:**

1. Increase `CouchbaseProviderOptions.ProvisionAttempts` and/or `ProvisionRetryInterval` in your DI registration.
2. Check Couchbase server load - high indexing or GC pressure on the query nodes widens the propagation gap.
3. Verify the scope was actually created (Couchbase UI -> Buckets -> Scopes & Collections).

See [Couchbase](couchbase.md) for the full options reference.

---

## Symptom: MongoDB "ResourceLocation Cannot find ..."

```
ResourceLocationException: Cannot find resource 'sample/users/alice.json'.
```

**Cause:** A resource-based migration referenced an embedded JSON file that is not actually embedded in the assembly. The runner enumerates files under the path prefix passed to `DocumentsFromAsync(["sample/users"])` and fails fast if the prefix yields no resources.

**Recovery:**

1. Verify the `.csproj` includes the file as an embedded resource:

   ```xml
   <ItemGroup>
     <EmbeddedResource Include="Migrations/sample/users/*.json" />
   </ItemGroup>
   ```

2. Rebuild and verify with `Assembly.GetManifestResourceNames()` if needed:

   ```csharp
   var names = typeof(MyMigration).Assembly.GetManifestResourceNames();
   ```

3. Confirm the path prefix matches the embedded resource path. Resource paths use dots, not slashes - the runner normalises this internally, but typos in the prefix produce empty enumerations.

See [Resource Migrations](resource-migrations.md) and [MongoDB](mongodb.md).

---

## Symptom: PostgreSQL connection failures during bootstrap

```
NpgsqlException: 42501: permission denied for schema public
```

or

```
NpgsqlException: 42P01: relation "migrations_ledger" does not exist
```

**Cause:** The provider creates both the lock and ledger tables in `InitializeAsync`. The configured role therefore needs DDL during bootstrap, plus DML on those tables for steady-state operation.

**Recovery:**

Verify the role has the following grants on the target schema:

```sql
GRANT CREATE ON SCHEMA public TO migrations_role;
GRANT INSERT, UPDATE, DELETE, SELECT ON migrations_lock, migrations_ledger TO migrations_role;
```

If creating tables under a non-default schema, ensure the `search_path` includes it or qualify the table names in `PostgresProviderOptions`.

Also verify the `ConnectionString` option resolves to the intended host/database. Connection-string typos produce 28P01 (auth) or 3D000 (database does not exist).

See [PostgreSQL](postgresql.md).

---

## Symptom: "Migrations failed but the ledger doesn't show the failed row"

**Cause:** This is by design, not a bug. The runner records ledger rows **after** `UpAsync` returns successfully. A migration that throws partway through leaves NO ledger row, so the next runner re-attempts it from scratch.

This is the correct behaviour for at-least-once semantics, but it places an obligation on migration authors:

**Recovery:** Make migrations idempotent so re-runs are safe.

- SQL: `CREATE TABLE IF NOT EXISTS`, `INSERT ... ON CONFLICT DO NOTHING`, `CREATE INDEX IF NOT EXISTS`.
- Couchbase: `CREATE PRIMARY INDEX IF NOT EXISTS`, `UPSERT` instead of `INSERT`.
- MongoDB: `ReplaceOne` with `upsert: true`, or guard with a `findOne` first.
- Aerospike: writes are upserts by default; index creation is idempotent if you check `GetIndexes()` first.
- OpenSearch: `PUT /_index_template/<name>` is idempotent.

If a migration cannot be made idempotent, split it into two migrations so the failure boundary is at a safe checkpoint.

See [Code Migrations](code-migrations.md).

---

## Symptom: "I want to mark a migration as applied without running it"

**Cause:** Not an error - a common operational request. Typically arises when:

- A migration was hand-applied out of band and needs to be marked applied.
- A migration is no longer relevant for new environments but cannot be deleted because old environments still reference it.

**Recovery:** Two supported paths:

1. **Write a no-op migration.** Create a migration class with the appropriate version and an empty `UpAsync` body. The runner records it like any other.

   ```csharp
   [Migration(20260301000000)]
   public class NoOpMigration : Migration
   {
       public override Task UpAsync( CancellationToken ct ) => Task.CompletedTask;
   }
   ```

2. **Custom record store override.** For one-off skips, register a record store that shadows the row. This is intentionally inconvenient - it is not a substitute for proper migration design.

For "mark a range of historical migrations as applied" at scale, use a squash. That is the supported workflow for collapsing migration history.

See [Squashing Migrations](squashing-migrations.md) and [Code Migrations](code-migrations.md).

---

## Still stuck?

- Re-read the relevant provider page: [Aerospike](aerospike.md), [Couchbase](couchbase.md), [PostgreSQL](postgresql.md), [OpenSearch](opensearch.md), [MongoDB](mongodb.md).
- Review [Concepts](concepts.md) for the lock/ledger model and runner lifecycle.
- File an issue at the [Hyperbee.Migrations repository](https://github.com/Stillpoint-Software/hyperbee.migrations) with the full exception message, the provider, and the migration class involved.
