---
layout: default
title: Concepts
nav_order: 2
---

# Concepts

This page covers the core concepts of the Hyperbee.Migrations framework. Understanding these
ideas will help you design, organize, and run database migrations effectively.

## What is a Migration?

A migration is a versioned C# class that describes a database change. Each migration inherits
from the `Migration` base class and implements an `UpAsync` method that performs the change.
An optional `DownAsync` method can undo the change if rollback support is needed.

Migrations are discovered automatically via reflection, ordered by version number, and
executed sequentially. Once a migration has been executed, it is recorded in a store and
will not run again on subsequent executions.

```csharp
[Migration(1)]
public class CreateUsersTable : Migration
{
    public override async Task UpAsync(CancellationToken cancellationToken = default)
    {
        // apply the change
    }

    public override async Task DownAsync(CancellationToken cancellationToken = default)
    {
        // undo the change (optional)
    }
}
```

## Migration Types

Hyperbee.Migrations supports two approaches for defining migrations.

### Code Migrations

Code migrations use C# directly. Database clients are injected through the constructor,
giving you full flexibility, type safety, and testability.

```csharp
[Migration(3000)]
public class SeedData : Migration
{
    private readonly NpgsqlDataSource _dataSource;

    public SeedData(NpgsqlDataSource dataSource)
    {
        _dataSource = dataSource;
    }

    public override async Task UpAsync(CancellationToken cancellationToken = default)
    {
        await using var connection = await _dataSource.OpenConnectionAsync(cancellationToken);
        await using var cmd = new NpgsqlCommand(
            "INSERT INTO users (name) VALUES (@name) ON CONFLICT DO NOTHING",
            connection);
        cmd.Parameters.AddWithValue("name", "Admin");
        await cmd.ExecuteNonQueryAsync(cancellationToken);
    }
}
```

### Resource Migrations

Resource migrations define changes as embedded resource files -- SQL, N1QL, AQL, or MongoDB
commands. This approach is declarative and reviewable. Statement changes do not require
recompilation. Each provider has a specific `ResourceRunner<T>` that processes the files.

```csharp
[Migration(1000)]
public class CreateInitialSchema(PostgresResourceRunner<CreateInitialSchema> resourceRunner)
    : Migration
{
    public override async Task UpAsync(CancellationToken cancellationToken = default)
    {
        await resourceRunner.AllSqlFromAsync(cancellationToken);
    }
}
```

The resource runner discovers embedded resource files by convention based on the migration
class name and executes them in order. See the provider-specific pages for details.

## Versioning

The `[Migration(version)]` attribute assigns a numeric version to each migration. Migrations
execute in ascending version order when running Up, and descending order when running Down.

Version numbers must be unique within a project. Duplicate version numbers cause a
`DuplicateMigrationException` at startup.

Common versioning conventions include:

- **Sequential** -- `1, 2, 3` -- simple, works well for small teams
- **Timestamp-based** -- `202401150001` -- avoids conflicts in larger teams

The `ToVersion` option on `MigrationOptions` stops execution at a specific version rather
than running all available migrations:

```csharp
options.ToVersion = 2000; // stop after version 2000
```

## Direction (Up and Down)

Every migration run has a direction:

- `Direction.Up` -- executes `UpAsync` and records the migration as completed
- `Direction.Down` -- executes `DownAsync` and removes the migration record

Down migrations are optional. Only implement `DownAsync` if you need rollback capability.
Configure the direction through options:

```csharp
options.Direction = Direction.Down;
```

When running Down, migrations execute in descending version order, undoing changes from
newest to oldest.

## Idempotency

The framework is inherently idempotent. The record store tracks which migrations have
completed, and previously executed migrations are skipped on subsequent runs.

As a best practice, make individual migration code idempotent as well. This protects
against partial failures where a migration executes partway through before encountering
an error. Use patterns like:

- `IF NOT EXISTS` for DDL statements
- `ON CONFLICT DO NOTHING` for inserts
- `CREATE INDEX IF NOT EXISTS` for indexes

## Journaling

By default, every migration is journaled -- meaning it is recorded in the record store
after execution. You can disable journaling for a specific migration by setting the
`journal` parameter to `false`:

```csharp
[Migration(1000, journal: false)]
```

Non-journaled migrations run every time the runner executes. This is useful for
idempotent setup tasks that should always run, such as ensuring a baseline configuration
exists.

The `journal` parameter is the fourth positional parameter on the attribute:

```csharp
[Migration(version, startMethod, stopMethod, journal)]
```

## Profiles

Profiles let you scope migrations to specific environments. A migration with a profile
only runs when that profile is active.

```csharp
// Only runs when the "development" profile is active
[Migration(1000, "development")]

// Runs in staging OR production
[Migration(1000, "staging", "production")]
```

Migrations with no profile always run, regardless of which profiles are active.

Activate profiles through options:

```csharp
options.Profiles.Add("development");
```

Profile matching is case-insensitive.

## Distributed Locking

When multiple application instances start simultaneously, distributed locking prevents
concurrent migration execution. Enable it through options:

```csharp
options.LockingEnabled = true;
```

Each provider implements locking at the database level using its native mechanisms. When
a lock is already held, other runners log a warning and skip execution.

Locks have a configurable maximum lifetime (defaulting to one hour) to prevent orphaned
locks from blocking future runs. Lock configuration varies by provider -- see the
provider-specific pages for details.

## Dependency Injection

Migrations support constructor injection via the .NET DI container. You can inject
database clients, loggers, configuration, or any registered service.

```csharp
[Migration(3000)]
public class SeedData : Migration
{
    private readonly IMongoClient _client;
    private readonly ILogger<SeedData> _logger;

    public SeedData(IMongoClient client, ILogger<SeedData> logger)
    {
        _client = client;
        _logger = logger;
    }

    public override async Task UpAsync(CancellationToken cancellationToken = default)
    {
        // use _client and _logger
    }
}
```

Resource runners are also injected through DI:

```csharp
public class CreateSchema(PostgresResourceRunner<CreateSchema> runner) : Migration
```

The framework uses `ActivatorUtilities.CreateInstance` to resolve migration dependencies.

## Record Store

Each provider implements the `IMigrationRecordStore` interface to track migration state.
The interface defines five operations:

- `InitializeAsync` -- creates the backing store (table, collection, or set) if needed
- `CreateLockAsync` -- acquires a distributed lock
- `ExistsAsync` -- checks whether a migration has been recorded
- `WriteAsync` -- records a completed migration
- `DeleteAsync` -- removes a migration record (used during Down)

The store creates its own schema automatically on first use. Record IDs follow the
convention `Record.{version}.{normalized-class-name}`.

## Migration Attribute Reference

The `MigrationAttribute` supports several constructor overloads:

```csharp
// Simple -- version only
[Migration(1000)]

// With profiles
[Migration(1000, "development", "staging")]

// With lifecycle methods (start/stop cron expressions)
[Migration(1000, "StartMethod", "StopMethod")]

// With lifecycle methods and journaling disabled
[Migration(1000, "StartMethod", "StopMethod", false)]

// Full: lifecycle, journaling, and profiles
[Migration(1000, "StartMethod", "StopMethod", true, "production")]
```

| Parameter     | Type       | Default | Description                                      |
|---------------|------------|---------|--------------------------------------------------|
| `version`     | `long`     | --      | Unique version number (required)                 |
| `startMethod` | `string`   | `null`  | Cron expression or method for start scheduling   |
| `stopMethod`  | `string`   | `null`  | Cron expression or method for stop scheduling    |
| `journal`     | `bool`     | `true`  | Whether to record the migration after execution  |
| `profiles`    | `string[]` | `[]`    | Profiles that activate this migration            |
