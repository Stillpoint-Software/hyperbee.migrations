# ADR-0020: Migration Squashes are Up-Only

**Status:** Proposed
**Date:** 2026-05-04
**Related design:** [docs/design/migration-squashing.md](../design/migration-squashing.md)
**Related ADRs:** ADR-0019 (Replaces-Graph Mechanism)

## Context

Hyperbee.Migrations supports `DownAsync` as a first-class concept on `Migration`. The OpenSearch provider has the richest rollback infrastructure (R-19 partial-rollback ledger state, statement-level inverse operations via `RollbackStatementsFromAsync`, `OpenSearchPartialRollbackException`). Other providers leave `DownAsync` virtual and implementations vary.

Squashes (per ADR-0019) collapse N migrations into one. The natural question: what is `DownAsync` for the squash?

The general answer has no clean form. Composing N inverses into a single inverse is not algebraically expressible without knowing the data state at each intermediate point — and even where the schema-only inverse is computable, the data state after the original sequence's `UpAsync` is not reconstructible from after-images alone. Pretending a squash has a Down that the original chain's Down "sort of" approximates is an integrity hazard: a `DownAsync` on the squash that does only schema-level reversal can leave data in an inconsistent state that the original chain's Down would have cleaned up.

Industry practice across surveyed tools is unanimous on this point:
- Django's squashed migrations have no Down; you migrate forward through the squash on fresh installs and never back across the squash boundary.
- Flyway's baseline (`B__`) scripts cannot be rolled back.
- Prisma's squash documentation says nothing about Down.
- EF Core, Atlas, Knex, Liquibase: same — squash/baseline output is forward-only.

## Decision

**Squash migrations are Up-only.** A squash migration's `DownAsync` throws `RollbackNotSupportedException` with a message naming the expected recovery path: backup restore.

A squash migration is identified by having non-empty `Replaces` on its `[Migration]` attribute. The runner's `Down` path, on encountering a squash migration's row in the ledger:

1. Refuses to invoke its `DownAsync`.
2. Surfaces `RollbackNotSupportedException` with detail naming the squash version, its `Replaces` set, and the recovery options:
   - Restore the database from a backup taken before the squash was first applied to that environment.
   - For environments that auto-marked the squash (i.e., the squash body never ran), the operator can manually delete the squash ledger row and the original ledger rows are still present — but this is an explicit operator decision, not an automatic path.
3. If `OpenSearchMigrationOptions.ForceResume` (or its analogue) is true and the operator has explicitly opted into "I accept that the squash is Up-only and rollback is forbidden," the runner refuses anyway. There is no opt-out flag for "rollback the squash" — only opt-in flags for advanced recovery paths.

**Authors who attempt to override `DownAsync` on a squash migration trigger a load-time validation error.** A migration class that declares non-empty `Replaces` and overrides `DownAsync` with a non-trivial body (anything other than `return Task.CompletedTask`) is a hyperbee-level error caught during reflection discovery — the framework will not load it.

**Squash *generators* (OpenSearch AST fusion, future provider strategies) refuse to compose source migrations whose `DownAsync` overrides are non-trivial without an explicit author opt-out flag** named to make the consequences clear: e.g., `--accept-squash-up-only` (or stronger). With the opt-out, the generator emits a squash whose `DownAsync` is the no-op that throws — but the source migrations' Down implementations remain in the source tree (so partial-catch-up environments can still execute Down on the originals individually).

## Consequences

**Positive:**
- Eliminates an entire class of integrity hazards by refusing to make a guarantee that has no general implementation.
- Matches industry practice unanimously; operators familiar with Django, Flyway, or Prisma will not be surprised.
- The recovery path (backup restore) is honest about what's actually required; pretending otherwise creates false confidence.
- Generators have a clear contract: they don't try to invent inverse operations.

**Negative:**
- Teams that rely on `Down` for routine rollback (rather than backup restore) lose that path across squash boundaries. The mitigation is "don't squash migration ranges that you actively roll back through" — squashes are for stable history, not active development.
- The squash author must explicitly opt out when source migrations have Down implementations. Some authors will find the heavy-language flag friction; the friction is the design's intent.

**Neutral:**
- The original migrations' `DownAsync` implementations remain in the source tree during the deprecation window (per ADR-0019). Down on individual originals still works; Down across the squash boundary does not.
- The framework provides no API for "compose Downs into an inverse squash." Authors who need this must hand-author both the squash *and* a separate explicit-recovery migration that runs as Up.

## Alternatives Considered

- **Auto-compose `DownAsync` from source migrations' Downs in reverse order** — rejected. Schema reversal is sometimes computable; data reversal almost never is. Producing a Down that is *partially* correct is worse than refusing.
- **Allow author to write a custom `DownAsync` on the squash** — rejected. The author has no more information than the framework about how to compose N inverses; encouraging this creates a false sense of safety. Authors who need custom recovery should write a separate explicit `Migration` with its own `Down` that is documented as the recovery path.
- **Make Up-only configurable per project** — rejected. The hazard is the same regardless of project; configuration here just buries the issue.
- **Ship squashes without taking a position on Down** — rejected. The position has to be explicit somewhere; better in the framework than in every consumer's docs.

## References

- Research: [docs/research/0005-migration-squashing.md](../research/0005-migration-squashing.md), Finding 8
- Requirements: [docs/requirements/migration-squashing.md](../requirements/migration-squashing.md), R-07
- Design: [docs/design/migration-squashing.md](../design/migration-squashing.md), Decision 3
- Related: [`OpenSearchExceptions.cs`](../../src/Hyperbee.Migrations.Providers.OpenSearch/OpenSearchExceptions.cs) — existing `RollbackNotSupportedException` infrastructure to reuse
