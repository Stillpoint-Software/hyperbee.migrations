---
layout: default
title: Aerospike Provider
nav_order: 9
---

# Aerospike Provider

The `Hyperbee.Migrations.Providers.Aerospike` package provides Aerospike support for Hyperbee Migrations. It handles schema changes, index management, and data seeding through both code and resource-based migrations. For cross-cutting concepts like profiles, cron, and journaling, see [Concepts](concepts.md).

## Installation

```shell
dotnet add package Hyperbee.Migrations.Providers.Aerospike
```

## Configuration

Register the Aerospike client and migration services with the DI container:

```csharp
services.AddSingleton<IAsyncClient>( new AsyncClient( "localhost", 3000 ) );
services.AddSingleton<IAerospikeClient>( sp => sp.GetRequiredService<IAsyncClient>() as IAerospikeClient );

services.AddAerospikeMigrations( options =>
{
    options.Namespace    = "test";                 // Aerospike namespace
    options.MigrationSet = "SchemaMigrations";     // set for journal records
} );
```

### Provider options

| Option | Type | Default |
|--------|------|---------|
| Namespace | string | "test" |
| MigrationSet | string | "SchemaMigrations" |
| LockName | string | "migration_lock" |
| LockMaxLifetime | TimeSpan | 1 hour |
| LockingEnabled | bool | true |

### Locking

The provider uses a distributed lock stored as an Aerospike record to prevent simultaneous migration runners.

```csharp
services.AddAerospikeMigrations( options =>
{
    options.LockingEnabled  = true;                            // default
    options.LockName        = "migration_lock";                // lock record key
    options.LockMaxLifetime = TimeSpan.FromHours( 1 );         // max time-to-live
} );
```

## Resource layout

A migration's resources live in a folder named after the migration class (or version). Statements live in `statements.json`; seed documents (optional) live in `<namespace>/<set>/<key>.json` subfolders.

```
Resources/
  1000-CreateInitialSchema/
    statements.json
    test/
      users/
        admin.json
        user1.json
        user2.json
  2000-AddSecondaryIndexes/
    statements.json
```

Mark each file `EmbeddedResource` in the project file:

```xml
<ItemGroup>
  <EmbeddedResource Include="Resources\1000-CreateInitialSchema\statements.json" />
  <EmbeddedResource Include="Resources\1000-CreateInitialSchema\test\users\admin.json" />
  <EmbeddedResource Include="Resources\1000-CreateInitialSchema\test\users\user1.json" />
</ItemGroup>
```

## Statement grammar

Statements use AQL-flavored syntax. Statement keywords are case-insensitive. Identifiers may be plain (`users`, `idx_users_email`) or backtick-quoted (`` `users.archive` ``) for names containing characters the plain-form parser does not accept.

The grammar is a subset of AQL focused on the operations that make sense as migrations -- index lifecycle and intent-only declarations for set creation and bulk record I/O.

### Statement file format

The runner accepts two file shapes. The script form (`.pql`) is the recommended default for new migrations (see [Resource migrations](resource-migrations.md)); the JSON-array form (`.statements.json`) is the original wrapper and is supported indefinitely. Both parse to the same statement list.

Script form (`Resources/2000-AddSecondaryIndexes/statements`):

```
-- Secondary indexes for the application's queries.
CREATE INDEX WAIT idx_users_role ON test.users (role) STRING;
CREATE INDEX WAIT idx_products_category ON test.products (category) STRING;
CREATE INDEX WAIT idx_products_price    ON test.products (price)    NUMERIC;
```

JSON-array form (`Resources/2000-AddSecondaryIndexes/statements.json`):

```json
{
  "statements": [
    { "statement": "CREATE INDEX WAIT idx_users_role ON test.users (role) STRING" },
    { "statement": "CREATE INDEX WAIT idx_products_category ON test.products (category) STRING" },
    { "statement": "CREATE INDEX WAIT idx_products_price ON test.products (price) NUMERIC" }
  ]
}
```

The script form supports `--`/`//` line comments and `/* ... */` block comments; statements are terminated by `;`. See [Resource Migrations](resource-migrations.md) for the cross-provider details.

### Statement summary

| Family | Form |
|--------|------|
| Index lifecycle | `CREATE INDEX [IF NOT EXISTS] [RECREATE] [WAIT] <name> ON <ns>.<set> (<bin>) [STRING|NUMERIC|GEO2DSPHERE]` |
|                 | `DROP INDEX <namespace> <name>` |
| Set lifecycle | `CREATE SET <ns>.<set>` |
| Records | `INSERT INTO <ns>.<set> (<columns>) VALUES (<values>)` |
|         | `DELETE FROM <ns>.<set> WHERE PK = '<key>'` |

## Statement reference

### CREATE INDEX

```
CREATE INDEX [IF NOT EXISTS] [RECREATE] [WAIT] <name> ON <ns>.<set> (<bin>) [STRING|NUMERIC|GEO2DSPHERE]
```

Creates a secondary index on a bin. Aerospike indexes are async by default (the cluster builds them in the background); use the `WAIT` flag to block the migration until the index is ready.

| Flag | Meaning |
|------|---------|
| `IF NOT EXISTS` | Parsed for AQL-familiarity. `CREATE INDEX` is already idempotent at the Aerospike API level, so the flag is accepted but does not change behavior. |
| `RECREATE` | Drop the index first if it already exists, then create it. Use when you need to change the bin or index type for an existing index name. |
| `WAIT` | Block until the index is fully built across the cluster before continuing. Without `WAIT`, the statement returns as soon as the index creation request is accepted. |

The index type defaults to `STRING` when omitted. Supported types:

- `STRING` -- secondary index on a string bin
- `NUMERIC` -- secondary index on an integer bin
- `GEO2DSPHERE` -- secondary index on a GeoJSON bin (point or region)

```json
{
  "statements": [
    { "statement": "CREATE INDEX WAIT idx_users_email ON test.users (email) STRING" },
    { "statement": "CREATE INDEX WAIT idx_users_active ON test.users (active) NUMERIC" },
    { "statement": "CREATE INDEX WAIT idx_stores_location ON test.stores (location) GEO2DSPHERE" }
  ]
}
```

Replace an existing index in place:

```json
{
  "statement": "CREATE INDEX RECREATE WAIT idx_users_role ON test.users (role) STRING"
}
```

### DROP INDEX

```
DROP INDEX <namespace> <name>
```

Removes a secondary index. Note that AQL's `DROP INDEX` shape uses a space (not a dot, not `ON`) between namespace and index name -- the parser follows that convention exactly.

```json
{ "statement": "DROP INDEX test idx_users_active" }
```

### CREATE SET

```
CREATE SET <namespace>.<set>
```

Declarative intent-only statement. Aerospike creates sets implicitly on first write, so no explicit set-creation API exists at the protocol level. The provider logs an INFO message when this statement is encountered and proceeds. Use it to make set ownership explicit at the migration level, e.g., to record that a particular migration introduced a particular set.

```json
{ "statement": "CREATE SET test.audit_log" }
```

### INSERT INTO / DELETE FROM

```
INSERT INTO <namespace>.<set> (<columns>) VALUES (<values>)
DELETE FROM <namespace>.<set> WHERE PK = '<key>'
```

Both statements are intent-only: the parser captures the namespace and set names but does not perform the actual I/O. For seeding records, use the resource runner's `DocumentsFromAsync` method instead (see "Seed documents" below). For surgical record edits, inject `IAsyncClient` and use the client API directly from a code migration.

```json
{
  "statements": [
    { "statement": "INSERT INTO test.users (PK, name, email) VALUES ('user-001', 'Alice', 'a@x.com')" },
    { "statement": "DELETE FROM test.users WHERE PK = 'user-orphan'" }
  ]
}
```

The provider logs each as INFO with a pointer to the supported alternative. Choose the supported path for production migrations:

- Bulk seed -> `DocumentsFromAsync` (resource files)
- Surgical edit -> code migration with `IAsyncClient` injection

## Code migration example

Inject `IAsyncClient` to interact with Aerospike directly:

```csharp
[Migration( 3000 )]
public class SeedData( IAsyncClient asyncClient, ILogger<SeedData> logger ) : Migration
{
    public override async Task UpAsync( CancellationToken cancellationToken = default )
    {
        logger.LogInformation( "Seeding data via code migration" );

        await asyncClient.Put( null, cancellationToken,
            new Key( "test", "users", "user-003" ),
            new Bin( "name",   "Bob Johnson" ),
            new Bin( "email",  "bob@example.com" ),
            new Bin( "active", 1 )
        ).ConfigureAwait( false );
    }
}
```

## Resource migration example

Use `AerospikeResourceRunner<T>` to execute embedded resource files. `StatementsFromAsync` runs the AQL statements; `DocumentsFromAsync` writes seed records.

```csharp
[Migration( 1000 )]
public class CreateInitialSchema( AerospikeResourceRunner<CreateInitialSchema> runner ) : Migration
{
    public override async Task UpAsync( CancellationToken cancellationToken = default )
    {
        await runner.StatementsFromAsync( ["statements.json"], cancellationToken );

        await runner.DocumentsFromAsync( ["test/users"], cancellationToken );
    }
}
```

## Seed documents

Seed documents are JSON files stored at `<namespace>/<set>/<key>.json`. Each file must contain an `id` (or `PK`) field -- this becomes the Aerospike record key. All other top-level properties are stored as bins.

```
Resources/1000-CreateInitialSchema/
  statements.json
  test/users/
    admin.json
    user1.json
```

Example document (`test/users/admin.json`):

```json
{
  "id":     "user-admin",
  "name":   "Admin User",
  "email":  "admin@example.com",
  "active": 1
}
```

The resource runner discovers documents by walking the `<namespace>/<set>` path passed to `DocumentsFromAsync`. Each `.json` file becomes one record; the `id`/`PK` field is removed from the bin set and used as the record key.

## Locking semantics

The provider uses a single Aerospike record as a distributed lock. Acquisition uses a generation-aware put so two runners cannot both claim the lock; the holder's heartbeat refreshes the record TTL. `LockMaxLifetime` caps total wall-clock hold so a hung migration cannot lock forever -- when reached, the in-flight migration is canceled cleanly via the cancellation token.

## Squash support

The Aerospike provider ships full squash codegen via `InfoSnapshotStrategy`. The canonical output is AQL statement form: `CREATE INDEX` + sentinel-set membership recorded as section-headered text. The capture path probes the live cluster via `Info.Request("sets/<namespace>", "sindex/<namespace>")` and an injected snapshot delegate; the canonicalizer strips ephemeral counters (`objects`, `memory_used`, etc.) at every level.

The Roslyn-based `AerospikeMigrationSourceScanner` enforces the `[DataMigration]` / `[StructuralOnly]` annotation requirement for migrations that write data via the SDK.

See [Squashing migrations](squashing-migrations.md) for the cross-provider squash CLI + workflow.

## Production deployment

The companion runner project (`runners/Hyperbee.MigrationRunner.Aerospike`) is the recommended deployment shape. See [Runners](runners.md) for CLI flags and configuration.

## Samples

`runners/samples/Hyperbee.Migrations.Aerospike.Samples` ships sample migrations covering the full statement surface plus seed-document patterns:

- `1000-CreateInitialSchema` -- `CREATE INDEX WAIT` for users; `DocumentsFromAsync` for seeded users
- `2000-AddSecondaryIndexes` -- additional `CREATE INDEX WAIT` statements for products
- `3000-SeedData` -- code-migration pattern using `IAsyncClient.Put` directly
