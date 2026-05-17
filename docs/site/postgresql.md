---
layout: default
title: PostgreSQL Provider
nav_order: 13
---

# PostgreSQL Provider

The `Hyperbee.Migrations.Providers.Postgres` package provides PostgreSQL support for Hyperbee Migrations. Unlike the document-store providers, the Postgres provider does not define a custom statement DSL -- migrations are written as plain `.sql` files (or as code migrations against `NpgsqlDataSource`) and executed in versioned order. For cross-cutting concepts like profiles, cron, and journaling, see [Concepts](concepts.md).

## Installation

```shell
dotnet add package Hyperbee.Migrations.Providers.Postgres
```

Resource files (`.sql` files) ship as embedded resources from the migration project's csproj. The provider targets PostgreSQL 14+.

## Configuration

Register the Npgsql data source and the migration services with the DI container:

```csharp
services.AddNpgsqlDataSource( connectionString );

services.AddPostgresMigrations( options =>
{
    options.SchemaName     = "migration";    // default
    options.TableName      = "ledger";       // default
    options.LockingEnabled = true;
} );
```

### Provider options

| Option | Type | Default | Description |
|--------|------|---------|-------------|
| SchemaName | string | "migration" | Schema holding the ledger and lock tables. Created on first run if it does not exist. |
| TableName | string | "ledger" | Table holding `MigrationRecord` rows (`id`, `run_on`, `checksum`, `kind`, `replaces`). |
| LockingEnabled | bool | false | Enable the distributed lock. Production deployments should set this true. |
| LockName | string | "ledger_lock" | Table name (under `SchemaName`) holding the single-row lock record. |
| LockMaxLifetime | TimeSpan | 1 hour | Hard cap on lock hold time; doubles as the lock row's expiry stamp written at acquisition. |

For multi-provider hosts (e.g. PostgreSQL + MongoDB in the same app), resolve the typed runner `PostgresMigrationRunner` rather than the base `MigrationRunner`. See [Multi-Provider Hosts](multi-provider-hosts.md) for the registration and invocation pattern.

## Resource layout

A migration's resources live in a folder named after the migration class (or version). Each `.sql` file inside that folder is an executable resource. There is no `statements.json` wrapper -- the file IS the statement (or batch of statements).

```
Resources/
  1000-CreateInitialSchema/
    CreateSchema.sql
  2000-AddSecondaryIndexes/
    CreateIndexes.sql
```

Mark each file `EmbeddedResource` in the project file:

```xml
<ItemGroup>
  <None Remove="Resources\1000-CreateInitialSchema\CreateSchema.sql" />
  <None Remove="Resources\2000-AddSecondaryIndexes\CreateIndexes.sql" />
</ItemGroup>

<ItemGroup>
  <EmbeddedResource Include="Resources\1000-CreateInitialSchema\CreateSchema.sql" />
  <EmbeddedResource Include="Resources\2000-AddSecondaryIndexes\CreateIndexes.sql" />
</ItemGroup>
```

The `<None Remove>` entries prevent MSBuild from including the same file twice (once as content, once as embedded resource).

## Statement format

The Postgres provider does not define a statement grammar. Resource files are plain `.sql` containing standard PostgreSQL syntax. Each file may contain one or more statements separated by semicolons; the entire file is sent to the server as a single `NpgsqlCommand.CommandText`.

Use idempotent guards (`IF NOT EXISTS`, `IF EXISTS`, `ON CONFLICT DO NOTHING`) where it makes sense -- a partial failure mid-file leaves the prior statements applied and the ledger does not record the migration as succeeded, so the next run will re-execute the whole file.

Example (`Resources/1000-CreateInitialSchema/CreateSchema.sql`):

```sql
-- Create the sample schema and tables
CREATE SCHEMA IF NOT EXISTS sample;

CREATE TABLE IF NOT EXISTS sample.users
(
    user_id      SERIAL PRIMARY KEY,
    name         TEXT,
    email        TEXT NOT NULL,
    active       BOOLEAN NOT NULL DEFAULT false,
    role         TEXT,
    created_date TIMESTAMP WITH TIME ZONE NOT NULL
);

CREATE TABLE IF NOT EXISTS sample.products
(
    product_id   SERIAL PRIMARY KEY,
    name         TEXT,
    category     TEXT,
    price        NUMERIC(10,2),
    active       BOOLEAN NOT NULL DEFAULT false,
    created_date TIMESTAMP WITH TIME ZONE NOT NULL
);
```

For arbitrary parameterized SQL or multi-statement coordination, prefer a code migration (see below) over packing logic into a `.sql` resource.

## Code migration example

Inject `NpgsqlDataSource` to interact with PostgreSQL directly when the operation needs parameter binding, server-side cursors, or interleaved control flow:

```csharp
[Migration( 3000 )]
public class SeedData( NpgsqlDataSource dataSource, ILogger<SeedData> logger ) : Migration
{
    public override async Task UpAsync( CancellationToken cancellationToken = default )
    {
        logger.LogInformation( "Seeding data via code migration" );

        await using var connection = await dataSource.OpenConnectionAsync( cancellationToken );

        await using var cmd = new NpgsqlCommand(
            @"INSERT INTO sample.users (name, email, active, role, created_date)
              VALUES (@name, @email, @active, @role, @created_date)
              ON CONFLICT DO NOTHING",
            connection );

        cmd.Parameters.AddWithValue( "name",         "Admin User" );
        cmd.Parameters.AddWithValue( "email",        "admin@example.com" );
        cmd.Parameters.AddWithValue( "active",       true );
        cmd.Parameters.AddWithValue( "role",         "admin" );
        cmd.Parameters.AddWithValue( "created_date", DateTimeOffset.Parse( "2024-01-01T00:00:00Z" ) );

        await cmd.ExecuteNonQueryAsync( cancellationToken );
    }
}
```

## Resource migration example

Use `PostgresResourceRunner<T>` to execute embedded `.sql` files. `AllSqlFromAsync` runs every `.sql` file in the migration's resource folder (sorted by name); `SqlFromAsync` runs a specific subset.

```csharp
[Migration( 1000 )]
public class CreateInitialSchema( PostgresResourceRunner<CreateInitialSchema> runner ) : Migration
{
    public override Task UpAsync( CancellationToken cancellationToken = default )
        => runner.AllSqlFromAsync( cancellationToken );
}

[Migration( 2000 )]
public class AddSecondaryIndexes( PostgresResourceRunner<AddSecondaryIndexes> runner ) : Migration
{
    public override Task UpAsync( CancellationToken cancellationToken = default )
        => runner.SqlFromAsync( ["CreateIndexes.sql"], cancellationToken );
}
```

## Locking semantics

The provider uses a single-row lock table under `SchemaName` as a distributed lock (provider-native locking). Acquisition reads the lock row; if a row exists with `release_on` in the past, it is deleted and a fresh row is inserted; if a live row exists, `MigrationLockUnavailableException` is raised. `LockMaxLifetime` caps total wall-clock hold and is stamped on the lock row at acquisition time, so a crashed runner does not lock the schema forever -- the next runner sees the expired stamp and reclaims.

The lock table and the ledger table both live under `SchemaName` and are created during `InitializeAsync` if they do not exist. The lock implementation is intentionally application-level (a single-row table with TTL semantics) rather than `pg_advisory_lock` so its semantics match the other providers and the lock state is visible through `SELECT * FROM <schema>.<lock_table>` for ops debugging.

## Rollback

Postgres has true transactional DDL -- a `BEGIN; ... ROLLBACK;` undoes the whole batch on the server. Hyperbee Migrations does not wrap each migration in a transaction by default (some operations like `CREATE INDEX CONCURRENTLY` cannot run inside one), so authors choose the boundary:

- **Reversible by code migration.** Override `DownAsync` and run the inverse SQL (`DROP TABLE`, `DROP INDEX`, parameterized `DELETE`).
- **Reversible by paired SQL files.** Add a sibling `*_down.sql` per up file and call `runner.SqlFromAsync(["create_users_down.sql"], ct)` from `DownAsync`.
- **Wrap explicitly.** Where appropriate, begin the up SQL with `BEGIN;` and end with `COMMIT;` to make a single migration's failure roll back cleanly server-side. Skip this when any statement in the batch requires its own transaction context.

The ledger writes are journaled per the framework's standard `WriteAsync` semantics; a successful Up write is the durability boundary.

## Squash support

The Postgres provider is the squash-codegen reference implementation. It ships `PgDumpSnapshotStrategy` with `pg_dump --schema-only` as the capture mechanism. The canonical output is `.sql` text with the pg_dump preamble + psql directives stripped; the splitter (`PostgresStatementSplitter`) handles dollar-quoted strings; the classifier recognizes all 30+ Postgres DDL kinds plus refuses `CREATE INDEX CONCURRENTLY` (incompatible with squash transactionality).

The Roslyn-based `PostgresMigrationSourceScanner` enforces the `[DataMigration]` / `[StructuralOnly]` annotation requirement for migrations using `NpgsqlCommand` writes outside the structural DDL surface.

See [Squashing migrations](squashing-migrations.md) for the cross-provider squash CLI + workflow.

## Production deployment

The companion runner project (`runners/Hyperbee.MigrationRunner.Postgres`) is the recommended deployment shape. It binds the same `Migrations:*` configuration keys used by the in-process runner and is published as a Docker image alongside the other Hyperbee runners. See [Runners](runners.md) for CLI flags, the standard `appsettings.json` layout, and the `Migrations:FromAssemblies` / `Migrations:FromPaths` discovery shape.

A typical environment configuration:

```json
{
  "ConnectionStrings": {
    "Migrations": "Host=postgres.internal;Database=app;Username=migrator;Password=..."
  },
  "Migrations": {
    "SchemaName":     "migration",
    "TableName":      "ledger",
    "LockingEnabled": true,
    "FromAssemblies": ["Acme.App.Migrations"]
  }
}
```

## Samples

`runners/samples/Hyperbee.Migrations.Postgres.Samples` ships sample migrations covering both resource and code patterns:

- `1000-CreateInitialSchema` -- `AllSqlFromAsync` running `CreateSchema.sql` (`CREATE SCHEMA` plus two `CREATE TABLE` statements)
- `2000-AddSecondaryIndexes` -- `AllSqlFromAsync` running `CreateIndexes.sql`
- `3000-SeedData` -- code-migration pattern using `NpgsqlDataSource` with parameterized inserts and `ON CONFLICT DO NOTHING` for idempotency
