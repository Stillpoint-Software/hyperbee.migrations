# ADR-0028: Transaction-Scoped Migration Apply (where supported)

**Status:** Proposed
**Date:** 2026-05-28
**Related ADRs:** ADR-0003 (Provider Record Store Contract), ADR-0023 (Multi-Runner, Not Meta-Runner), ADR-0027 (Interruption-Safe Ledger)

## Context

ADR-0027 (Tier 1) makes an interrupted migration *fail-closed*: a durable sentinel
halts the next run for non-idempotent data migrations until an operator sets
`ForceResume`. That is correct and universal, but for an automated rollout (Argo
pre-init) a halt-and-wait-for-human defeats the automation it is embedded in.

Where the datastore supports a transaction spanning the migration body *and* its
ledger write, a strictly better outcome is available: an interruption rolls back
both atomically, leaving a pristine ledger, so the restart is *fail-clean* with no
operator step. The sentinel becomes unnecessary in that case -- the transaction is
the mechanism.

Transaction capability is not uniform:

| Provider   | Body transaction                                              | Verdict |
|------------|--------------------------------------------------------------|---------|
| Postgres   | Transactional DDL+DML, except `CREATE INDEX CONCURRENTLY`, `VACUUM`, etc. | Reference implementation. |
| MongoDB    | Multi-document transactions only on replica sets; 16MB / `transactionLifetimeLimitSeconds` (default 60s) limits | Opt-in, small migrations only. Follow-up. |
| Couchbase  | KV/N1QL transactions exist; DDL (bucket/scope/collection/index) is not transactional | DML-only at best. Follow-up. |
| Aerospike  | No multi-record transactions in this client usage           | Tier 1 only. |
| OpenSearch | No transactions                                              | Tier 1 only. |

Tier 2 is therefore an opt-in capability a provider implements *if it can*, not an
all-provider feature. The hard part is the seam: the resource runner and the
`IMigrationRecordStore` use independent connections today (e.g. Postgres runner uses
pooled-per-command `NpgsqlDataSource.CreateCommand`; the store opens its own
connection). A shared transaction requires both to enroll in one
connection/transaction, which may be a non-trivial refactor of the runner/store
contracts.

## Decision

1. **Spike-gate the seam.** Before committing to Tier 2, a time-boxed spike proves
   whether the Postgres runner and `PostgresRecordStore` can share one
   `NpgsqlConnection` + `NpgsqlTransaction` without a disruptive contract refactor.
   The spike returns go/no-go plus a cost estimate. If no-go, Tier 1 (ADR-0027)
   remains the answer and this ADR is superseded with the rationale recorded.

2. **On go, define a capability seam** (e.g. `IMigrationTransactionScope`):
   begin -> expose an ambient connection/transaction -> commit/rollback, orchestrated
   by the runner around the migration body. The resource runner obtains the ambient
   transaction through this seam; the journal write enrolls in the same transaction.
   Commit on `UpAsync` success; rollback on any exception including
   `OperationCanceledException`.

3. **Reference implementation: Postgres.** Body + journal commit atomically.
   Operations that cannot run in a transaction (`CREATE INDEX CONCURRENTLY`, etc.)
   opt out and fall back to Tier 1.

4. **Under Tier 2, write no sentinel.** The transaction is the safety mechanism.
   A sentinel written *outside* the transaction would survive a clean body-rollback
   and wrongly fail-close (worst of both worlds); a sentinel written *inside* just to
   be rolled back is pointless ceremony. The ADR-0027 pre-scan invariant ("no
   sentinel + no real row = clean") already classifies a rolled-back Tier-2
   migration correctly.

5. **MongoDB and Couchbase Tier 2 are follow-ups**, gated on demonstrated need
   (a real data migration that requires fail-clean and fits the provider's
   transaction limits).

## Consequences

**Positive.** For transactional providers, interruption is fully automatic and
clean -- the ideal outcome for unattended orchestrated runs. No marker, no operator
step.

**Negative.** Requires a connection/transaction seam that crosses the runner/store
boundary -- the reason it is spike-gated rather than assumed feasible. Tier 2 is
inherently a capability subset; the asymmetry across providers must be documented so
operators know which providers are fail-clean and which are fail-closed.

**Neutral.** Tier 2 is an optimization layered on Tier 1, not a replacement. A
provider with no Tier 2, or an operation that opts out, is exactly as safe as
ADR-0027 makes it -- just fail-closed rather than fail-clean.
