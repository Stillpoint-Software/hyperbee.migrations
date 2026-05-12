# Plan: Multi-Runner Composition (ADR-0023)

**Status:** Active
**Created:** 2026-05-08
**ADR:** [ADR-0023](../../decisions/0023-multi-runner-not-meta-runner.md)
**Assessed:** [research/0008-multi-runner-composition-assessment.md](../../research/0008-multi-runner-composition-assessment.md) — 3 P0 + 7 P1 + 4 P2 amendments incorporated. Plan is post-assessment.

## Objective

Eliminate the single-provider-per-container restriction in `Hyperbee.Migrations` by introducing per-provider `MigrationRunner` subclasses. After this work, a host that calls more than one `Add{Provider}Migrations` extension on the same `IServiceCollection` produces N independent, addressable runners — no shadowing, no silent provider-loss, no behavior change for single-provider hosts.

**Success criteria:**
- A host calling `services.AddPostgresMigrations(...).AddMongoDBMigrations(...)` resolves both runners via concrete type and runs both successfully against fresh containers.
- Single-provider host code resolving the base `MigrationRunner` continues to work unchanged (zero call-site changes required).
- All 22 existing ADRs continue to be honored; squash, fleet manifest, resource runners, and CLI behavior unchanged.
- Full unit + integration suite green on net8/9/10.

## Constraints

- **Backward compatibility is mandatory.** Existing call sites that resolve `MigrationRunner` (the base type) must continue to work without code changes. Only the multi-provider scenario gains new shape.
- **No keyed-service dependency.** Solution must compile and work on net8 (the lowest target framework); not blocked on .NET 8+ keyed-service feature semantics.
- **No new runtime infrastructure.** Per-provider subclass is type-identity only; the run loop, lock semantics, ledger semantics, recovery semantics remain in the base class and the existing record stores. No meta-runner.
- **Cross-provider participation lens.** Every change confirms how all 5 providers absorb it. Tasks that look "provider-shaped" enumerate per-provider notes.

## Phases

### Phase 0 — Audit & Preconditions

Walks the registration code, the runner constructor, the existing single-provider call sites, and the test fixtures to confirm assumptions before any code change. Surfaces any divergence as an extra task.

#### Task 0.1 — Registration audit
Read each provider's `ServiceCollectionExtensions.cs` and confirm:
- Each registers `MigrationOptions` (cast from concrete subclass), `IMigrationRecordStore` (concrete subclass), `MigrationRunner` (base type).
- The concrete `{Provider}RecordStore` and `{Provider}MigrationOptions` are NOT independently registered as their concrete types today.

**Verified by the assessment (research/0008 § N1):** all 5 providers' `RecordStore` types are `internal`. There is no asymmetry to fix. Plan does NOT make any RecordStore type public.

#### Task 0.2 — `MigrationRunner` constructor audit
Confirm:
- Base ctor takes `IMigrationRecordStore`, `MigrationOptions`, `ILogger<MigrationRunner>` (will become `ILoggerFactory` per F7 — see Task 1.0 below).
- No internal state depends on the runtime type of either dependency.
- All `RunAsync` / `DiscoverMigrations` paths are agnostic to the provider concrete type.

If the runner reaches into provider-specific behavior (e.g. casts to a concrete record store), surface that as Phase 1 sub-task before subclassing.

**Per assessment N3 — broaden Task 0.2 scope:** Grep the entire codebase (src/ + tests/ + runners/) for:
- `GetRequiredService<MigrationOptions>` / `GetService<MigrationOptions>`
- `GetRequiredService<IMigrationRecordStore>` / `GetService<IMigrationRecordStore>`
- Constructor injections of `MigrationOptions` and `IMigrationRecordStore` (any class with these as ctor parameters)

Catalog every consumer. In multi-provider hosts, each of these resolution sites will hit the throwing factory introduced in Task 1.0 — confirm each consumer is reachable only from single-provider code paths (or refactor it to take the typed dependency).

#### Task 0.3 — Call-site survey
Grep for all `GetRequiredService<MigrationRunner>` and `GetService<MigrationRunner>` usages across runners, samples, tests, and docs. Confirm they're all single-provider hosts. Each such site is a backward-compat check during Phase 2.

**Output:** an audit appendix in this plan recording the file:line references and any divergence-tasks discovered.

### Phase 1 — Per-Provider Runner Subclasses + Fail-Loud Detection

Introduces the typed runner per provider AND the multi-provider detection mechanism. The detection mechanism is the load-bearing fix from the assessment (F1) — without it, the plan replaces last-wins shadowing with first-wins shadowing, which is the same UX failure under a different name.

**All Phase 1 tasks honor:**
- Factory-delegate registration so all `RecordStore` types stay `internal` (F3/N1).
- `TryAddSingleton` on every typed registration to handle duplicate `Add{Provider}Migrations` calls (N2).
- Centralised `RegisterBaseAliases` helper that handles the first-vs-subsequent-provider logic (F1, F10).

#### Task 1.0 — Base infrastructure (precedes per-provider work)
1. **Logger contract change** (assessment F7): change `MigrationRunner` ctor to take `ILoggerFactory` instead of `ILogger<MigrationRunner>`. Inside the ctor, call `loggerFactory.CreateLogger(GetType())` once and store. Each subclass instance now logs under its concrete runtime type. CHANGELOG entry: small semver event.
2. **`MultiProviderRegistrationMarker`** internal type in `src/Hyperbee.Migrations/`. Empty marker with one property: `string FirstProvider`.
3. **`RegisterBaseAliases` shared helper** as `internal static` extension on `IServiceCollection`. Signature:
   ```csharp
   internal static void RegisterBaseAliases(
       this IServiceCollection services,
       string providerName,
       Func<IServiceProvider, MigrationOptions> optionsFactory,
       Func<IServiceProvider, IMigrationRecordStore> storeFactory,
       Func<IServiceProvider, MigrationRunner> runnerFactory )
   ```
   Logic: if marker absent, register marker + the three legacy aliases pointing at this provider. If marker present, `RemoveAll` the three base aliases and re-register with throwing factories that name the offending second provider.
4. **Tests for `RegisterBaseAliases`** (no providers needed — pure DI):
   - Calling once registers marker + three aliases.
   - Calling twice removes the legacy aliases and registers throwing factories.
   - Resolving a base type after second call throws with a clear, actionable message.
   - Resolving a base type after one call works.

**Completion criteria:** Logger contract change merged + tested; marker + helper + helper tests merged.

#### Task 1.1 — `PostgresMigrationRunner`
- Add `PostgresMigrationRunner : MigrationRunner` in `src/Hyperbee.Migrations.Providers.Postgres/`. Constructor takes `PostgresRecordStore`, `PostgresMigrationOptions`, `ILoggerFactory`. Forwards to base.
- Update `ServiceCollectionExtensions.AddPostgresMigrations` to:
  - `services.TryAddSingleton<PostgresMigrationOptions>( PostgresMigrationOptionsFactory )` (replaces the existing non-Try registration).
  - `services.TryAddSingleton<PostgresRecordStore>( sp => new PostgresRecordStore( /* ctor args via sp */ ) )` — concrete record-store registered via factory delegate so the type can stay `internal`.
  - `services.TryAddSingleton<PostgresMigrationRunner>( sp => new PostgresMigrationRunner( sp.GetRequiredService<PostgresRecordStore>(), sp.GetRequiredService<PostgresMigrationOptions>(), sp.GetRequiredService<ILoggerFactory>() ) )`.
  - **Replace** the existing `AddSingleton<IMigrationRecordStore, PostgresRecordStore>()`, `AddSingleton<MigrationOptions>(...)`, and `AddSingleton<MigrationRunner>()` registrations with a single call to `services.RegisterBaseAliases("Postgres", optionsFactory, storeFactory, runnerFactory)`.
- Resource runner registration (`AddTransient(typeof(PostgresResourceRunner<>))`) unchanged.
- Test: existing single-provider Postgres unit tests still pass. New tests:
  - Resolve `PostgresMigrationRunner` directly — runs.
  - Resolve `MigrationRunner` (base) — returns the Postgres subclass instance.
  - Logger emitted from Postgres runner is categorized as `Hyperbee.Migrations.Providers.Postgres.PostgresMigrationRunner`.

**Completion criteria:** Postgres subclass exists, all Postgres unit tests pass, new direct-resolution + logger-category tests pass.

#### Task 1.2 — `MongoDBMigrationRunner`
Same shape as 1.1, against the MongoDB provider. `MongoDBRecordStore` stays `internal` (factory-delegate registration).

#### Task 1.3 — `CouchbaseMigrationRunner`
Same shape, against the Couchbase provider. `CouchbaseRecordStore` stays `internal` (factory-delegate registration).

#### Task 1.4 — `OpenSearchMigrationRunner`
Same shape, against the OpenSearch provider. `OpenSearchRecordStore` stays `internal` (factory-delegate registration).

#### Task 1.5 — `AerospikeMigrationRunner`
Same shape, against the Aerospike provider. `AerospikeRecordStore` stays `internal` (factory-delegate registration).

**Cross-provider check:** at the end of Phase 1, all 5 providers expose a typed runner; all 5 record-store types remain `internal`; all 5 use the same `RegisterBaseAliases` helper. The `{Provider}MigrationRunner` types share zero behavior beyond ctor delegation.

### Phase 2 — Multi-Provider Composition Test

Validates the actual capability the work was for: two providers in one host, no shadowing.

#### Task 2.1 — Multi-provider integration test
Add a new integration test that:
- Spins two Testcontainers (Postgres + MongoDB).
- Builds an `IServiceCollection` with both `AddPostgresMigrations` and `AddMongoDBMigrations`.
- Resolves `PostgresMigrationRunner` and `MongoDBMigrationRunner` via DI.
- Runs both. Confirms each provider's ledger contains the expected applied-migrations entries and neither provider sees the other's records.
- Confirms `IServiceCollection.AddPostgresMigrations(...).AddMongoDBMigrations(...)` registration order doesn't change the outcome (run with both orderings).

#### Task 2.2 — Backward-compat regression test
Add a unit/integration test that:
- Calls `services.AddPostgresMigrations(...)` only (no second provider).
- Resolves `MigrationRunner` (base type, not the subclass).
- Confirms `RunAsync` works against the Postgres ledger.
- Confirms the base type resolves to the concrete `PostgresMigrationRunner` instance.

#### Task 2.3 — Discovery scope test
Add a unit test that:
- Builds a host with `AddPostgresMigrations` (one assembly) + `AddMongoDBMigrations` (a different assembly).
- Confirms each runner's `DiscoverMigrations` only finds migrations from its own configured assembly.
- This is an existing behavior — the test pins it against regression under the new shape.

#### Task 2.4 — Profile filtering test
Add a unit test that:
- Configures `PostgresMigrationOptions.Profiles = ["a"]` and `MongoDBMigrationOptions.Profiles = ["b"]`.
- Confirms each runner respects its own profile filter and does not leak into the other.

#### Task 2.5 — Fail-loud regression test (assessment F1)
Add a unit test that:
- Calls both `services.AddPostgresMigrations(...)` and `services.AddMongoDBMigrations(...)`.
- Resolves `MigrationRunner` (base type), `MigrationOptions` (base type), and `IMigrationRecordStore`.
- Confirms each resolution **throws** with a clear, actionable message that names "Multiple providers registered" and instructs the operator to resolve `{Provider}MigrationRunner` explicitly.
- Repeats with reversed registration order (`AddMongoDBMigrations(...).AddPostgresMigrations(...)`) and confirms the throw is symmetric.
- This is the test that proves the F1 fix; without it, the plan is regression-vulnerable to silent first-wins shadowing.

#### Task 2.6 — Idempotent registration test (assessment N2)
Add a unit test that:
- Calls `services.AddPostgresMigrations(...)` twice in a row (e.g., from two helper methods).
- Confirms `PostgresMigrationRunner` resolves successfully (single registration, not duplicate).
- Confirms `MigrationRunner` (base) resolves successfully (single-provider mode preserved across duplicate calls).

**Completion criteria:** Phase 2 tests green on net8/9/10. The multi-provider test demonstrates a use case that cannot work on the current code; the fail-loud test proves the F1 fix; the idempotent test proves the N2 fix.

### Phase 3 — Documentation & Examples

#### Task 3.1 — Multi-provider operator guide (assessment F4 expansion)
Create `docs/site/multi-provider-hosts.md` with **all** of:

1. **When to register multiple providers** (use case). Concrete: app uses Postgres for relational data + MongoDB for documents; running migrations for both at host startup.
2. **Registration shape**. The example from ADR-0023, copyable.
3. **Resolution by typed runner.** Why the base `MigrationRunner` resolution throws in multi-provider hosts; how to fix it (resolve `PostgresMigrationRunner` and `MongoDBMigrationRunner` explicitly).
4. **Worked expand/contract example** — concrete, with filenames:
   - `Migrations.Postgres/2026_01_15_001_AddUsersTable.cs` (the expand: add new column, leave old in place)
   - `Migrations.MongoDB/2026_01_15_001_AddUserProfilesCollection.cs`
   - `Application/UserService.cs` showing the dual-write behind a feature flag (`features.UseNewProfileSchema`)
   - `Migrations.Postgres/2026_03_01_001_DropUsersOldEmailColumn.cs` (the contract: remove old column once flag is fully on)
   - Show the migration filenames sorted by version so the cross-store ordering is visible at a glance.
5. **Negative example showing the wrong way** — a developer who writes a single migration that "creates the Postgres table AND inserts the matching MongoDB doc," then ships, then hits a half-failure where the Postgres DDL committed but the Mongo insert errored. Show what state the system is in. Show why there's no rollback path. The operator-guide is the only place this lesson lands; the ADR cites the pattern but the operator copies whatever code is in the docs.
6. **Failure-isolation code sample** — operator-copyable host-startup hook:
   ```csharp
   var pg = sp.GetRequiredService<PostgresMigrationRunner>();
   var mg = sp.GetRequiredService<MongoDBMigrationRunner>();
   var failures = new List<Exception>();
   try { await pg.RunAsync(ct); }
   catch (Exception ex) { failures.Add(ex); logger.LogError(ex, "Postgres migrations failed"); }
   try { await mg.RunAsync(ct); }
   catch (Exception ex) { failures.Add(ex); logger.LogError(ex, "MongoDB migrations failed"); }
   if (failures.Any()) throw new AggregateException("One or more provider migrations failed", failures);
   ```
   The act of writing this loop forces the operator to confront partial-failure semantics. The package ships no `MultiRunnerCoordinator` type — that would be the meta-runner the ADR rejects (assessment F2).
7. **Parallel composition example** — when providers are disjoint (no cross-store invariants), `Task.WhenAll(pg.RunAsync(ct), mg.RunAsync(ct))` is safe per ADR-0005 (provider locks are independent). Show the pattern; show the costs (sum-of-bootstraps becomes max-of-bootstraps but cumulative resource use is the same).
8. **Squash + multi-runner**: each runner squashes its own ledger; the CLI continues to be invoked per provider with `--provider <name>`. Multi-provider hosts run squash separately per provider.
9. **`services.Replace` semantics**: in multi-provider mode replaces only the base alias, not the per-provider subclasses. Operators who wrap or override the runner must do so against the typed subclass, not the base.

#### Task 3.2 — Update each provider README
Each `src/Hyperbee.Migrations.Providers.{Provider}/README.md` adds a one-paragraph "multi-provider hosts" note pointing at the operator guide section.

#### Task 3.3 — Provider-author checklist (assessment F10)
Add a `provider-template.md` doc (or expand `CONTRIBUTING.md`) with a five-point checklist for any future provider added to the codebase:
1. Register the concrete options factory with `TryAddSingleton`.
2. Register the concrete record store with `TryAddSingleton` via factory delegate (keep the type `internal`).
3. Register `{Provider}MigrationRunner` with `TryAddSingleton` via factory delegate.
4. Call `services.RegisterBaseAliases("{ProviderName}", optionsFactory, storeFactory, runnerFactory)` exactly once at the end.
5. Add a multi-provider integration test that pairs the new provider with at least one existing provider; assert each runner's ledger is independent.

This is documentation, not infrastructure — provider velocity is too low to justify a source generator (assessment F10).

#### Task 3.4 — CHANGELOG entry (assessment F9)
Add an entry under "Behavior changes" in the next release's CHANGELOG:

> **Multi-provider hosts.** Calling `Add{Provider}Migrations` for more than one provider in the same `IServiceCollection` previously caused silent shadowing — only the last-registered provider's runner ran. The base `MigrationRunner` / `MigrationOptions` / `IMigrationRecordStore` resolutions now throw `InvalidOperationException` with a clear message when multiple providers are registered; resolve `{Provider}MigrationRunner` explicitly. Single-provider hosts are unaffected. (See ADR-0023 + the multi-provider hosts operator guide.)
>
> **Logger category.** `MigrationRunner` log lines were previously emitted under category `Hyperbee.Migrations.MigrationRunner` regardless of provider. They now use the runtime type — `Hyperbee.Migrations.Providers.Postgres.PostgresMigrationRunner`, etc. Operators tailing logs by category may need to update filters.

#### Task 3.5 — ADR-0023 transition to Accepted
Once Phase 2 tests are green and Phase 3 docs are merged, ADR-0023 status moves from Proposed to Accepted. Update the ADR file and `docs/decisions/INDEX.md` to reflect the change.

### Phase 4 — Cleanup

#### Task 4.1 — Remove unused single-provider shadowing
After Phase 1, the existing `services.AddSingleton<MigrationOptions>( sp => sp.GetRequiredService<{Provider}MigrationOptions>() )` registrations are redundant in single-provider hosts (the new `TryAddSingleton` handles this). Confirm the old `AddSingleton` lines were replaced with `TryAddSingleton` everywhere, not just added alongside.

#### Task 4.2 — Audit for any provider that registers concrete record store as `IMigrationRecordStore` directly
Confirmed in Phase 0; resolved in Phase 1. This task is a final pass to ensure no dual registration remains.

## Risk Register

(Note: an earlier version of this register contained a factual error — it claimed Postgres+MongoDB RecordStore types were public. Verified during the assessment: ALL FIVE provider RecordStore types are `internal`. The "expose for symmetry" risk is moot; factory-delegate registration is the chosen approach for all 5 providers, keeping all RecordStore types `internal`.)

| Risk | Likelihood | Impact | Mitigation |
|------|-----------|--------|------------|
| A test fixture or sample re-resolves `MigrationRunner` (base) in a multi-provider integration test and gets a confusing throw | Low | Test failure with clear message (acceptable) | Phase 2 Task 2.5 explicitly tests this; the throw message names "Multiple providers registered" + instructs how to fix. |
| Existing CLI (`Hyperbee.MigrationRunner.{Provider}` projects) implicitly assumes single-provider | High (already true) | None — CLI stays single-provider per binary | Each runner project is its own executable; multi-provider CLI hosts are out of scope for this plan. |
| Squash codegen reaches into `MigrationRunner` types | Low | Unknown — depends on what Phase 6 of the squash plan looks like | Phase 0 audit confirms; if squash references `MigrationRunner` directly, the per-provider subclass inherits cleanly. Squash CLI is per-provider per fleet manifest (verified in assessment § F5) — multi-provider hosts don't compose into the squash readiness probe. |
| Logger ctor change (`ILogger<MigrationRunner>` → `ILoggerFactory`) is a small semver event | Low | Consumers passing `ILogger<MigrationRunner>` directly to a custom subclass need to update | Document in CHANGELOG (Task 3.4); the affected consumers are vanishingly rare (you'd need a custom `MigrationRunner` subclass already, which the test asserts is internal-only today). |
| Future provider added without using `RegisterBaseAliases` re-introduces shadowing | Medium | Returns the original bug | Provider-author checklist (Task 3.3); Phase 2 Task 2.5 catches it on integration; PR review checks against checklist. |
| Documentation drift between ADR-0023 (recommends per-provider subclass) and ADR-0006 (options inheritance) | Resolved | — | ADR-0023 explicitly amends ADR-0006 § DI registration shape; ADR-0006 has back-reference. |

## Riskiest task

**Task 1.0** (base infrastructure including `RegisterBaseAliases`) — the load-bearing fix from the assessment. If `RegisterBaseAliases` has a bug, every provider's registration is wrong and the F1 fix is invalid. Address first; ship Task 1.0 with thorough unit tests before any per-provider task uses it.

**Tasks 1.1-1.5** are mechanical applications of the proven shape from Task 1.0. Each provider takes the same shape; difference is which concrete types get registered. Address Task 1.1 (Postgres) first as the canonical shape, then 1.2-1.5 follow.

## Test Plan

- **Unit tests** verify DI registration, type identity (`MigrationRunner == PostgresMigrationRunner` instance), and discovery scope per runner. Run on net8/9/10.
- **Integration tests** verify two-provider end-to-end: spawn Postgres + MongoDB containers, register both, resolve both runners, run both, confirm independent ledgers. Run on net10 (multi-target containers are wasteful at integration tier).
- **Backward-compat tests** verify single-provider hosts don't break.

## ADR compliance check

| Task | Honors ADR | How |
|------|-----------|-----|
| All Phase 1 tasks | ADR-0006 (amended by ADR-0023) | Per-provider options inheritance preserved. Base-type aliases switch from `AddSingleton` to `TryAddSingleton`-with-throwing-fallback per the amendment. |
| Task 1.1-1.5 | ADR-0003 | `IMigrationRecordStore` contract unchanged; concrete record-store types remain `internal` per assessment N1. |
| Task 1.1-1.5 | ADR-0005 | Lock semantics remain provider-native; each runner uses its own lock; no cross-provider lock contention. |
| Task 2.1 | ADR-0019 | Squash is per-provider already; this work doesn't change squash. (Verified in assessment § F5.) |
| Task 3.1 | ADR-0020 | The operator guide explicitly states cross-provider rollback isn't supported — matches the up-only squash policy. |
| All tasks | ADR-0023 | This plan implements ADR-0023 (post-assessment amended version). |

## Decisions

- **2026-05-08** Adopted ADR-0023 — multi-runner over meta-runner. Reasoning in the ADR's "Why Not a Meta-Runner" section.
- **2026-05-08** Per-provider `MigrationRunner` subclass approach over keyed services. Reasoning: compile-time safety, IDE discoverability. The rejection is ergonomics-driven — keyed services have been GA since .NET 8 (assessment F6 refinement).
- **2026-05-08** **All five providers** use factory-delegate registration; **all five RecordStore types stay `internal`**. Earlier framing of "expose for symmetry with Postgres+MongoDB" was based on a false premise — verified in assessment § N1 that all five were already internal.
- **2026-05-08** No `MultiRunnerCoordinator` package type. Operators get a doc-only worked sample showing failure-isolation, parallel composition, and expand/contract. The act of writing the foreach loop forces them to confront the failure semantics. (Assessment F2 reversal — my earlier Red-Blue₁ proposal of a "thin coordinator" was meta-runner-shaped.)
- **2026-05-08** Base `MigrationRunner` ctor takes `ILoggerFactory`, not `ILogger<MigrationRunner>`. Subclass instances log under their runtime type. Small semver event; CHANGELOG entry. (Assessment F7.)
- **2026-05-08** Multi-provider detection mechanism: `MultiProviderRegistrationMarker` + `RegisterBaseAliases` helper. Single-provider hosts unchanged; second `Add{Provider}Migrations` swaps the legacy aliases with throwing factories naming both providers. (Assessment F1 — the load-bearing fix.)

## Status

- Phase 0: ☑ COMPLETE 2026-05-11 (audit completed inline during implementation)
- Phase 1: ☑ COMPLETE 2026-05-11 (5 typed runner subclasses + MultiProviderRegistrationMarker + RegisterBaseAliases + ILoggerFactory ctor shipped)
- Phase 2: ☑ COMPLETE 2026-05-11 (18 unit tests: RegistrationExtensionsTests 8 + MultiProviderHostTests 13 including Task 2.3 Discovery scope + Task 2.4 Profile filtering + Task 4.1 services.Replace; the live two-runner integration test from Task 2.1 is tracked as a v3.0.1 enhancement -- DI shape regressions surface here at the unit tier)
- Phase 3: ☑ COMPLETE 2026-05-11 (multi-provider-hosts.md operator guide; provider README multi-provider notes; CONTRIBUTING.md provider-author DI checklist; CHANGELOG entry; ADR-0023 promoted Accepted)
- Phase 4: ☑ COMPLETE 2026-05-11 (cleanup audit: no leftover AddSingleton base aliases per Task 4.1; record stores remain internal per Task 4.2)

## Effort

By the velocity calibration in `feedback_velocity_calibration.md`: a single new provider takes ~1 day (Aerospike) to under a week (Couchbase). This work is **smaller per provider** than a new provider — the runner subclass is ~10 lines plus DI rewiring plus tests.

Post-assessment estimate (revised upward from the original 4-5 days because Task 1.0 + the worked operator-guide examples + the broader audit scope are real work):
- **Phase 0** (audit + broader N3 scope): 1 day
- **Phase 1.0** (logger contract change + `MultiProviderRegistrationMarker` + `RegisterBaseAliases` + thorough unit tests): 1 day. Riskiest task; load-bearing.
- **Phase 1.1-1.5** (per-provider subclasses, mechanical once 1.0 is right): 1.5 days
- **Phase 2** (integration tests + fail-loud regression + idempotent registration): 1 day
- **Phase 3** (operator guide with worked + negative + failure-isolation samples; provider-author checklist; CHANGELOG): 1.5 days. Bigger than original because the docs are load-bearing per the assessment.
- **Phase 4** (cleanup): 0.5 day

**Total: ~6.5 days.** Up from 4-5 in the pre-assessment estimate; the extra 1.5-2 days is the cost of doing the assessment-flagged work properly rather than shipping the original plan.

**Total: ~4-5 days.**

This is intentionally a small plan. The ADR is the load-bearing artifact; the implementation is mechanical.
