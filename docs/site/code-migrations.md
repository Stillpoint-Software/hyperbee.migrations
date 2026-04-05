---
layout: default
title: Code Migrations
nav_order: 4
---

# Code Migrations

Code migrations give you full control over database operations by writing C# code
directly. You receive database clients and services through constructor injection,
implement your forward logic in `UpAsync`, and optionally provide rollback logic
in `DownAsync`.

## Basic Structure

```csharp
[Migration(1000)]
public class CreateUsersCollection : Migration
{
    private readonly IMongoClient _client;
    private readonly ILogger<CreateUsersCollection> _logger;

    public CreateUsersCollection(IMongoClient client, ILogger<CreateUsersCollection> logger)
    {
        _client = client;
        _logger = logger;
    }

    public override async Task UpAsync(CancellationToken cancellationToken = default)
    {
        _logger.LogInformation("Creating users collection");
        var db = _client.GetDatabase("myapp");
        await db.CreateCollectionAsync("users", cancellationToken: cancellationToken);
    }

    public override async Task DownAsync(CancellationToken cancellationToken = default)
    {
        var db = _client.GetDatabase("myapp");
        await db.DropCollectionAsync("users", cancellationToken: cancellationToken);
    }
}
```

## Dependency Injection

Any service registered in the DI container can be injected into a migration
constructor. Common injections include database clients, loggers, and configuration.

Each provider exposes its own client type:

| Provider   | Client Type                          |
|------------|--------------------------------------|
| PostgreSQL | `NpgsqlDataSource`                   |
| MongoDB    | `IMongoClient`                       |
| Aerospike  | `IAsyncClient`, `IAerospikeClient`   |
| Couchbase  | `IClusterProvider`                   |

## Writing Idempotent Migrations

Migrations should be safe to re-run. Use provider-specific idioms to achieve
idempotency:

- **SQL** -- `CREATE TABLE IF NOT EXISTS`, `CREATE INDEX IF NOT EXISTS`,
  `ON CONFLICT DO NOTHING`
- **MongoDB** -- Check if a collection exists before creating it, use `UpdateOne`
  with upsert
- **Aerospike** -- Catch `INDEX_ALREADY_EXISTS` exceptions when creating
  secondary indexes
- **Couchbase** -- Use `IF NOT EXISTS` in N1QL statements

## Scheduled and Continuous Migrations

Migrations can run on a recurring schedule using the `Cron` attribute property,
or implement `IContinuousMigration` for custom lifecycle control with full
CancellationToken support.

See the dedicated [Continuous Migrations](continuous-migrations.md) page for
cron scheduling, the IContinuousMigration interface, and execution patterns.

## Disabling Journaling

When a migration should run every time -- regardless of whether it has already
been recorded -- disable journaling:

```csharp
[Migration(3000, journal: false)]
public class AlwaysRunSetup : Migration
{
    // This migration runs every time because it is not journaled
}
```

## Best Practices

- **Keep migrations focused** -- one logical change per migration.
- **Use descriptive class names** that explain the change (e.g.,
  `AddEmailIndexToUsers` rather than `Migration42`).
- **Always pass `CancellationToken`** through every async call.
- **Log important operations** so you can debug failures in production.
- **Test against both a fresh database and one with existing data** to catch
  ordering and state assumptions.
- **Prefer additive changes** (add columns, add indexes) over destructive ones
  (drop, rename). Destructive changes are harder to roll back safely.
