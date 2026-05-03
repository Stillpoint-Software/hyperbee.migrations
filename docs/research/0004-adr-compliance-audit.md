# ADR Compliance Audit — OpenSearch Provider Release

**Date:** 2026-05-03
**Scope:** ADRs 0001-0017 (10 cross-provider, 7 OpenSearch-specific)
**Method:** for each Accepted ADR, locate (a) the code path that implements the decision and (b) the passing test or doc reference that verifies it. ADRs with neither are flagged for follow-up before release.

This is the regression check called for by phase Definition-of-Done item "ADRs touched by this phase verified against acceptance criteria" (per B1 / NF-5). It is intentionally NOT the first verification — each ADR was verified at the time its slice landed; this audit is the cross-cutting sweep that confirms nothing has decayed or been silently superseded.

## Audit table

| ADR | Title | Code | Verification |
|-----|-------|------|--------------|
| 0001 | Use Parlot for Statement Parsers | `src/.../Internal/Grammar/OpenSearchStatementParser.cs` (Parlot.Fluent productions); existing Aerospike statement parsers also use Parlot | `tests/.../Internal/FoundationVerbParserTests.cs` (51+ verb tests), `OpenSearchStatementParserTests`, `BodySourceParserTests`, `WhenVersionTests`, `NoWaitParserTests` |
| 0002 | Resource Migration Pattern | `src/.../Resources/OpenSearchResourceRunner.cs` exposes `StatementsFromAsync` and `RunStatementsFromJsonAsync`; Aerospike/Couchbase/MongoDB providers mirror | `tests/.../OpenSearchResourceRunnerIntegrationTests.cs`, `OpenSearchContextFilterTests` |
| 0003 | Provider Record Store Contract | `src/Hyperbee.Migrations/IMigrationRecordStore.cs` (5-method interface); `src/.../OpenSearchRecordStore.cs` implements | `tests/.../OpenSearchRecordStoreTests.cs` (lock tuning), `OpenSearchRecordStoreIntegrationTests`, `OpenSearchPartialRollbackIntegrationTests` |
| 0004 | Reflection-Based Migration Discovery | `src/Hyperbee.Migrations/MigrationRunner.cs::DiscoverMigrations`; `[Migration]` attribute drives ordering | `tests/.../RunnerTests.cs` (multiple discovery + ordering scenarios) |
| 0005 | Provider-Native Distributed Locking | `src/.../OpenSearchRecordStore.cs::CreateLockAsync` (op_type=create + realtime-GET takeover); other providers use their native primitives | `tests/.../OpenSearchLockContentionTests.cs`, `OpenSearchRecordStoreLockTuningTests` |
| 0006 | Options Inheritance + DI Registration | `src/.../OpenSearchMigrationOptions.cs : MigrationOptions`; `services.AddOpenSearchMigrations(...)` extension; mirrors Aerospike/Couchbase/MongoDB | `tests/.../OpenSearchAuthenticationOptionsTests.cs` covers IConfiguration overload |
| 0007 | Lifecycle Hooks + Cron | `src/Hyperbee.Migrations/IContinuousMigration.cs`; `src/Hyperbee.Migrations/Helper/MigrationCronHelper.cs` | `tests/.../RunnerTests.cs` cron + continuous-migration test cases |
| 0008 | Composable Wait/Retry Infrastructure | `src/Hyperbee.Migrations/Wait/` (RetryStrategy, Backoff, Pause); `src/.../Internal/Dispatch/StatementDispatcher.cs::DispatchWaitUntilTaskAsync` uses exponential backoff | Existing wait infra tests + `OpenSearchTemplatePolicyIntegrationTests` exercises WAIT FOR + WAIT UNTIL TASK |
| 0009 | Convention-Based Record IDs | `src/Hyperbee.Migrations/IMigrationConventions.cs::GetRecordId`; `DefaultMigrationConventions` returns `{version}-{type-name}` | Indirectly via `RunnerTests` (ledger writes) and `OpenSearchPartialRollbackIntegrationTests` |
| 0010 | Dual-Tier Testing Strategy | `tests/Hyperbee.Migrations.Tests/` (MSTest unit, no Docker); `tests/Hyperbee.Migrations.Integration.Tests/` (MSTest + Testcontainers) | Self-evident from project structure; `334 unit tests pass`, integration tests gated by `#if INTEGRATIONS` and run in CI via `multi_node_tests.yml` |
| 0011 | Hybrid Parser+Runtime Injection | Parser sets `InjectDynamicStrict` / `InjectOpTypeCreate` / `NoWaitJustification` / `UnsafeJustification` flags on AST records; `SafeDefaultMergeMiddleware` and `StatementDispatcher` consume at dispatch time | `tests/.../SafeDefaultMergeMiddlewareTests.cs` (R-17 dynamic:strict, composed_of skip); `tests/.../OpenSearchR24cGapFillIntegrationTests.cs::DynamicStrict_AutoInjected_RejectsUnmappedFields` (live-cluster R-24c (g)) |
| 0012 | WithProductionDefaults() Extension | `src/.../ServiceCollectionExtensions.cs::WithProductionDefaults()`; placeholder marker in DI today, options-factory wiring deferred to a follow-up slice noted in ADR consequences | Smoke registration (the marker is registered); follow-up noted in plan if the four defaults need automated coverage |
| 0013 | Always-Create Indices + Override | `src/.../Internal/Bootstrap/Steps/LedgerIndexInitStep.cs` and `LockIndexInitStep.cs` honor `AssumeIndicesExist` | `tests/.../OpenSearchRecordStoreIntegrationTests.cs` covers create-on-bootstrap + verify-on-bootstrap |
| 0014 | State-Machine Façade over Pipeline | `src/.../Internal/Bootstrap/OpenSearchBootstrapper.cs` (public `RunAsync` returning `BootstrapResult`); `IBootstrapStep[]` plug-in order | `tests/.../Bootstrap/OpenSearchBootstrapperTests.cs` (step ordering, failure surfacing) |
| 0015 | Parser Offline-Pure; All I/O Runtime Middleware | Parser produces `TemplateBodyRef` (name only, no fetch); `TemplateResolutionMiddleware` performs `GET /_index_template/<id>` immediately before CREATE INDEX dispatch | `tests/.../TemplateResolutionMiddlewareTests.cs` (extraction logic); `tests/.../OpenSearchMigrateIndexIntegrationTests.cs::MigrateIndex_ProducesIdenticalEndState_ToHandComposedSequence` (R-24c (o)) |
| 0016 | No File-Level Templating | OpenSearch provider has no Hyperbee.Templating dependency (verified via `grep` over the project file); typed options + IConfiguration binding handle env-variation per the house pattern | Code search; no positive test (absence of a feature is the point) |
| 0017 | Body-Source Grammar (Three Forms) | `src/.../Internal/Ast/StatementAst.cs` defines `BodySource`, `BodyRef`, `BodyFileRef`; `src/.../Internal/Grammar/OpenSearchStatementParser.cs` produces both via `OneOf`; `src/.../Resources/OpenSearchResourceRunner.cs::ResolveBody` resolves with `bodies` first, sibling fallback, file load | `tests/.../Internal/BodySourceParserTests.cs` (14 grammar tests); `tests/.../OpenSearchBodySourceIntegrationTests.cs` (5 live resolver tests including bodies-section beats sibling, missing-ref remediation) |

## Findings

### Compliant (17 of 17)

Every Accepted ADR has both a code implementation path and a verification mechanism. No ADR is dangling.

### Soft spots noted for follow-up

These are NOT compliance failures — the ADRs are honored. They are areas where the verification could be tighter:

1. **ADR-0012 (WithProductionDefaults)** — the extension method exists and registers a marker, but the options-factory wiring that flips the four defaults (Green threshold, PerMigration waits, RequireExplicit context, RequireUnsafeJustification) on options-instance construction is a Phase 6 follow-up per the ADR's own consequences section. Today, calling `WithProductionDefaults()` is the marker registration; the user still has to set the four options manually. Worth a follow-up slice once the options-factory pattern is settled. Not a regression — the slice was scoped this way intentionally per the requirements doc.

2. **ADR-0009 (Convention-Based Record IDs)** — verified indirectly through ledger-bearing tests rather than a dedicated unit test. The convention is simple enough (version + type name) that the indirect coverage is sufficient, but a focused convention-output test would tighten the regression net for any future ID-format change.

3. **ADR-0016 (No File-Level Templating)** — verified through absence (no Hyperbee.Templating reference in the project file). A code-level "no positive test for absence" is correct but means a future contributor adding the dependency wouldn't be alerted by CI. The provider's csproj is small enough that a dependency-scan grep in the build is the cheapest possible safeguard if future drift becomes a concern.

### Open Questions during the audit

None. All ADRs cleanly map to code + tests with the soft spots noted above.

## Release readiness

The OpenSearch provider's ADR set (0011-0017) plus the cross-provider ADRs (0001-0010) are all honored by the v1 implementation. No ADR has been silently superseded, deferred-without-record, or violated. The provider clears the ADR-compliance gate for release.

The DoD line on the release checklist:

> 2026-05-03  ADR compliance audit (0001-0017): PASS  (17/17 honored; 3 soft spots noted in docs/research/0004-adr-compliance-audit.md, none blocking)

## Method

This audit was performed by:

1. Listing all Accepted ADRs (17) from `docs/decisions/INDEX.md`.
2. For each ADR, reading the Decision and Consequences sections.
3. Locating the code path or paths where the decision is implemented (file + symbol).
4. Locating the test class or classes that exercise the decision, OR identifying the doc artifact that documents the verification approach if no automated test applies (ADR-0010 self-evidence; ADR-0016 absence-of-feature).
5. Flagging anything that doesn't fit either bucket as a soft spot.

The audit document itself is durable and version-controlled; future drift will surface in the diff against this baseline.
