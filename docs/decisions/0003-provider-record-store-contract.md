# ADR-0003: Provider Record Store Contract

**Status:** Accepted
**Date:** 2026-04-03

## Context

The migration framework supports multiple database providers (Couchbase, MongoDB, Postgres, Aerospike). Each provider has fundamentally different storage mechanics, but the core `MigrationRunner` needs a consistent way to track migration state and manage concurrency.

## Decision

All providers implement a single `IMigrationRecordStore` interface with 5 operations:

- `InitializeAsync()` -- provider-specific setup (create tables, verify connectivity)
- `CreateLockAsync()` -- distributed lock returning `IDisposable`
- `ExistsAsync(recordId)` -- check if migration was previously applied
- `WriteAsync(recordId)` -- record successful migration
- `DeleteAsync(recordId)` -- remove record (for Down migrations)

## Rationale

- Single abstraction enables one `MigrationRunner` implementation for all providers
- Each provider optimizes its implementation using native features
- `IDisposable` lock pattern ensures cleanup even on failure
- Minimal surface area -- 5 methods cover the full lifecycle

## Consequences

- Providers must map their storage model to these 5 operations
- Advanced provider-specific features (Couchbase bucket management, Postgres schemas) live outside the record store in resource runners
- Lock semantics are provider-specific but expose the same contract
