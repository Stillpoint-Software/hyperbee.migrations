---
layout: default
title: Advanced Topics
nav_order: 12
---

# Advanced Topics

## Writing a Custom Provider

To add support for a new database, implement the following:

1. **IMigrationRecordStore** -- 5 methods that manage migration state and locking.
2. **Provider-specific MigrationOptions** -- extend `MigrationOptions` to add
   connection and lock settings for your database.
3. **ServiceCollectionExtensions.AddXxxMigrations()** -- register the record store,
   options, runner, and resource runner with DI.
4. Optionally, a **ResourceRunner\<T\>** for resource-based migrations.

### IMigrationRecordStore Interface

```csharp
public interface IMigrationRecordStore
{
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IDisposable> CreateLockAsync();
    Task<bool> ExistsAsync(string recordId);
    Task DeleteAsync(string recordId);
    Task WriteAsync(string recordId);
}
```

- **InitializeAsync** -- create tables, collections, or sets needed for tracking.
- **CreateLockAsync** -- acquire a distributed lock; return an `IDisposable` that
  releases it.
- **ExistsAsync** -- check whether a migration record has already been applied.
- **WriteAsync** -- persist a migration record after successful execution.
- **DeleteAsync** -- remove a migration record (used during down migrations).

## Custom Conventions

`IMigrationConventions` controls how record IDs are generated for each migration.

- Default format: `Record.{version}.{normalized-class-name}`
- Override by implementing `IMigrationConventions` and assigning it to
  `options.Conventions` during registration.

## Custom Migration Activator

`IMigrationActivator` controls how migration instances are created.

- The default uses `ActivatorUtilities.CreateInstance` (standard DI).
- Override for custom instantiation logic, such as pulling migrations from a
  container scope or applying cross-cutting concerns.

## Retry Strategies

Two built-in retry strategies are available for polling operations:

- **BackoffRetryStrategy** -- exponential backoff with jitter. Default: 100ms
  initial delay, 120s maximum delay.
- **PauseRetryStrategy** -- fixed delay between retries. Default: 1s delay.

These are used by `WaitHelper` for polling operations such as waiting for
Aerospike secondary index readiness.

## Distributed Locking Details

Each provider implements locking at the database layer using native primitives:

- Locks have a maximum lifetime to prevent orphaned locks from blocking
  future runs.
- The lock is acquired in a `using` block and released automatically when
  disposed.
- If lock acquisition fails, a `MigrationLockUnavailableException` is thrown
  and the runner skips execution.

### Provider Lock Options

| Option | Aerospike | Couchbase | MongoDB | PostgreSQL |
|--------|-----------|-----------|---------|------------|
| LockName | Yes | Yes | Yes | Yes |
| LockMaxLifetime | Yes | Yes | Yes | Yes |
| LockExpireInterval | No | Yes | No | No |
| LockRenewInterval | No | Yes | No | No |

Couchbase supports additional lock options because its lock implementation
uses a renewal loop to extend the lock during long-running migrations.

## Error Handling

The library defines a hierarchy of exceptions for migration failures:

| Exception | Description |
|-----------|-------------|
| `MigrationException` | Base exception for all migration errors |
| `DuplicateMigrationException` | Two migrations share the same version number |
| `MigrationLockUnavailableException` | Distributed lock could not be acquired |
| `MigrationTimeoutException` | A resource operation exceeded its timeout |
| `RetryTimeoutException` | Polling via WaitHelper exceeded its timeout |

All exceptions derive from `MigrationException`, so a single catch block can
handle the full range of migration failures when fine-grained handling is not
needed.
