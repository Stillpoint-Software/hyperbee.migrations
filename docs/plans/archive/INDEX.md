# plans/archive/INDEX.md

Completed plans. All four delivered the v3.0 release (migration squashing for
all 5 providers + the `hyperbee-migrations squash` CLI + multi-runner
composition). Ship SHA `ae9e78f`; final CI run 25977961816 = 23/23 green.

| Plan | Title | Completed | Summary |
|------|-------|-----------|---------|
| 2026-05-migration-squashing-v1 | [Migration Squashing v1 (Universal Scaffolding + Script-Format + Postgres Codegen)](2026-05-migration-squashing-v1.md) | 2026-05-16 | Phases 0-6 (universal ledger scaffolding for all 5 providers, attribute discovery, reconciliation, ADR-0022 script-format resources, `ISquashStrategy` contract, Postgres `PgDumpSnapshotStrategy`). Phase 7/8 deferred closers (CLI, Testcontainers verification round, operator guide) delivered via ADR-0024 + the sibling per-provider plan + the v3.0 pre-ship hardening pass. |
| 2026-05-migration-squashing-providers | [Squash Codegen for the Four Non-Postgres Providers](2026-05-migration-squashing-providers.md) | 2026-05-16 | R-P1..R-P9 across 6 phases. Aerospike `InfoSnapshotStrategy`, OpenSearch `RestStateDiffStrategy`, MongoDB `IntrospectionSnapshotStrategy`, Couchbase `HybridStrategy` — each with 6 components + unit/determinism/verification-round tests. No ADR-0019 amendments needed across all 4 (the contract held). |
| 2026-05-multi-runner-composition | [Multi-Runner Composition (ADR-0023)](2026-05-multi-runner-composition.md) | 2026-05-16 | Phases 0-4: base infrastructure (`ILoggerFactory` ctor, `MultiProviderRegistrationMarker`, `RegisterBaseAliases`), 5 typed `{Provider}MigrationRunner` subclasses, 18 unit tests, operator guide + provider-author DI checklist + CHANGELOG. ADR-0023 promoted Accepted. |
| 2026-05-v3-preship-hardening | [v3.0 Pre-Ship Hardening](2026-05-v3-preship-hardening.md) | 2026-05-16 | 5-phase remediation of the five-agent pre-ship audit: 2 gated decisions (ADR-0025 retain NullSquashStrategy, ADR-0026 cut deploy-time fleet gate), doc ship-blockers, README v3.0 section, Aerospike readiness gate, GSI rebalance-retry consolidation + two root-caused Couchbase indexer-readiness flakes, dead-code removal, drift cleanup. Final CI 23/23 green on ae9e78f. |
