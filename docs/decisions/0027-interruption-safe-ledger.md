# ADR-0027: Interruption-Safe Ledger (marker-before-work)

**Status:** Accepted
**Date:** 2026-05-28
**Related ADRs:** ADR-0003 (Provider Record Store Contract), ADR-0009 (Convention-Based Record Ids), ADR-0019 (Replaces-Graph Mechanism), ADR-0021 (Migration Record Checksum), ADR-0028 (Transaction-Scoped Apply)

## Context

A migration's ledger row is written only *after* `UpAsync` completes successfully
(`MigrationRunner.ProcessJobAsync`). Every resource runner executes statements one
at a time, checking cancellation *between* statements, with no per-statement
checkpoint and no transaction spanning the statements. None of OpenSearch,
Couchbase, Aerospike, or MongoDB opens a transaction around a migration body;
Postgres gets an implicit transaction only per single command, not across the
resource files of one migration.

Consequence: if the process is interrupted mid-migration -- e.g. an orchestrator
(Argo Rollouts pre-init, a Kubernetes Job) sends SIGTERM when a timeout elapses,
then SIGKILL after the grace period -- no ledger row is written, so the next run
re-runs the *entire* migration from the first statement. Idempotent structural DDL
tolerates replay; non-idempotent data operations (Mongo `InsertOneAsync`, Couchbase
N1QL `UPDATE`, OpenSearch `REINDEX`/bulk) double-apply, corrupting data.

Surveyed tools converge on "one logical migration = one unit, journaled on success,"
and split partial-failure safety between transactions (where the engine has them)
and a fail-closed dirty marker (where it does not): Flyway writes a `success=false`
history row and refuses to proceed until `repair`; golang-migrate sets a `dirty`
flag and blocks. Hyperbee currently fails *open* (silent re-run) rather than
fail-closed, which is the gap.

An alternative -- writing the ledger row *on* cancellation (in a catch/finally) --
was rejected: the cancellation token is already tripped, SIGKILL can follow SIGTERM,
and the node can die. Cleanup-on-shutdown is best-effort and fails in exactly the
case it must cover. The durable signal must be written *before* the work, when the
process reliably has time.

## Decision

Adopt a two-tier model. This ADR specifies **Tier 1**, the universal safety net;
**Tier 2** (transaction-scoped apply, fail-clean) is ADR-0028.

### Tier 1 -- in-flight sentinel row

Before `UpAsync` runs, the runner writes a durable **sentinel** ledger row; after
the migration's real journal row is committed, the sentinel is deleted. On the next
startup, a sentinel whose real row is absent means that migration was interrupted.

- **Representation.** A *separate* ledger row at a deterministic derived id (a
  suffix on the migration's record id, mirroring the recovery-row id idiom of
  ADR-0019), with `Kind = MigrationRecordKind.InProgress` (a new additive enum
  value `= 4`; values are an on-disk contract and are never renumbered). The
  sentinel is a separate row -- not an `InProgress` overload of the migration's own
  record id -- so that `IntersectWithAppliedAsync` ("applied" = real row exists)
  stays correct with no change to any store's applied-set query.

- **Ordering.** Write sentinel -> run body -> write real journal row -> delete
  sentinel. A crash between the journal write and the sentinel delete leaves
  {real row + stale sentinel}; the next run sees the migration applied and reaps
  the stale sentinel unconditionally. This matches the recovery-row
  delete-after-journal ordering of ADR-0019.

- **Restart pre-scan.** After the start-of-run applied snapshot, the runner
  computes sentinel ids and issues a second `IntersectWithAppliedAsync` over them.
  This reuses the existing primitive: `IntersectWithAppliedAsync` is *kind-agnostic
  existence-by-id*, a contract this ADR pins (it must not be optimized to filter by
  `Kind`, or sentinel detection breaks). No new store query method is required.

- **Fail-closed scoping.** Whether a leftover sentinel halts the run depends on the
  migration's data-vs-structural intent (ADR-0019 A5 attributes):
  - `[StructuralOnly]` -> reap the sentinel and re-run. Replay of guarded DDL is a
    no-op. Reap is **not** a success guarantee: a structural op that is not yet
    idempotent (e.g. Mongo `CreateCollection` throws if the collection exists) will
    surface its own failure on replay; such ops are hardened separately.
  - `[DataMigration]` or unannotated (intent unknown, treated as unsafe) ->
    **fail closed**: throw `MigrationInterruptedException` unless `ForceResume` is
    set, in which case reap + WARN + continue. Mirrors the OpenSearch down-path
    `partially_rolled_back` + `ForceResume` lockout, promoted to the core runner.
  - Cron migrations re-run by design and upsert their record; they are exempt from
    fail-closed and always reap.

### Cross-cutting

- **`ForceResume` is promoted to `MigrationOptions`.** It previously existed only on
  `OpenSearchMigrationOptions` for the down-path lockout; the up-path lockout lives
  in the core runner, so the flag (default `false`) moves to the base options. The
  OpenSearch down-path behavior is unchanged.

- **Locking is a precondition.** The guarantee assumes `LockingEnabled` (default
  true) serializes runners so the pre-scan and reap are atomic against another
  runner. Under `LockingEnabled = false` the mechanism is no worse than today but
  the guarantee does not hold. A SIGKILL leaves the lock held; the next run relies
  on lock expiry before its pre-scan. A graceful SIGTERM releases the lock via the
  `RunAsync` finally.

- **Postgres ledger constraint.** The Postgres ledger table carries
  `CHECK (kind IN (0, 1, 2))`. Introducing `InProgress = 4` (and reconciling the
  already-defined `Recovery = 3`) requires widening this constraint via an
  idempotent `ALTER` on `InitializeAsync` for existing deployments -- not merely a
  new enum value. Schemaless stores (OpenSearch, Couchbase, Mongo, Aerospike) store
  the kind as a number and need no schema change.

- **Back-compat for custom stores.** A custom `IMigrationRecordStore` that
  implements only the legacy `WriteAsync(string)` still gets a working sentinel via
  the default-interface-method fallback (the sentinel id is written as a plain row;
  detection via existence). Fidelity is reduced (no `Kind` metadata) but the
  fail-closed behavior is correct.

### Scope

Up-direction only for v1. Down-direction interruption is the symmetric problem
(the journal row is deleted only after `DownAsync` succeeds) but down-runs are
operator-initiated and supervised; symmetric coverage is a tracked follow-up.

## Consequences

**Positive.** Silent double-apply of data migrations becomes an operator-visible
halt on every provider, including those with no transactions. Built entirely from
existing ledger primitives (derived row id, `Kind`, `WriteAsync(record, ...)`,
`DeleteAsync`, `IntersectWithAppliedAsync`); no new store query method; custom
stores keep working.

**Negative.** A halted `[DataMigration]` requires an operator to set `ForceResume`
after verifying state -- friction that is intentional and matches Flyway/golang-migrate.
For providers that cannot offer Tier 2 (OpenSearch, Aerospike, Couchbase DDL),
fail-closed plus idempotent authoring is the only safety; there is no atomic
rollback to lean on. Postgres requires a one-time ledger-constraint migration.

**Neutral.** The sentinel is a halt mechanism, not a repair: it does not make a
half-applied migration whole. Author idempotency (upsert-by-key, not blind insert)
and an orchestrator timeout greater than the migration's worst-case runtime remain
the primary safety; the sentinel is the backstop when those fail.
