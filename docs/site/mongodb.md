---
layout: default
title: MongoDB Provider
nav_order: 11
---

# MongoDB Provider

The `Hyperbee.Migrations.Providers.MongoDB` package provides MongoDB support for Hyperbee Migrations. It manages collections, indexes, and document seeding through both code and resource-based migrations using a small SQL-flavored statement grammar. For cross-cutting concepts like profiles, cron, and journaling, see [Concepts](concepts.md).

## Installation

```shell
dotnet add package Hyperbee.Migrations.Providers.MongoDB
```

Resource files (statement JSON, seed documents) ship as embedded resources from the migration project's csproj. The provider targets MongoDB Server 6+ (replica set or standalone).

## Configuration

Register `IMongoClient` and the migration services with the DI container. `MongoClient` is thread-safe and intended to be a singleton.

```csharp
services.AddSingleton<IMongoClient>( new MongoClient( connectionString ) );

services.AddMongoDBMigrations( options =>
{
    options.DatabaseName    = "migration";   // default
    options.CollectionName  = "ledger";      // default
    options.LockingEnabled  = true;
} );
```

### Provider options

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| DatabaseName | string | "migration" | Database holding the ledger and lock documents. |
| CollectionName | string | "ledger" | Collection holding the ledger and (under a different `_id`) the lock document. |
| LockingEnabled | bool | false | Enable the distributed lock. Production deployments should set this true. |
| LockName | string | "ledger" | Logical lock name surfaced in `MigrationLockUnavailableException` messages. |
| LockMaxLifetime | TimeSpan | 1 hour | Hard cap on lock hold time; doubles as the lock document's expiry stamp. |

For multi-provider hosts (e.g. PostgreSQL + MongoDB in the same app), resolve the typed runner `MongoDBMigrationRunner` rather than the base `MigrationRunner`. See [Multi-Provider Hosts](multi-provider-hosts.md) for the registration and invocation pattern.

## Resource layout

A migration's resources live in a folder named after the migration class (or version). Statements live in `statements.json`; seed documents (optional) live in `<database>/<collection>/<key>.json` subfolders.

```
Resources/
  1000-CreateInitialSchema/
    sample/
      users/
        user1.json
        user2.json
      products/
        product1.json
        product2.json
  2000-AddSecondaryIndexes/
    statements.json
```

Mark each file `EmbeddedResource` in the project file:

```xml
<ItemGroup>
  <EmbeddedResource Include="Resources\1000-CreateInitialSchema\sample\users\user1.json" />
  <EmbeddedResource Include="Resources\1000-CreateInitialSchema\sample\products\product1.json" />
  <EmbeddedResource Include="Resources\2000-AddSecondaryIndexes\statements.json" />
</ItemGroup>
```

## Statement grammar

Statements use a small SQL-flavored DSL -- not the Mongo-shell JavaScript syntax. Statement keywords are case-insensitive. Identifiers may be plain (`users`, `idx_users_email`) or backtick-quoted (`` `users` ``). The grammar covers the collection and index lifecycle operations that make sense as migrations.

### Statement file format

The runner accepts two file shapes. The script form (`.statements`) is the recommended default for new migrations (see [Resource migrations](resource-migrations.md)); the JSON-array form (`.statements.json`) is the original wrapper and is supported indefinitely. Both parse to the same statement list.

Script form (`Resources/2000-AddSecondaryIndexes/statements`):

```
-- Unique constraint on user email.
CREATE UNIQUE INDEX idx_users_email ON sample.users (email);

-- Common filters used by the application.
CREATE INDEX idx_users_active ON sample.users (active);
CREATE INDEX idx_users_role   ON sample.users (role);
```

JSON-array form (`Resources/2000-AddSecondaryIndexes/statements.json`):

```json
{
  "statements": [
    { "statement": "CREATE UNIQUE INDEX idx_users_email ON sample.users (email)" },
    { "statement": "CREATE INDEX idx_users_active ON sample.users (active)" },
    { "statement": "CREATE INDEX idx_users_role ON sample.users (role)" }
  ]
}
```

The script form supports `--`/`//` line comments and `/* ... */` block comments; statements are terminated by `;`. See [Resource Migrations](resource-migrations.md) for the cross-provider details.

### Statement summary

| Family | Form |
|--------|------|
| Collection lifecycle | `CREATE COLLECTION <db>.<col>` |
|                      | `DROP COLLECTION <db>.<col>` |
| Index lifecycle | `CREATE INDEX <name> ON <db>.<col>(<field1>, <field2>, ...)` |
|                 | `CREATE UNIQUE INDEX <name> ON <db>.<col>(<field1>, <field2>, ...)` |
|                 | `DROP INDEX <name> ON <db>.<col>` |
| Records | `INSERT INTO <db>.<col>` (intent-only -- use `DocumentsFromAsync` for actual seeding) |

The grammar is intentionally narrow. For arbitrary MongoDB commands (aggregation pipelines, schema validation rules, time-series options), inject `IMongoClient` and run a code migration -- see below.

```json
{
  "statements": [
    { "statement": "CREATE UNIQUE INDEX idx_users_email ON sample.users (email)" },
    { "statement": "CREATE INDEX idx_users_active ON sample.users (active)" },
    { "statement": "CREATE INDEX idx_products_category ON sample.products (category)" }
  ]
}
```

## Seed documents

Seed documents are JSON files stored at `<database>/<collection>/<key>.json`. The filename (without extension) becomes a logical key; the file body becomes the document content as a `BsonDocument`. If the document body contains an `_id` field it is used as the Mongo `_id`; otherwise Mongo assigns one.

```
Resources/1000-CreateInitialSchema/
  sample/users/
    user1.json
    user2.json
```

Example document (`sample/users/user1.json`):

```json
{
  "userId":      1,
  "name":        "Admin User",
  "email":       "admin@example.com",
  "active":      true,
  "role":        "admin",
  "createdDate": "2024-01-01T00:00:00Z"
}
```

The resource runner discovers documents by walking the `<database>/<collection>` path passed to `DocumentsFromAsync`; each `.json` file becomes one inserted document under the matching collection.

## Code migration example

Inject `IMongoClient` to interact with MongoDB directly when the operation is outside the supported grammar:

```csharp
[Migration( 3000 )]
public class SeedData( IMongoClient mongoClient, ILogger<SeedData> logger ) : Migration
{
    public override async Task UpAsync( CancellationToken cancellationToken = default )
    {
        logger.LogInformation( "Seeding data via code migration" );

        var db    = mongoClient.GetDatabase( "sample" );
        var users = db.GetCollection<BsonDocument>( "users" );

        await users.InsertOneAsync( new BsonDocument
        {
            { "userId",      3 },
            { "name",        "Bob Johnson" },
            { "email",       "bob@example.com" },
            { "active",      true },
            { "role",        "user" },
            { "createdDate", "2024-06-01T09:00:00Z" }
        }, cancellationToken: cancellationToken );
    }
}
```

## Resource migration example

Use `MongoDBResourceRunner<T>` to execute embedded resource files. `StatementsFromAsync` runs the SQL-flavored statements; `DocumentsFromAsync` writes seed documents.

```csharp
[Migration( 1000 )]
public class CreateInitialSchema( MongoDBResourceRunner<CreateInitialSchema> runner ) : Migration
{
    public override async Task UpAsync( CancellationToken cancellationToken = default )
    {
        await runner.DocumentsFromAsync( [
            "sample/users",
            "sample/products"
        ], cancellationToken );
    }
}

[Migration( 2000 )]
public class AddSecondaryIndexes( MongoDBResourceRunner<AddSecondaryIndexes> runner ) : Migration
{
    public override Task UpAsync( CancellationToken cancellationToken = default )
        => runner.StatementsFromAsync( "statements.json", cancellationToken );
}
```

## Locking semantics

The provider uses a single MongoDB document inside the ledger collection as a distributed lock (provider-native locking). Acquisition is a single `InsertOneAsync` against a fixed `_id`; on conflict, the existing lock's `ReleaseOn` is checked -- expired locks are deleted and re-acquired, live locks throw `MigrationLockUnavailableException`. `LockMaxLifetime` caps total wall-clock hold and is stamped on the lock document at acquisition time, so a crashed runner does not lock forever.

The lock and the ledger share the same collection (`<database>.<collection>`); reads use `ReadConcern.Majority` and `ReadPreference.Primary` so replica-set deployments see committed writes and standalones fall back to local semantics automatically.

## Rollback

Each statement entry in `statements.json` may carry an optional `rollback` field. UpAsync runs `statement` fields in declaration order; DownAsync runs `rollback` fields in reverse declaration order.

```json
{
  "statements": [
    {
      "statement": "CREATE UNIQUE INDEX idx_users_email ON sample.users (email)",
      "rollback":  "DROP INDEX idx_users_email ON sample.users"
    }
  ]
}
```

For code migrations, override `DownAsync` and reverse the operations explicitly. MongoDB has no transactional DDL boundary on standalone deployments and only limited multi-document transactions on replica sets -- prefer idempotent operations (`CREATE COLLECTION` is a no-op on existing collections; `CREATE INDEX` keys are content-addressed) and rely on the ledger to skip already-succeeded runs. Inserts written via `DocumentsFromAsync` are not automatically reversed -- pair seed inserts with explicit `DELETE` rollback statements when reversibility matters.

## Squash support

The MongoDB provider ships full squash codegen via `IntrospectionSnapshotStrategy`. The canonical output is JSON-section form (`[collections]`, `[indexes]`, etc.) because MongoDB structural state (validators, time-series options, view pipelines, partialFilterExpression on indexes) exceeds the narrow MongoStatementParser grammar. The capture path probes the live cluster via the `listCollections` + per-collection `Indexes.List` admin commands; the canonicalizer strips ephemeral fields (`uuid`, `readOnly`, `v`, `ns`) at every nesting level.

Capture uses BSON Extended JSON v2 (`CanonicalExtendedJson`) so MongoDB-specific types (ObjectId, BinData, Decimal128, BsonDateTime) round-trip losslessly. The canonicalizer treats payloads as opaque JSON value content -- BSON-flavored values ride through unchanged.

The Roslyn-based `MongoDBMigrationSourceScanner` enforces the `[DataMigration]` / `[StructuralOnly]` annotation requirement for migrations using `collection.InsertOneAsync`, `BulkWriteAsync`, `UpdateManyAsync`, and related write call-sites.

See [Squashing migrations](squashing-migrations.md) for the cross-provider squash CLI + workflow.

## Production deployment

The companion runner project (`runners/Hyperbee.MigrationRunner.MongoDB`) is the recommended deployment shape. It binds the same `Migrations:*` configuration keys used by the in-process runner and is published as a Docker image alongside the other Hyperbee runners. See [Runners](runners.md) for CLI flags, the standard `appsettings.json` layout, and the `Migrations:FromAssemblies` / `Migrations:FromPaths` discovery shape.

A typical environment configuration:

```json
{
  "Mongo": {
    "ConnectionString": "mongodb://mongo.internal:27017"
  },
  "Migrations": {
    "DatabaseName":    "app",
    "CollectionName":  "ledger",
    "LockingEnabled":  true,
    "FromAssemblies":  ["Acme.App.Migrations"]
  }
}
```

## Samples

`runners/samples/Hyperbee.Migrations.MongoDB.Samples` ships sample migrations covering the supported statement surface plus seed-document and code-migration patterns:

- `1000-CreateInitialSchema` -- `DocumentsFromAsync` for seeded users and products under `sample/users` and `sample/products`
- `2000-AddSecondaryIndexes` -- `CREATE INDEX` and `CREATE UNIQUE INDEX` statements
- `3000-SeedData` -- code-migration pattern using `IMongoClient` directly
