# ADR-0025: NullSquashStrategy Retained as a Public Extension Point

**Status:** Accepted
**Date:** 2026-05-16
**Related ADRs:** ADR-0019 (Squash via Replaces Graph), ADR-0024 (Migration Host Discovery)

## Context

`NullSquashStrategy` (`src/Hyperbee.Migrations/Squash/NullSquashStrategy.cs`) is an `ISquashStrategy` whose `GenerateAsync` always returns `Failed` with a "this provider has no squash codegen yet" message. It was introduced when the v1 plan expected Postgres to be the only provider shipping real squash codegen, with the other four providers shipping `NullSquashStrategy` as a placeholder until their codegen landed.

The all-5-providers-or-nothing release rule changed that: every provider now ships a real strategy (`PgDumpSnapshotStrategy`, `HybridStrategy`, `InfoSnapshotStrategy`, `IntrospectionSnapshotStrategy`, `RestStateDiffStrategy`). As a result `NullSquashStrategy` has **no production consumer** — it is referenced only by:

- XML-doc `<see cref>` links in `ISquashStrategy.cs`, `SquashGenerationResult.cs`, `SquashStrategyDescriptor.cs`
- its contract test `SquashStrategyContractTests.cs`

The remark at `ISquashStrategy.cs:12-14` still claims four providers ship `NullSquashStrategy` and "calls return Failed" — which directly contradicts shipped reality and will mislead a reader into thinking four providers are squash no-ops.

The question this ADR settles: delete `NullSquashStrategy` as dead code, or consciously retain it as a public extension point.

## Decision

**Retain `NullSquashStrategy` as a documented public SDK extension point. Do not delete it.**

Rationale: the type is the correct, intention-revealing return for any future or third-party provider that registers a `SquashStrategyDescriptor` before its codegen exists. ADR-0024 makes the provider surface open to third-party providers discovered via reference closure; a third-party provider mid-build needs exactly this "registered but not yet generating" strategy so the CLI fails loudly with a clear roadmap message instead of throwing a null/NotImplemented. Deleting it would remove a small, stable, already-tested affordance that the open provider model legitimately wants — for no maintenance saving (it is ~1 type with no dependencies and full contract-test coverage).

Required corrections (documentation only, no deletion):

1. Fix the stale remark in `ISquashStrategy.cs` so it no longer claims any first-party provider ships `NullSquashStrategy`.
2. Add an explicit note on `NullSquashStrategy` and at the `ISquashStrategy`/`SquashStrategyDescriptor` cref sites that it is an **extension point for providers without codegen yet**, and that **all five first-party providers ship real strategies** (none use it).
3. Reconcile `SquashGenerationResult.cs:9` similarly ("providers that lack v1 codegen" -> "providers that have not yet implemented codegen, e.g. future/third-party providers").

The contract test stays as-is — it is the guarantee that the extension point keeps its documented fail-loud behavior.

## Consequences

- **Positive:** the open provider model (ADR-0024) keeps a clean, tested "not yet" strategy; no churn to `SquashStrategyDescriptor` composition; documentation stops lying about first-party providers.
- **Negative:** one public type remains that no first-party code exercises; mitigated by the contract test and the corrected docs that state its purpose explicitly.
- **Neutral:** no behavior change. This ADR authorizes documentation fixes only; the type and its test are unchanged.
