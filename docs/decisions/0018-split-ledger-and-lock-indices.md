# ADR-0018: OpenSearch Provider Splits Ledger and Lock into Two Indices

**Status:** Accepted
**Date:** 2026-05-04

## Context

Hyperbee's NoSQL provider family — Aerospike, Couchbase, MongoDB, Postgres — co-locates the migration ledger and the run-lock in a single namespace, distinguished by document id. A reviewer comparing the OpenSearch provider against a sibling internal implementation flagged that the OpenSearch provider deviates from this convention: it ships with two indices, `.migrations` (ledger) and `.migrations-lock` (lock), defaulted by `OpenSearchMigrationOptions.LedgerIndex` and `LockIndex` respectively.

The deviation is intentional but, until now, undocumented as an ADR. The reviewer's observation is correct in two ways:

1. **The convention exists.** The other four providers co-locate; the OpenSearch provider is the outlier.
2. **The deviation is load-bearing.** It exists to serve a specific concurrency invariant that the other providers don't face in the same shape.

The risk of leaving this implicit is that a future provider author copies the wrong convention — either co-locating in OpenSearch when they shouldn't, or splitting in a future provider when they shouldn't. This ADR captures the reasoning so the next implementer makes the choice deliberately.

## The deviation

OpenSearch's primary-shard write contract is shard-replica coupling: a primary write blocks until each in-sync replica acknowledges the write. Under N-runner concurrent lock acquire (R-24b), the lock primary shard is contended; replica-write coupling adds a second source of tail latency on top of the contention itself.

The mitigation (PA-2 from assessment 0002, encoded in `LockIndexInitStep`) is to create the lock index with `number_of_replicas: 0`. The lock document then writes to a single primary with no replica fan-out — eliminating replica-write coupling as a tail-latency contributor under contention.

The ledger index has the opposite needs: it's a forensic record (R-06) used after the fact to answer "what migrations ran, when, in what direction, against what state." Durability matters; tail latency under concurrent writes does not (the lock serializes writes to the ledger). The ledger gets the cluster's normal replica configuration.

Two distinct durability/latency profiles, two indices. The other providers don't face this trade-off because:

- **Aerospike** uses native CAS on a record key in a configured namespace; durability is a namespace-level setting and is not coupled to replica-write semantics on a per-record basis.
- **Couchbase** uses bucket-level durability; the lock is a single document with provider-level coordination.
- **MongoDB** uses a collection-level write concern; the lock is a single document with `findOneAndUpdate` semantics.
- **Postgres** uses `pg_advisory_lock`; the lock is not a row in the ledger table at all.

In each case, the lock's durability story is decoupled from the ledger's, either by language (advisory lock vs row) or by configuration knob (namespace, bucket, collection write concern). OpenSearch couples them through index settings — which means decoupling the two requires two indices.

## Decision

The OpenSearch provider will continue to ship two indices:

- `LedgerIndex` (default `.migrations`) — strict-mapped ledger per R-06, with the cluster's normal replica configuration.
- `LockIndex` (default `.migrations-lock`) — `number_of_replicas: 0` per PA-2 mitigation, asserted by `LockIndexInitStep`.

We will not introduce an option to combine them into a single index. The combined-index shape would either lose the PA-2 mitigation (if the index were configured for ledger-grade durability) or compromise the ledger's durability (if the index were configured for `replicas: 0`). Neither trade-off is worth the cross-provider symmetry.

If a future operator deployment is so IAM-restricted that index creation is gated to a single index, we will reconsider — but only as a documented constrained-mode opt-in, never as a default. ADR-0013's `AssumeIndicesExist` already covers the IAM-restricted case for both indices; no additional surface is needed today.

## Consequences

**Easier:**

- The lock's tail-latency story is clean: under R-24b N-runner contention, the lock primary shard's write path has no replica-coupling component.
- The ledger's durability story is clean: it inherits the cluster's normal replica configuration without per-index special-casing.
- Operators in non-AWS environments who configure cluster-wide replica counts get exactly what they expect for both indices.

**Harder:**

- Operators must monitor / back up two indices. In practice this is one extra entry in any backup or alerting tool; the entries are co-located by name (`.migrations*` glob covers both).
- Cross-provider documentation has to surface the asymmetry. This ADR is the canonical reference; the provider README's "Quick start" continues to default both indices for the common case.
- The next provider author asking "should I co-locate or split?" must read this ADR. The default answer is co-locate (the house style); split only when the lock and ledger have distinct durability or latency requirements that the underlying engine couples through shared configuration.

**Constrains:**

- The lock and ledger indices are part of the public contract of `OpenSearchMigrationOptions`. Removing either as a top-level index requires a superseding ADR.
- The PA-2 invariant (`number_of_replicas: 0` on the lock index) is asserted at startup by `LockIndexInitStep`; weakening this assertion requires a superseding ADR.

## Relation to other ADRs

- **ADR-0013 (Always-Create Lock and Ledger Indices in InitializeAsync with Explicit Override)** — this ADR refines the model that one introduced. ADR-0013 names the two indices and the always-create behavior; this ADR captures *why there are two*.
- **ADR-0005 (Provider-Native Distributed Locking)** — preserved. The split is an OpenSearch-specific implementation choice for native locking; the cross-provider lock contract is unchanged.

## Implementation

- `OpenSearchMigrationOptions.LedgerIndex` (default `.migrations`) and `OpenSearchMigrationOptions.LockIndex` (default `.migrations-lock`).
- `LedgerIndexInitStep` creates the ledger with strict R-06 mapping; replica configuration follows cluster default.
- `LockIndexInitStep` creates the lock with `number_of_replicas: 0` and asserts the value when the index already exists. Mismatch fails at startup with a remediation message.
- `OpenSearchRecordStore` reads the ledger and the lock through these options. There is no path that writes lock state to the ledger index (or vice versa).
