---
layout: default
title: Aerospike Provider
nav_order: 8
---

# Aerospike Provider

The `Hyperbee.Migrations.Providers.Aerospike` package provides Aerospike support for Hyperbee Migrations.
It handles schema changes, index management, and data seeding through both code and resource-based migrations.
For cross-cutting concepts like profiles, cron, and journaling, see [Concepts](concepts.md).

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
    options.Namespace = "test";                     // Aerospike namespace
    options.MigrationSet = "SchemaMigrations";      // set for journal records
});
```

## Locking

The provider uses a distributed lock stored as an Aerospike record to prevent simultaneous migration runners.

```csharp
services.AddAerospikeMigrations( options =>
{
    options.LockingEnabled = true;                                  // default
    options.LockName = "migration_lock";                            // lock record key
    options.LockMaxLifetime = TimeSpan.FromHours( 1 );             // max time-to-live
});
```

## Code Migration Example

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
            new Bin( "name", "Bob Johnson" ),
            new Bin( "email", "bob@example.com" ),
            new Bin( "active", 1 )
        ).ConfigureAwait( false );
    }
}
```

## Resource Migration Example

Use `AerospikeResourceRunner<T>` to execute embedded resource files:

```csharp
[Migration( 1000 )]
public class CreateInitialSchema( AerospikeResourceRunner<CreateInitialSchema> resourceRunner ) : Migration
{
    public override async Task UpAsync( CancellationToken cancellationToken = default )
    {
        await resourceRunner.StatementsFromAsync( [
            "statements.json"
        ], cancellationToken );

        await resourceRunner.DocumentsFromAsync( [
            "test/users"
        ], cancellationToken );
    }
}
```

## Statement Syntax

Statements use AQL syntax inside a JSON wrapper. The `WAIT` keyword blocks until the index is built.

```json
{
  "statements": [
    { "statement": "CREATE INDEX WAIT idx_users_email ON test.users (email) STRING" },
    { "statement": "CREATE INDEX WAIT idx_users_active ON test.users (active) NUMERIC" }
  ]
}
```

Supported index types: `STRING`, `NUMERIC`, `GEO2DSPHERE`.

## Document Format

Documents are JSON files stored at `namespace/set/key.json`. Each file must contain an `id` or `PK`
field that becomes the Aerospike record key. All other properties are stored as bins.

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
  "id": "user-admin",
  "name": "Admin User",
  "email": "admin@example.com",
  "active": 1
}
```

## Provider Options Reference

| Option             | Type       | Default              |
|--------------------|------------|----------------------|
| Namespace          | string     | "test"               |
| MigrationSet       | string     | "SchemaMigrations"   |
| LockName           | string     | "migration_lock"     |
| LockMaxLifetime    | TimeSpan   | 1 hour               |
| LockingEnabled     | bool       | true                 |
