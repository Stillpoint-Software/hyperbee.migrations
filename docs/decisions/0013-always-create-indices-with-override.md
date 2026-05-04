# ADR-0013: Always-Create Lock and Ledger Indices in InitializeAsync with Explicit Override

**Status:** Accepted
**Date:** 2026-05-02

## Context

The OpenSearch provider's lock document (R-04) and migration ledger (R-06) must exist before `MigrationRunner.RunAsync` can do meaningful work. Three init strategies were considered during `/nop:propose`:

1. **Always-create in `InitializeAsync`** (Approach A and C in propose) — provider performs idempotent `PUT` operations on both indices at startup; consistent with how Couchbase/Aerospike/MongoDB providers handle similar setup.

2. **Provision-on-demand** (Approach B in propose) — lock index created on first `CreateLockAsync`, ledger created on first `WriteAsync`. `InitializeAsync` is light. Defers cluster errors until first use.

3. **Explicit-only** — operator must call a separate `EnsureIndicesAsync()` or set up indices via deployment automation. Provider treats indices as preconditions.

The forces in tension:

- **Concurrent runner race window** — provision-on-demand introduces a race during the very first concurrent acquire attempt (the laziest CI matrix run is the worst case for race exposure; assessment 0002 R-24b lock contention test explicitly exercises this).
- **AWS Managed OpenSearch IAM scoping** — production deployments may use IAM policies that grant migration runners read/write but deny `indices:admin/create`. Always-create breaks for these consumers.
- **House-style consistency** — Couchbase/Aerospike/MongoDB always-create. Diverging here costs operator muscle memory.
- **Bootstrap simplicity** — light `InitializeAsync` is easier to reason about than one that does multiple cluster mutations.

Approach B's provision-on-demand was eliminated in propose because it introduces a race window in concurrent CI runs and defers errors that should fail at deploy-time, not first-acquire-time. Explicit-only was not seriously considered because it diverges from house style without compensating benefit.

## Decision

We will always create the lock and ledger indices in `InitializeAsync` with idempotent semantics:

- `PUT /<lockIndex>` with `IF NOT EXISTS` behavior; assert `number_of_replicas: 0` to eliminate replica-write coupling on the lock primary shard (PA-2 mitigation, requirement R-04)
- `PUT /<ledgerIndex>` with `IF NOT EXISTS` behavior and the strict mapping defined in R-06 (including `appliedBy`, `direction`, `failedStatementIndex` forensic fields)

For consumers in tightly-scoped IAM contexts where the migration runner cannot create indices, we will provide an explicit opt-out: `OpenSearchMigrationOptions.AssumeIndicesExist` (default `false`). When `true`:

- `InitializeAsync` skips creation
- `InitializeAsync` verifies both indices exist via `HEAD /<index>` and validates the mapping shape via `GET /<index>/_mapping`
- Missing indices fail at startup with a remediation message naming the indices and the expected mapping
- Mapping mismatches fail at startup with a diff summary

## Consequences

**Easier:**
- Zero-race-window for lock acquisition; concurrent CI matrix runs converge on a single created index
- Consistent with house-style provider initialization; operators in cross-provider deployments don't context-switch
- Cluster errors (network, auth, missing permission) surface at deploy-time, not first-acquire-time
- Backup/restore of the cluster automatically covers migration state (no out-of-band ledger setup)

**Harder:**
- Bootstrap path must handle `index_already_exists` (409) cleanly as success — easy in code, easy to test
- Verification under `AssumeIndicesExist=true` requires a parallel mapping-shape check that is non-trivial; this code path is exercised in integration tests but is the lowest-traffic branch
- Operators in IAM-scoped contexts must explicitly opt out; documentation must surface this as a first-class scenario in the runner project's README
- Always-create wastes a small amount of cluster work on every deploy where indices already exist — measurable but not significant against R-07's `?refresh=wait_for` cost (R-24c measures both)

**Constrains:**
- Future schema changes to the lock or ledger indices cannot rely on auto-migration — they must be explicit migration steps because R-06's strict mapping is **immutable** per the Forbidden trust boundary. Adding fields after v1 release means a ledger reindex via `MIGRATE INDEX` (R-30)
- The `AssumeIndicesExist` option is part of the public contract; once set, deprecating it requires a superseding ADR
- Any future "ephemeral migration runner" mode (e.g., dry-run) must explicitly state its index-handling behavior
