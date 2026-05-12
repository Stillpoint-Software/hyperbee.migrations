---
layout: default
title: Couchbase Provider
nav_order: 10
---

# Couchbase Provider

The `Hyperbee.Migrations.Providers.Couchbase` package provides Couchbase support for Hyperbee Migrations. It manages buckets, scopes, collections, indexes, and document seeding through both code and resource-based migrations using an N1QL-flavored statement grammar. For cross-cutting concepts like profiles, cron, and journaling, see [Concepts](concepts.md).

## Installation

```shell
dotnet add package Hyperbee.Migrations.Providers.Couchbase
```

Resource files (statement JSON, seed documents) ship as embedded resources from the migration project's csproj.

## Configuration

Register the Couchbase cluster (via `Couchbase.Extensions.DependencyInjection`) and the migration services with the DI container:

```csharp
services.AddCouchbase( options =>
{
    options.ConnectionString = "couchbase://localhost";
    options.UserName         = "Administrator";
    options.Password         = "password";
} );

services.AddCouchbaseMigrations( options =>
{
    options.BucketName     = "sample";          // required
    options.ScopeName      = "migrations";      // default
    options.CollectionName = "ledger";          // default
    options.LockingEnabled = true;
} );
```

### Provider options

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| BucketName | string | (required) | Bucket the ledger and lock documents live in. Created on first run if it does not exist. |
| ScopeName | string | "migrations" | Scope under the bucket. Created on first run. |
| CollectionName | string | "ledger" | Collection under the scope holding `MigrationRecord` documents and the lock document. |
| ClusterReadyTimeout | TimeSpan | 5 minutes | Time `CouchbaseBootstrapper` waits for the cluster, bucket, and N1QL service to become healthy at startup. |
| ProvisionRetryInterval | TimeSpan | 1 second | Backoff between bucket/scope/collection provisioning probes. |
| ProvisionAttempts | int | 30 | Maximum provisioning probes before bootstrap fails loudly. |
| LockingEnabled | bool | false | Enable the distributed lock. Production deployments should set this true. |
| LockName | string | (none) | Document key used for the lock record under the ledger collection. |
| LockMaxLifetime | TimeSpan | 1 hour | Hard cap on how long any single runner may hold the lock. |
| LockExpireInterval | TimeSpan | 5 minutes | Lock-document expiry written on each heartbeat (the safety TTL if a runner crashes). |
| LockRenewInterval | TimeSpan | 2 minutes | How often the holding runner renews the lock heartbeat (must be smaller than `LockExpireInterval`). |

For multi-provider hosts (e.g. Couchbase + MongoDB in the same app), resolve the typed runner `CouchbaseMigrationRunner` rather than the base `MigrationRunner`. See [Multi-Provider Hosts](multi-provider-hosts.md) (ADR-0023) for the registration and invocation pattern.

## Resource layout

A migration's resources live in a folder named after the migration class (or version). Statements live in `statements.json`; seed documents (optional) live in `<bucket>/<scope>/<key>.json` subfolders.

```
Resources/
  1000-CreateInitialSchema/
    statements.json
    sample/
      statements.json
      _default/
        ccuser001.json
        ccuser002.json
  2000-AddSecondaryIndexes/
    sample/
      statements.json
```

Mark each file `EmbeddedResource` in the project file:

```xml
<ItemGroup>
  <EmbeddedResource Include="Resources\1000-CreateInitialSchema\statements.json" />
  <EmbeddedResource Include="Resources\1000-CreateInitialSchema\sample\statements.json" />
  <EmbeddedResource Include="Resources\1000-CreateInitialSchema\sample\_default\ccuser001.json" />
  <EmbeddedResource Include="Resources\2000-AddSecondaryIndexes\sample\statements.json" />
</ItemGroup>
```

## Statement grammar

Statements use an N1QL (SQL++) flavored syntax. Statement keywords are case-insensitive. Identifiers may be plain (`sample`, `idx_users_email`) or backtick-quoted (`` `sample` ``) for names containing characters the plain-form parser does not accept. The grammar covers the keyspace and index lifecycle operations that make sense as migrations.

### Statement file format

The runner accepts two file shapes. The script form (`.statements`) is the recommended default for new migrations per [ADR-0022](https://github.com/Stillpoint-Software/hyperbee.migrations/blob/main/docs/decisions/0022-script-format-resource-migrations.md); the JSON-array form (`.statements.json`) is the original ADR-0002 wrapper and is supported indefinitely. Both parse to the same statement list.

Script form (`Resources/2000-AddSecondaryIndexes/sample/statements`):

```
-- Deferred build: each CREATE INDEX adds the index but doesn't build it
-- yet. The trailing BUILD INDEX kicks all deferred indexes off in one batch.

CREATE INDEX `idx_users_email`  ON `sample`(`email`)  WHERE `userId` IS NOT MISSING USING GSI WITH {'defer_build':true};
CREATE INDEX `idx_users_active` ON `sample`(`active`) WHERE `userId` IS NOT MISSING USING GSI WITH {'defer_build':true};

BUILD INDEX ON `sample` ( ( SELECT RAW name FROM system:indexes WHERE keyspace_id = 'sample' AND state = 'deferred' ));
```

JSON-array form (`Resources/2000-AddSecondaryIndexes/sample/statements.json`):

```json
{
  "statements": [
    { "statement": "CREATE INDEX `idx_users_email`  ON `sample`(`email`)  WHERE `userId` IS NOT MISSING USING GSI WITH {'defer_build':true}" },
    { "statement": "CREATE INDEX `idx_users_active` ON `sample`(`active`) WHERE `userId` IS NOT MISSING USING GSI WITH {'defer_build':true}" },
    { "statement": "BUILD INDEX ON `sample` ( ( SELECT RAW name FROM system:indexes WHERE keyspace_id = 'sample' AND state = 'deferred' ))" }
  ]
}
```

The script form supports `--`/`//` line comments and `/* ... */` block comments; statements are terminated by `;`. See [Resource Migrations](resource-migrations.md) for the cross-provider details.

### Statement summary

| Family | Form |
|--------|------|
| Bucket lifecycle | `CREATE BUCKET <name> [TYPE <type>] [RAMQUOTA <mb>] [FLUSH ENABLED] [REPLICAS <n>]` |
|                  | `DROP BUCKET <name>` |
| Scope lifecycle | `CREATE SCOPE <bucket>.<scope>` |
|                 | `DROP SCOPE <bucket>.<scope>` |
| Collection lifecycle | `CREATE COLLECTION <bucket>.<scope>.<collection>` |
|                      | `DROP COLLECTION <bucket>.<scope>.<collection>` |
| Index lifecycle | `CREATE PRIMARY INDEX [<name>] ON <keyspace> [USING GSI] [WITH {...}]` |
|                 | `CREATE INDEX <name> ON <keyspace>(<fields>) [WHERE ...] [USING GSI] [WITH {...}]` |
|                 | `BUILD INDEX ON <keyspace> ( <subquery> )` |
|                 | `DROP INDEX <keyspace>.<name>` |
| Records | `UPDATE <keyspace> SET ...` (data fix-ups) |

The full N1QL surface is intentionally NOT supported. For arbitrary N1QL (joins, aggregations, complex expressions), inject `IClusterProvider` and run a code migration -- see below.

```json
{
  "statements": [
    { "statement": "CREATE BUCKET `sample` TYPE Couchbase RAMQUOTA 100 FLUSH ENABLED" },
    { "statement": "CREATE PRIMARY INDEX `idx_sample_primary` ON `sample` WITH {'defer_build':true}" },
    { "statement": "BUILD INDEX ON `sample` ( ( SELECT RAW name FROM system:indexes WHERE keyspace_id = 'sample' AND state = 'deferred' ));" }
  ]
}
```

## Seed documents

Seed documents are JSON files stored at `<bucket>/<scope>/<key>.json`. The filename (without extension) becomes the Couchbase document key; the file body becomes the document content as-is.

```
Resources/1000-CreateInitialSchema/
  sample/_default/
    ccuser001.json
    ccuser002.json
```

Example document (`sample/_default/ccuser001.json`):

```json
{
  "userId": 1,
  "name":   "Alice Smith",
  "email":  "alice@example.com",
  "active": true
}
```

The resource runner discovers documents by walking the `<bucket>/<scope>` path passed to `DocumentsFromAsync`; each `.json` file becomes one document under the matching collection.

## Code migration example

Inject `IClusterProvider` to interact with Couchbase directly when the operation is outside the supported grammar:

```csharp
[Migration( 3000 )]
public class SeedData( IClusterProvider clusterProvider, ILogger<SeedData> logger ) : Migration
{
    public override async Task UpAsync( CancellationToken cancellationToken = default )
    {
        logger.LogInformation( "Seeding additional data via code migration" );

        var cluster    = await clusterProvider.GetClusterAsync().ConfigureAwait( false );
        var bucket     = await cluster.BucketAsync( "sample" ).ConfigureAwait( false );
        var collection = bucket.DefaultCollection();

        await collection.UpsertAsync( "user::003", new
        {
            userId = 3,
            name   = "Bob Johnson",
            email  = "bob@example.com",
            active = true
        } ).ConfigureAwait( false );
    }
}
```

## Resource migration example

Use `CouchbaseResourceRunner<T>` to execute embedded resource files. `StatementsFromAsync` runs the N1QL-flavored statements; `DocumentsFromAsync` writes seed documents.

```csharp
[Migration( 1000 )]
public class CreateInitialSchema( CouchbaseResourceRunner<CreateInitialSchema> runner ) : Migration
{
    public override async Task UpAsync( CancellationToken cancellationToken = default )
    {
        await runner.StatementsFromAsync( [
            "statements.json",
            "sample/statements.json"
        ], cancellationToken );

        await runner.DocumentsFromAsync( [
            "sample/_default"
        ], cancellationToken );
    }
}
```

## Locking semantics

The provider uses a Couchbase document on the ledger collection as a distributed lock (per [ADR-0005](https://github.com/Stillpoint-Software/hyperbee.migrations/blob/main/docs/decisions/0005-provider-native-distributed-locking.md), provider-native locking). Acquisition uses `Couchbase.Extensions.Locks.RequestMutexAsync`, which writes a TTL-bearing document with a CAS-protected create. The holding runner auto-renews the lock at `LockRenewInterval`; if the runner crashes, the document expires after `LockExpireInterval` and the next runner's acquisition succeeds. `LockMaxLifetime` caps total wall-clock hold so a hung migration cannot lock forever -- when reached, the in-flight migration is canceled cleanly via the cancellation token.

The migration ledger and the lock share the same collection (`<bucket>.<scope>.<collection>`); both are created on first run.

## Rollback

Each statement entry in `statements.json` may carry an optional `rollback` field. UpAsync runs `statement` fields in declaration order; DownAsync runs `rollback` fields in reverse declaration order.

```json
{
  "statements": [
    {
      "statement": "CREATE INDEX idx_users_active ON `sample`(`active`) USING GSI WITH {'defer_build':true}",
      "rollback":  "DROP INDEX `sample`.idx_users_active"
    }
  ]
}
```

For code migrations, override `DownAsync` and reverse the operations explicitly. Couchbase has no transactional DDL boundary -- a partial failure mid-migration leaves earlier statements applied. Use the rollback field on each statement so DownAsync can undo what UpAsync accomplished. Document upserts are idempotent on retry; bucket and scope creates are not -- pair them with conditional checks or rely on the ledger to skip already-succeeded runs.

## Squash support

The Couchbase provider ships full squash codegen via `HybridStrategy` (per ADR-0019). The canonical output is JSON-section form (`[buckets]`, `[scopes]`, `[collections]`, `[indexes]`, etc.) because Couchbase structural state (FTS index definitions, Eventing function source, GSI WITH clauses) exceeds the partial-grammar StatementParser. The capture path combines two sources: N1QL `system:keyspaces` + `system:indexes` for keyspaces and GSI indexes, REST `/pools/default/buckets/<name>` for bucket settings.

**Deferred-build GSI preservation:** the canonicalizer scope-aware-handles the `state` field in the `[indexes]` section -- `state=online` is dropped (default, no structural information), `state=deferred` is preserved (apply path issues BUILD INDEX), transient values (`building`/`pending`) throw at squash-time so the settle-wait bug surfaces immediately rather than producing a non-deterministic snapshot. This is the R-P3 OQ resolution for deferred-build indexes.

**Parameterized N1QL classification:** `Cluster.QueryAsync` / `AnalyticsQueryAsync` call-sites are treated as default-deny by both the data-op classifier and the Roslyn-based `CouchbaseMigrationSourceScanner` -- source-only inspection cannot resolve the SQL value reliably (variables, interpolation, builders), so operators annotate explicitly with `[DataMigration]` or `[StructuralOnly]`.

See [Squashing migrations](squashing-migrations.md) for the cross-provider squash CLI + workflow.

## Production deployment

The companion runner project (`runners/Hyperbee.MigrationRunner.Couchbase`) is the recommended deployment shape. It binds the same `Migrations:*` configuration keys used by the in-process runner and is published as a Docker image alongside the other Hyperbee runners. See [Runners](runners.md) for CLI flags, the standard `appsettings.json` layout, and the `Migrations:FromAssemblies` / `Migrations:FromPaths` discovery shape.

A typical environment configuration:

```json
{
  "Couchbase": {
    "ConnectionString": "couchbase://couchbase.internal",
    "UserName":         "migrator",
    "Password":         "..."
  },
  "Migrations": {
    "BucketName":      "app",
    "ScopeName":       "migrations",
    "CollectionName":  "ledger",
    "LockingEnabled":  true,
    "FromAssemblies":  ["Acme.App.Migrations"]
  }
}
```

## Samples

`runners/samples/Hyperbee.Migrations.Couchbase.Samples` ships sample migrations covering the supported statement surface plus seed-document and code-migration patterns:

- `1000-CreateInitialSchema` -- `CREATE BUCKET`, `CREATE PRIMARY INDEX` with `defer_build`, `BUILD INDEX`, and `DocumentsFromAsync` for seeded users
- `2000-AddSecondaryIndexes` -- multiple `CREATE INDEX` statements with partial-index `WHERE` clauses
- `3000-SeedData` -- code-migration pattern using `IClusterProvider` directly
