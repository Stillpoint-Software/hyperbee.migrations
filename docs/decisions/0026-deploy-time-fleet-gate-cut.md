# ADR-0026: Deploy-Time Fleet Gate Cut (Redundant with Wired Apply-Time Refusal)

**Status:** Accepted
**Date:** 2026-05-16
**Amends:** ADR-0019 (Squash via Replaces Graph) -- supersedes the
*deploy-time* fleet gate originally specified there (`EnsureDeployable`).
The *generation-time* gate (`EnsureGenerable`) is unaffected and remains in
force.
**Related ADRs:** ADR-0019, ADR-0020 (Squashes are Up-Only), ADR-0021
(Migration Record Checksum / Kind / Replaces), ADR-0024 (Migration Host
Discovery)

## Context

`SquashFleetGate` has two halves:

- **Generation-time** (`EnsureGenerable`): production-wired
  (`SquashVerb` -> `FleetReadinessProbe` -> `SquashFleetGate.EnsureGenerable`).
  Refuses to *create* a squash whose `Replaces` range would strand a fleet
  member. Core squash safety. **Stays.**
- **Deploy-time** (`EnsureDeployable` + `StaleFleetMemberException` +
  `UnregisteredEnvironmentException`): originally specified in ADR-0019 to
  run at apply time and refuse a squash on an environment that is
  unregistered in the fleet manifest, or stale beyond a staleness window.
  It has **no production caller** -- only `FleetGateTests` exercises it. The
  class doc claims a "runner deploy path" caller that does not exist.

The deploy-time gate was originally rated high-priority because the failure
it targeted -- an operator stands up a new environment, forgets to add it
to `fleet.yml`, the
squash is generated, the originals are deleted, and the forgotten
mid-range environment is then **silently** stranded -- is destructive,
irreversible-without-out-of-band-restore, and (at the time of the
assessment) undetected.

That failure, however, **is not silent in the shipped system.**
`MigrationRunner` squash reconciliation (the wired, production-active core
apply loop) raises `MidRangeSquashException` when an environment's ledger
covers only a strict subset of a squash's `Replaces` graph, with recovery
hints pointing at the `recover from-mid-range` verb. ADR-0021 Kind/Replaces
integrity (`MigrationLedgerIntegrityException`) backs this at write+read
time. The loud, recoverable apply-time refusal the deploy-time gate was
meant to provide is therefore **already delivered by a different, wired
mechanism**. `EnsureDeployable` is a redundant second mechanism for a case
the runtime already refuses loudly, and it was never wired precisely
because the primary mechanism already covered the load-bearing case (a
mid-range environment hitting the squash).

Recovery is also intact independent of this gate: the squashed originals
remain in git history; `recover from-mid-range` (ADR-0019) is the blessed
restore path. The scenario is loud *and* recoverable today without
`EnsureDeployable`.

Industry practice corroborates: no mainstream tool (Django
`squashmigrations`, Rails `schema.rb`, EF Core, Flyway baseline, Liquibase,
Sqitch, Prisma, Atlas) implements a mechanical deploy-time fleet-staleness
gate. The universal pattern is recoverability-from-history plus documented
operator responsibility for environments behind the floor. A bespoke
deploy-time staleness gate would make Hyperbee an outlier in mechanism
complexity for a problem the ecosystem solves with recoverability +
discipline.

## Decision

**Cut the deploy-time fleet gate.** Remove `EnsureDeployable`,
`StaleFleetMemberException`, and `UnregisteredEnvironmentException`, and
their `FleetGateTests` cases. This removes a misleading, never-wired safety
net -- it does **not** remove protection, because the loud apply-time
refusal the deploy-time gate sought is already provided by the wired
`MidRangeSquashException` reconciliation path + the `recover from-mid-range`
verb + ADR-0021 integrity checks.

Rationale:

1. **Redundant, not load-bearing.** The P0 outcome ("convert silent
   stranding into a loud, recoverable refusal") is already met by wired
   code. Keeping unwired code that claims to be that safety net is worse
   than removing it -- it implies a control that does not run.
2. **Recoverability is unaffected.** Originals live in git history;
   `recover from-mid-range` is the supported path; cutting this gate does
   not touch either.
3. **Industry-consistent.** Recoverability + documented operator fleet
   responsibility is the universal pattern; a deploy-time staleness gate is
   not.

Scope guard: `EnsureGenerable`, `FleetReadinessProbe`,
`MidRangeFleetException` (the generation-time exception), the
`MidRangeSquashException` apply-path, and all generation-time tests are
**not** touched.

Implementation:

1. Remove `SquashFleetGate.EnsureDeployable`,
   `StaleFleetMemberException`, `UnregisteredEnvironmentException`, and the
   deploy-time `FleetGateTests` cases (keep all `EnsureGenerable` tests).
2. Correct the doc surface that describes the deploy-time model as active:
   `MidRangeFleetException.cs` ("DEPLOY-TIME half" cross-reference),
   `SquashMetadata.cs` (the staleness-window + env-map remarks that exist
   only to feed `EnsureDeployable`). `SquashMetadata`'s data shape is kept
   where it still serves generation-time/reconciliation; only the remarks
   asserting a deploy-time enforcement path are corrected.
3. The operator documentation (`squashing-migrations.md`) states the fleet
   responsibility and the `MidRangeSquashException` -> `recover
   from-mid-range` recovery path explicitly.

## Consequences

- **Positive:** no unwired code masquerading as an active safety control;
  the fleet model documentation matches what the runtime actually enforces;
  smaller public exception surface; Hyperbee aligns with industry practice.
- **Negative:** loses a latent (never-active) second detection layer.
  Accepted: the wired `MidRangeSquashException` path already converts the
  dangerous case (mid-range environment) into a loud, recoverable refusal;
  the unregistered/below-minimum-but-not-mid-range cases are non-destructive
  (a below-range environment simply runs the squash body by design).
- **Reversible:** if a distinct deploy-time gate is ever justified beyond
  what `MidRangeSquashException` provides, it returns under a new ADR that
  must also specify fleet-manifest distribution + freshness -- the part the
  original deploy-time design never addressed.
