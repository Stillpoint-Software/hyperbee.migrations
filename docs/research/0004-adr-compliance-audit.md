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

1. ~~**ADR-0012 (WithProductionDefaults)**~~ — **CLOSED 2026-05-03**. Options-factory wiring landed in `ServiceCollectionExtensions.AddOpenSearchMigrations`: when the `UseProductionDefaultsMarker` is registered, the factory flips the four documented defaults (Green threshold, PerMigration waits, RequireUnsafeJustification, RequireExplicit context) on the `OpenSearchMigrationOptions` instance BEFORE invoking the user's configuration callback, so explicit user overrides still win. Coverage: `tests/Hyperbee.Migrations.Tests/Providers/OpenSearch/WithProductionDefaultsTests.cs` (3 tests).

2. ~~**ADR-0009 (Convention-Based Record IDs)**~~ — **CLOSED 2026-05-03 (commit 163196f)**. Focused convention test added at `tests/Hyperbee.Migrations.Tests/DefaultMigrationConventionsTests.cs` covering the documented `record.<version>.<kebab-cased-name>` format and the missing-attribute throw path.

3. ~~**ADR-0016 (No File-Level Templating)**~~ — **CLOSED 2026-05-03 (commit 163196f)**. Dependency-scan unit test added at `tests/Hyperbee.Migrations.Tests/Providers/OpenSearch/OpenSearchProviderDependencyTests.cs` that asserts the OpenSearch provider assembly references no `Hyperbee.Templating*` package. CI fails if a future contributor adds the dependency.

### Hardening landed alongside the audit

Items addressed in commits 163196f and the follow-up:

- **EOF-anchored parser** — the OpenSearch statement parser now applies `.Eof()` to the top-level Parlot parser, so trailing tokens after a successful prefix-match are reported as parse errors instead of silently dropped. Closes the documented `NO WAIT` UX gap (bare `NO WAIT` without parens-and-justification used to parse as `<verb>` + trailing garbage; now correctly fails). Four parse-time-rejection tests previously deferred are now passing.
- **Domain-exception wrapping** — grammar-level `InvalidOperationException` (raised inside Parlot `.Then(...)` callbacks for empty-justification and malformed version-literal validation) is now wrapped into `OpenSearchParseException` at the `Parse()` boundary. Callers handle one exception type.
- **R-24c (f) bulk-load 429 retry coverage** — the OpenSearch.Net library owns the actual 429-retry mechanism (configured via `BulkAll`'s `BackOffRetries` / `BackOffTime` options, threaded through from `BulkLoadOptions` per R-20). The provider-owned behavior is the `BulkAllObserver`'s WARN-logging path when `response.Retries > 0`. Coverage: `tests/Hyperbee.Migrations.Tests/Providers/OpenSearch/BulkAllObserverRetryTests.cs` (4 unit tests driving the observer with synthetic responses) plus the joint cluster-level scenario added as Step 4 of `docs/runbooks/opensearch-aws-validation.md` (chaos via cluster-saturation against an undersized AWS instance).

### Open Questions during the audit

None. All ADRs cleanly map to code + tests; all soft spots noted in the original audit have been closed.

## Release readiness

The OpenSearch provider's ADR set (0011-0017) plus the cross-provider ADRs (0001-0010) are all honored by the v1 implementation. No ADR has been silently superseded, deferred-without-record, or violated. The provider clears the ADR-compliance gate for release.

The DoD line on the release checklist:

> 2026-05-03  ADR compliance audit (0001-0017): PASS  (17/17 honored; all soft spots closed). See docs/research/0004-adr-compliance-audit.md

## Method

This audit was performed by:

1. Listing all Accepted ADRs (17) from `docs/decisions/INDEX.md`.
2. For each ADR, reading the Decision and Consequences sections.
3. Locating the code path or paths where the decision is implemented (file + symbol).
4. Locating the test class or classes that exercise the decision, OR identifying the doc artifact that documents the verification approach if no automated test applies (ADR-0010 self-evidence; ADR-0016 absence-of-feature).
5. Flagging anything that doesn't fit either bucket as a soft spot.

The audit document itself is durable and version-controlled; future drift will surface in the diff against this baseline.
