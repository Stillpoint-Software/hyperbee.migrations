---
layout: default
title: Advanced Topics
nav_order: 17
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
    // Lifecycle
    Task InitializeAsync(CancellationToken cancellationToken = default);
    Task<IDisposable> CreateLockAsync();

    // Per-record CRUD
    Task<bool>            ExistsAsync(string recordId);
    Task<MigrationRecord> ReadAsync(string recordId);
    Task                  DeleteAsync(string recordId);
    Task                  WriteAsync(string recordId);

    // Squash-aware write: persists Checksum + Kind + Replaces.
    // Default-interface-method delegates to the legacy WriteAsync(string)
    // for backward compatibility; shipped providers override.
    Task<WriteOutcome> WriteAsync(
        MigrationRecord record,
        WritePrecondition precondition = WritePrecondition.None,
        CancellationToken cancellationToken = default);

    // Bulk reads used during reconciliation.
    Task<IReadOnlySet<string>> IntersectWithAppliedAsync(
        IEnumerable<string> candidateIds,
        CancellationToken cancellationToken = default);

    Task<IReadOnlySet<long>> IntersectWithSquashedAsync(
        IEnumerable<long> versions,
        CancellationToken cancellationToken = default);
}
```

**Lifecycle**

- **InitializeAsync** -- create tables, collections, or sets needed for tracking.
- **CreateLockAsync** -- acquire a distributed lock; return an `IDisposable`
  that releases it. Provider-native locking (each provider uses its own
  store's primitives, not a shared lock service).

**Per-record CRUD**

- **ExistsAsync** -- realtime point-lookup; checks whether a migration record
  has already been applied. Used in the runner's discover loop.
- **ReadAsync** -- realtime read returning the full `MigrationRecord` (id,
  runOn, checksum, kind, replaces). Used for ledger inspection and integrity
  checks.
- **WriteAsync(string)** -- legacy v2 overload; persists only the record id.
  Kept for source compatibility with v2 record-store implementations.
- **DeleteAsync** -- remove a migration record (used during down migrations).

**Squash-aware write (v3)**

- **WriteAsync(MigrationRecord, ...)** -- the v3 write path. Persists the
  record id along with its `Checksum`, `Kind` (`Migration` / `Squash` /
  `Baseline`), and `Replaces` array. The optional `WritePrecondition`
  ensures concurrent runners don't double-write a row. Returns a
  `WriteOutcome` distinguishing `Created`, `AlreadyExistsBenign` (the row
  exists with matching content -- treated as no-op success), and
  `PreconditionFailed` (the row exists with a different checksum -- hard
  error).

  The default-interface-method implementation delegates to the legacy
  `WriteAsync(string)` so v2 record-store implementations compile
  unchanged. Shipped providers override with a single-round-trip persist
  that captures all three fields.

**Bulk reads (v3, squash reconciliation)**

- **IntersectWithAppliedAsync** -- given a candidate set of record ids,
  returns the subset already in the ledger. Single round trip per
  reconciliation pass. Default implementation falls back to a per-id
  `ExistsAsync` loop -- adequate but slow for large migration sets.
- **IntersectWithSquashedAsync** -- given a candidate set of migration
  versions, returns the subset transitively covered by some applied
  squash row's `Replaces` array. Default returns an empty set; custom
  implementations must walk the squash graph to support fresh-install
  reconciliation against squashed history.

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

| Option              | Aerospike | Couchbase | MongoDB | OpenSearch | PostgreSQL |
|---------------------|-----------|-----------|---------|------------|------------|
| LockName            | Yes       | Yes       | Yes     | Yes        | Yes        |
| LockMaxLifetime     | Yes       | Yes       | Yes     | Yes        | Yes        |
| LockExpireInterval  | No        | Yes       | No      | No         | No         |
| LockRenewInterval   | No        | Yes       | No      | Yes        | No         |
| LockStaleAfter      | No        | No        | No      | Yes        | No         |

Couchbase + OpenSearch support additional lock options because their lock
implementations use renewal loops to extend the lock during long-running
migrations. OpenSearch additionally exposes `LockStaleAfter` for forensic
recovery from crashed runners (it uses a split lock index).

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
