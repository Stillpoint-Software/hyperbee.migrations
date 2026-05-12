# Changelog

All notable changes to **Hyperbee.Migrations** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [3.0.0] — 2026-05-11 — Migration Squashing (all 5 providers)

This release ships the destructive-model migration squash feature
(per [ADR-0019](docs/decisions/0019-migration-squash-replaces-graph.md)) plus
the universal script-format resource form (per
[ADR-0022](docs/decisions/0022-script-format-resource-migrations.md)).
v3.0 is a major release because of two breaking changes around
`IMigrationRecordStore` and provider record-store schemas — both protected by
safe back-compat paths so existing v2 migrations and consumers keep working
unchanged.

### Highlights

- **Squash migrations** — declare a migration that subsumes a contiguous range
  of prior versions via `[Migration(version, Replaces = new[] { ... })]` or
  `[Migration(version, ReplacesRange = "1000-1500")]`. The runner reconciles
  per environment: mature environments auto-mark the squash without running
  its body; fresh environments run the squash as a baseline.
- **Squash codegen for all five providers** — every provider ships its
  own snapshot strategy in v1: Postgres `PgDumpSnapshotStrategy` (canonical
  `.sql` from `pg_dump --schema-only`), Aerospike `InfoSnapshotStrategy`,
  OpenSearch `RestStateDiffStrategy`, MongoDB `IntrospectionSnapshotStrategy`,
  Couchbase `HybridStrategy`. Shipping all five together is deliberate per the
  2026-05-09 scope retraction in
  [ADR-0019 amendment A7](docs/decisions/0019-migration-squash-replaces-graph.md):
  the strategy abstraction is only proven correct by being implemented
  against the full provider matrix. **Outcome:** the contract held for all
  five providers without amendment -- four consecutive provider
  implementations after Postgres shipped without requiring an ADR-0019
  amendment, confirming the 5-interface abstraction is correct.
- **Universal `.statements` script form** — alongside the legacy
  `.statements.json` shape, all four NoSQL providers accept multi-statement
  script files with `--`/`//`/`/* */` comments and `;` terminators. Postgres
  treats `.statements` as an alias for `.sql`. Backward-compatible: existing
  `.statements.json` files continue to apply unchanged.
- **Two-phase fleet readiness gate** — the squash CLI refuses generation while
  any registered fleet member is mid-range
  (`MidRangeFleetException`); the runner refuses deploy when an environment
  isn't registered (`UnregisteredEnvironmentException`) or has gone stale
  beyond the configurable window (`StaleFleetMemberException`, default
  30 days per [ADR-0019 amendment A15](docs/decisions/0019-migration-squash-replaces-graph.md)).
- **Recovery acknowledgement token** for the
  `MidRangeSquashException` escape hatch — deterministic per
  `(env, squash, missing-versions)` so retries reproduce the same token,
  but accidental copy-paste from a sibling environment is rejected.

### Added

- `[Migration(version, Replaces = ..., ReplacesRange = ...)]` — squash declaration.
- `MigrationRecordKind` enum (`Migration`/`Squash`/`Baseline`).
- `MigrationRecord.Checksum` + `Kind` + `Replaces` (long[]).
- `MigrationLedgerIntegrityException` — refuses inconsistent `Kind`/`Replaces` rows.
- `MidRangeSquashException` — partial-coverage refusal with three documented recovery paths.
- `MigrationApplyMode` enum (`Fresh`/`PartialCatchUp`) plus `MigrationContext` ambient context with `IsFreshInstall` back-compat sugar.
- `IChecksumStrategy` + `DefaultChecksumStrategy` — deterministic SHA-256 over `(typeof.FullName, Version)`.
- `WritePrecondition` (`None`/`MustNotExist`) + `WriteOutcome`
  (`Created`/`AlreadyExistsBenign`/`PreconditionFailed`).
- `IMigrationRecordStore` gains
  `WriteAsync(MigrationRecord, WritePrecondition, CancellationToken) → WriteOutcome`,
  `IntersectWithAppliedAsync`, and `IntersectWithSquashedAsync` — all with safe
  default-interface-method implementations so v2 record stores compile and run
  unchanged.
- `Hyperbee.Migrations.Squash` namespace with the strategy contract
  (`ISquashStrategy`, `SquashGenerationResult`, `ITopologySignature`,
  `IDataOpClassifier`, `ISnapshotCanonicalizer`, `ISquashVerifier`,
  `SquashStrategyDescriptor`).
- `Hyperbee.Migrations.Resources` namespace with `ResourceFormat`,
  `ResourceFormatDetector`, and `ScriptStatementSplitter` for the universal
  script form.
- Postgres squash components (`PostgresTopologySignature`,
  `PostgresStatementClassifier` + `PostgresStatementSplitter`,
  `PostgresSnapshotCanonicalizer`, `PostgresDataOpClassifier`,
  `PgDumpSnapshotStrategy`, `PostgresSquashVerifier`,
  `PostgresMigrationSourceScanner`).
- Aerospike squash components (`AerospikeTopologySignature`,
  `AerospikeStatementClassifier` + `AerospikeStatementKind`,
  `AerospikeSnapshotCanonicalizer`, `AerospikeDataOpClassifier`,
  `InfoSnapshotStrategy` + `AerospikeSquashGenerationContext` +
  `AerospikeSnapshotCapture`, `AerospikeSquashVerifier`,
  `AerospikeMigrationSourceScanner`).
- OpenSearch squash components (`OpenSearchTopologySignature`,
  `OpenSearchStatementClassifier`,
  `OpenSearchSnapshotCanonicalizer` -- JSON-section canonical form with
  opaque painless preservation per the Task 2.0 spike,
  `OpenSearchDataOpClassifier`,
  `RestStateDiffStrategy` + `OpenSearchSquashGenerationContext` +
  `OpenSearchSnapshotCapture`, `OpenSearchSquashVerifier`,
  `OpenSearchMigrationSourceScanner`).
- MongoDB squash components (`MongoDBTopologySignature`,
  `MongoDBStatementClassifier` + `MongoDBStatementKind`,
  `MongoDBSnapshotCanonicalizer` -- JSON-section canonical form with
  ephemeral strip catalog (uuid/readOnly/v/ns),
  `MongoDBDataOpClassifier`,
  `IntrospectionSnapshotStrategy` + `MongoDBSquashGenerationContext` +
  `MongoDBSnapshotCapture`, `MongoDBSquashVerifier`,
  `MongoDBMigrationSourceScanner`).
- Couchbase squash components (`CouchbaseTopologySignature`,
  `CouchbaseStatementClassifier` + `CouchbaseStatementKind`,
  `CouchbaseSnapshotCanonicalizer` -- JSON-section canonical form with
  deferred-build GSI state preservation (R-P3 OQ resolution: `state=online`
  dropped, `state=deferred` preserved, transient states throw at squash-time),
  `CouchbaseDataOpClassifier` -- parameterized N1QL `QueryAsync` /
  `AnalyticsQueryAsync` default-deny (R-P3 OQ resolution),
  `HybridStrategy` + `CouchbaseSquashGenerationContext` +
  `CouchbaseSnapshotCapture`, `CouchbaseSquashVerifier`,
  `CouchbaseMigrationSourceScanner`).
- `[DataMigration]` and `[StructuralOnly]` attributes (ADR-0019 A5) -- the
  Roslyn-based source scanners refuse squash generation if a migration class
  matches the data-op heuristic without an explicit annotation.
- Two-phase fleet gate types: `SquashMetadata`, `SquashFleetGate`,
  `StaleFleetMemberException`, `UnregisteredEnvironmentException`,
  `MidRangeFleetException`.
- `RecoveryAcknowledgement` — deterministic 12-char token for the
  `recover from-mid-range` escape hatch.
- **Per-provider `MigrationRunner` subclasses** (`PostgresMigrationRunner`,
  `MongoDBMigrationRunner`, `CouchbaseMigrationRunner`,
  `OpenSearchMigrationRunner`, `AerospikeMigrationRunner`). Each provides
  a unique DI handle so multi-provider hosts can resolve and run each
  provider's runner independently. See ADR-0023 + the multi-provider
  hosts operator guide.

### Changed (back-compat preserved)

- All five provider record stores override `IntersectWithAppliedAsync` with a
  single-round-trip realtime read (Postgres `WHERE = ANY`, MongoDB
  `find _id $in` with majority+primary, Couchbase parallel `ExistsAsync`,
  Aerospike `BatchGet`, OpenSearch `_mget realtime=true`).
- Postgres / MongoDB / Couchbase / OpenSearch override
  `IntersectWithSquashedAsync` for transitive squash satisfaction. Aerospike's
  transitive override is deferred to a follow-up; the DIM default returns an
  empty set, so direct auto-mark works but re-squash transitivity does not on
  Aerospike v1.
- OpenSearch ledger index strict mapping extended with `kind` (byte) and
  `replaces` (long[]) fields. Existing v2-era indices receive an additive
  `PUT _mapping` patch on bootstrap, idempotent and IAM-aware.
- `MigrationDescriptor` (previously a private record on `MigrationRunner`)
  is now a public core type so squash strategies can consume it.
- **`MigrationRunner` accepts `ILoggerFactory` in addition to
  `ILogger<MigrationRunner>`.** Per ADR-0023 (assessment F7) the new
  primary constructor takes `ILoggerFactory` and creates a logger
  categorized under the concrete runtime type so per-provider subclass
  instances log under their own type names
  (e.g. `Hyperbee.Migrations.Providers.Postgres.PostgresMigrationRunner`).
  The original `ILogger<MigrationRunner>` constructor remains for back-
  compat. Operators tailing logs by category may need to update filters.
- **Multi-provider hosts**: calling `Add{Provider}Migrations` for more
  than one provider on the same `IServiceCollection` previously caused
  silent shadowing — only the last-registered provider's runner ran.
  The base `MigrationRunner` / `MigrationOptions` / `IMigrationRecordStore`
  resolutions now throw `InvalidOperationException` with a clear,
  actionable message when multiple providers are registered; resolve
  the typed `{Provider}MigrationRunner` explicitly. Single-provider
  hosts are unaffected. (See ADR-0023 + the multi-provider hosts
  operator guide at `docs/site/multi-provider-hosts.md`.)

### Breaking changes (with safe back-compat paths)

1. **`IMigrationRecordStore` gains three methods.** Custom implementations
   compile and run unchanged via the DIM defaults (`WriteAsync(record, ...)`
   delegates to legacy `WriteAsync(string)`; `IntersectWithAppliedAsync` falls
   back to a per-id `ExistsAsync` loop; `IntersectWithSquashedAsync` returns an
   empty set). Override these to opt into squash support.

2. **Provider record-store schemas gain `Checksum` + `Kind` columns/fields.**
   Migration is automatic and idempotent on first v3 apply. Pre-existing rows
   read as `Checksum=null, Kind=Migration` and pass integrity validation.

   - Postgres: `ALTER TABLE ADD COLUMN IF NOT EXISTS` for `checksum` and
     `kind` (with `CHECK (kind IN (0,1,2))`) and `replaces` (`bigint[]`).
   - Aerospike, Couchbase, MongoDB: additive bins / fields; sparse on
     pre-existing records.
   - OpenSearch: additive `PUT _mapping` patch (see Changed above).

### Operational notes

- **Squash is operationally one-way.** Once committed, original migration source
  files are removed. Rollback to v2 against a squashed ledger is unsupported;
  the documented recovery is backup-restore.
- **Mixed-version fleet hazard.** Don't run v2 and v3 against the same ledger
  simultaneously; deploy v3 to all environments before squashing. The
  two-phase fleet readiness gate is the safety net; see ADR-0019 A2.
- **Aerospike re-squash transitivity** is unsupported in v1 — the
  `IntersectWithSquashedAsync` override is a follow-up. Direct
  `Migration_<v>` auto-mark works via `IntersectWithAppliedAsync`.

### Documentation

- [ADR-0019](docs/decisions/0019-migration-squash-replaces-graph.md) — Migration squash via Replaces graph + 19 amendments
- [ADR-0020](docs/decisions/0020-squashes-are-up-only.md) — Squashes are up-only
- [ADR-0021](docs/decisions/0021-migration-record-checksum.md) — MigrationRecord checksum
- [ADR-0022](docs/decisions/0022-script-format-resource-migrations.md) — Script-format resource migrations
- [Upgrade guide v2 → v3](docs/guides/upgrading-from-v2.md)

[3.0.0]: https://github.com/Stillpoint-Software/hyperbee.migrations/releases/tag/v3.0.0
