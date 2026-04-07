---
layout: default
title: PostgreSQL Provider
nav_order: 11
---

# PostgreSQL Provider

The `Hyperbee.Migrations.Providers.Postgres` package provides PostgreSQL support for Hyperbee Migrations.
It handles schema changes, table management, and data seeding through code and resource-based migrations.
For cross-cutting concepts like profiles, cron, and journaling, see [Concepts](concepts.md).

## Installation

```shell
dotnet add package Hyperbee.Migrations.Providers.Postgres
```

## Configuration

Register the Npgsql data source and migration services with the DI container:

```csharp
services.AddNpgsqlDataSource( connectionString );

services.AddPostgresMigrations( options =>
{
    options.SchemaName = "migration";               // default
    options.TableName = "ledger";                   // default
});
```

## Locking

The provider uses a PostgreSQL-based distributed lock to prevent simultaneous migration runners.

```csharp
services.AddPostgresMigrations( options =>
{
    options.LockingEnabled = true;                                  // default
    options.LockName = "ledger_lock";                               // lock identifier
    options.LockMaxLifetime = TimeSpan.FromHours( 1 );             // max time-to-live
});
```

## Code Migration Example

Inject `NpgsqlDataSource` to interact with PostgreSQL directly:

```csharp
[Migration( 3000 )]
public class SeedData( NpgsqlDataSource dataSource, ILogger<SeedData> logger ) : Migration
{
    public override async Task UpAsync( CancellationToken cancellationToken = default )
    {
        logger.LogInformation( "Seeding data via code migration" );

        await using var conn = await dataSource.OpenConnectionAsync( cancellationToken );
        await using var cmd = conn.CreateCommand();

        cmd.CommandText = "INSERT INTO sample.users (name, email) VALUES ('Bob Johnson', 'bob@example.com')";
        await cmd.ExecuteNonQueryAsync( cancellationToken );
    }
}
```

## Resource Migration Example

Use `PostgresResourceRunner<T>` to execute embedded SQL files. Unlike other providers, Postgres
uses plain `.sql` files -- not JSON statement wrappers.

```csharp
[Migration( 1000 )]
public class CreateInitialSchema( PostgresResourceRunner<CreateInitialSchema> resourceRunner ) : Migration
{
    public override async Task UpAsync( CancellationToken cancellationToken = default )
    {
        // run all .sql files matching this migration
        await resourceRunner.AllSqlFromAsync( cancellationToken );
    }
}

[Migration( 2000 )]
public class AddSecondaryIndexes( PostgresResourceRunner<AddSecondaryIndexes> resourceRunner ) : Migration
{
    public override async Task UpAsync( CancellationToken cancellationToken = default )
    {
        // run specific .sql files
        await resourceRunner.SqlFromAsync( [
            "create_indexes.sql"
        ], cancellationToken );
    }
}
```

## Statement Format

Resource files are plain `.sql` files containing standard PostgreSQL statements. Each file can
contain one or more SQL statements separated by semicolons.

Example (`create_schema.sql`):

```sql
CREATE SCHEMA IF NOT EXISTS sample;

CREATE TABLE IF NOT EXISTS sample.users (
    id SERIAL PRIMARY KEY,
    name VARCHAR(100) NOT NULL,
    email VARCHAR(255) NOT NULL,
    active BOOLEAN DEFAULT true,
    created_at TIMESTAMP DEFAULT CURRENT_TIMESTAMP
);
```

## Document Format

The Postgres provider does not use JSON document files. All data operations are performed through
`.sql` files using standard SQL `INSERT`, `UPDATE`, or `COPY` statements.

```
Resources/1000-CreateInitialSchema/
  create_schema.sql
  create_tables.sql
  seed_data.sql
```

## Provider Options Reference

| Option           | Type       | Default        |
|------------------|------------|----------------|
| SchemaName       | string     | "migration"    |
| TableName        | string     | "ledger"       |
| LockName         | string     | "ledger_lock"  |
| LockMaxLifetime  | TimeSpan   | 1 hour         |
| LockingEnabled   | bool       | true           |
