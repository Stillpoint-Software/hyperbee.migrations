# ADR-0005: Provider-Native Distributed Locking

**Status:** Accepted
**Date:** 2026-04-03

## Context

Migration runners may execute concurrently in distributed environments (multiple pods, blue/green deployments). Concurrent execution must be prevented to avoid partial or duplicate migrations.

## Decision

Each provider implements distributed locking using its database's native primitives:

- **MongoDB:** Document with `LockedOn`/`ReleaseOn` timestamps; manual expiry check
- **Postgres:** Dedicated lock table row with `release_on` column; manual expiry check
- **Couchbase:** `RequestMutexAsync()` from Couchbase.Extensions.Locks with auto-renewal
- **Aerospike:** Record with `WritePolicy.expiration` (TTL); `CREATE_ONLY` prevents overwrites

All return `IDisposable` for cleanup. All have configurable `LockMaxLifetime` (default 1 hour).

## Rationale

- No external lock service (Redis, Zookeeper) required -- reduces operational complexity
- Each provider leverages its strongest concurrency primitive
- TTL/expiry fallback prevents deadlocks from crashed processes
- `IDisposable` ensures lock release even when migrations throw

## Consequences

- Lock behavior varies slightly per provider (Couchbase auto-renews, others expire on timeout)
- Clock skew can affect expiry-based locks (MongoDB, Postgres)
- Lock granularity is per-migration-set, not per-migration
