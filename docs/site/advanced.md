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

### Two rules for the ledger's wire contract

Both rules below come from real defects in the shipped providers. They cost a
broken release and a silently wrong query, so they are worth following in a
custom store.

**Address the ledger explicitly. Never let the client infer it.** Modern clients
resolve a great deal from configuration the consumer owns: which index or
collection a CLR type maps to, what casing a property serializes as, which
serializer runs. Your record store's document type is not part of the consumer's
domain, so requiring them to declare a mapping for it inverts ownership -- and
their configuration can change under you. Take the target from your own options
object and set it on every request, at every level the protocol carries it.

The OpenSearch `_mget` body carries a per-entry index in addition to the one in
the URL. Setting only the URL one left the body to CLR-type inference, which no
shipped client factory configured, so every run failed during serialization.

**Route every field reference through the same path as the writer.** Where a
query names your document's own fields, build all of it the same way. Mixing a
typed expression with a raw string is the trap: the typed half goes through the
serializer's member map, the raw half does not, and the two disagree the moment
any naming convention is in play.

```csharp
// wrong -- "Kind" renders through the class map, "replaces" renders verbatim
Filter.And( Filter.Eq( x => x.Kind, Squash ), Filter.In( "replaces", versions ) );

// right -- both terms route through the class map
Filter.And( Filter.Eq( x => x.Kind, Squash ), Filter.AnyIn( x => x.Replaces, versions ) );
```

Where the query language has no typed field reference at all -- N1QL, SQL --
pin the serializer your store uses instead, so the names you hard-code are a
guarantee rather than an inherited default.

Note what the fix is *not*. Pinning element names with serialization attributes
or a registered class map repairs self-consistency, but it also stops reading
ledgers written under a different convention. Routing everything through one
path is correct under any configuration, and changes no bytes.

**Test the wire, not the mock.** A substituted client never serializes, so it
cannot catch either defect. Drive the real client and real serializer over a
faked transport instead -- an in-memory connection, or comparing a rendered
query against the serializer's actual output. Both bugs above are visible that
way in milliseconds, with no container.

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
