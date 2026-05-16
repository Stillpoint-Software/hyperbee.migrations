# Changelog

All notable changes to **Hyperbee.Migrations** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [3.0.0] — 2026-05-12 — Migration Squashing (all 5 providers)

This release ships the destructive-model migration squash feature
(per [ADR-0019](docs/decisions/0019-migration-squash-replaces-graph.md)) plus
the universal script-format resource form (per
[ADR-0022](docs/decisions/0022-script-format-resource-migrations.md)) and
the CLI extensibility contract that lets each provider plug into
`hyperbee-migrations squash` (per
[ADR-0024](docs/decisions/0024-migration-host-discovery.md)).
v3.0 is a major release because of two breaking changes around
`IMigrationRecordStore` and provider record-store schemas — both protected by
safe back-compat paths so existing v2 migrations and consumers keep working
unchanged.

The full release-readiness audit
(`docs/research/0009-v3-release-readiness-assessment.md`) is closed: 5
release-blockers + 17 Redesigns + the F-tier deferred items are resolved.
Library tier ships at 1335 unit tests per target framework (.NET 8, 9, 10);
the five Squash provider packages
(`Hyperbee.Migrations.Providers.{Provider}.Squash`) ship as separate
NuGet packages so production deployments do not pay the Testcontainers /
Docker runtime cost.

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
- **`IEphemeralProvisioner` abstraction (+ Couchbase sibling-container variant).**
  The per-provider squash CLI capture orchestrators consume an
  `IEphemeralProvisioner` for container provisioning, decoupling lifecycle
  from the apply/capture pipeline. Each Squash package ships a default
  Testcontainers-backed provisioner; the Couchbase package additionally
  ships `CouchbaseSiblingContainerProvisioner` for the case where the CLI
  itself runs inside a Docker container (CI pipelines, containerized
  operator tooling). Provider provisioners are DI-overridable: the
  default `{Provider}SquashProvider()` ctor wires the default
  Testcontainers impl; a second `(IEphemeralProvisioner)` ctor accepts
  a caller-supplied provisioner for integration tests and third-party
  embeddings. Per ADR-0024 audit Week 2 + Week 4 completion.
- **5 end-to-end SquashProvider integration tests + CLI binary E2E.**
  One SquashProvider integration test per provider (Postgres,
  Aerospike, OpenSearch, MongoDB, Couchbase). Each test loads the
  corresponding sample assembly by path (`Assembly.LoadFrom`),
  discovers `IMigrationHost`, builds a `SquashRequest`, and invokes
  `provider.GenerateAsync` end-to-end. Determinism gate (C12): the
  Postgres variant runs `GenerateAsync` twice against the same sample
  and asserts byte-equal output. Couchbase tagged `LocalOnly` per F-1
  v3.0.1 follow-up (sibling-container model). `CliBinaryEndToEndTests`
  spawns the actual `hyperbee-migrations.exe` child process against
  the Postgres sample + a live Postgres Testcontainer and verifies the
  emitted `.sql`, `.metadata.json`, and `.summary.md` artifacts. All
  pass on net8.0, net9.0, and net10.0.
- **Plugin-style `AssemblyLoadContext` isolation for the CLI binary**
  (ADR-0024 A2). `MigrationAssemblyLoader` now defers shared-type
  identity to the Default ALC (so `IServiceCollection`,
  `IServiceProvider`, etc. type-match across the host/plugin boundary)
  AND probes the NuGet cache directly via the migration assembly's
  `.deps.json` for transitive packages that library projects don't
  carry in their bin folder. `SquashProviderRegistry.Discover`
  supplements the metadata reference closure with a directory scan so
  `<ProjectReference>` packages whose types the migration project
  doesn't directly use (a common shape for the Squash packages)
  still surface through discovery. Without this, the CLI binary
  reported "Discovered providers: <none>" or threw
  `MissingMethodException` at the first cross-ALC call. Set
  `HYPERBEE_CLI_ALC_TRACE=1` to surface every plugin-ALC resolution
  step on stderr when diagnosing an operator's load failure.
- **OpenSearch resource-runner: leaf-filename dashes preserved.**
  `OpenSearchResourceRunner.LoadBodyFromResource` no longer over-
  sanitizes leaf filenames. MSBuild's manifest-name rule converts
  dashes to underscores in folder segments but preserves them in leaf
  filenames; the prior shared-helper sanitization treated all dashes
  uniformly and silently failed to find resources like
  `WITH BODY @bodies/common-mappings-component.json` whose embedded
  manifest entry is `...bodies.common-mappings-component.json`.
- **MongoDB test container: mapped public port (not fixed 28017).**
  `MongoDbTestContainer` previously bound `28017:27017` as a fixed
  host port; the binding got retained by Windows HNS after Docker
  container teardown and surfaced as "port is already allocated" on
  the next test run. Mapped ports are allocated fresh per container
  and avoid the retention path entirely; downstream consumers read
  via `MongoDbTestContainer.ConnectionString` rather than assuming a
  fixed host:port.
- **OpenSearch ISM lifecycle DSL — `DROP POLICY` + `DETACH POLICY FROM INDEX`.**
  Closes the CREATE/APPLY/DETACH/DROP symmetry for ISM policy management
  (R-17 per ADR-0024 audit follow-up). `DROP POLICY <id> [IF EXISTS]` deletes
  the policy via `DELETE _plugins/_ism/policies/<id>` (the cluster rejects
  with 409 if any index still references the policy -- run DETACH first).
  `DETACH POLICY FROM INDEX <pattern> [NO WAIT("<reason>")]` calls
  `POST _plugins/_ism/remove/<pattern>` and reports `updated_indices`
  count; zero-match is treated as an idempotent no-op (informational, not
  failure) so operator teardown scripts stay rerunnable. The legacy
  `_opendistro/_ism` endpoint prefix is honored automatically via the
  existing `IsmEndpointCapability` bootstrap. Both verbs participate in
  the data-op classifier as structural ops (squash-replaceable).

### Changed (back-compat preserved)

- **F-1 partial close -- CouchbaseRunnerTest now runs in CI.** The
  prior LocalOnly tag on `CouchbaseRunnerTest` is removed; the test
  now passes in CI on net8/9/10. Two fixes:
  (1) Pin `couchbase:community-7.6.2` (was: Testcontainers.Couchbase
  default of 7.0.2-community). 7.0.2 had a planner-catalog refresh
  issue for new scopes/collections that surfaced as
  `IndexFailureException 12021 "Scope not found in CB datastore"` on
  CREATE PRIMARY INDEX; 7.6.x ships the fix.
  (2) Bump `retryCount` from 3 to 60 on
  `CouchbaseTestContainer.ConfigureCouchbaseAsync`'s admin-API wait
  (and from 1 to 150 on the bucket-ready wait). `retryCount` is the
  real ceiling -- previously capped the budget at 15 seconds, too
  tight for 7.6.2's ~12s warmup on a CI runner.
- **F-1 v3.0.1 -- 6 Couchbase squash tests remain LocalOnly** for a
  SEPARATE issue: the host-side cluster-map redirect. The Couchbase
  SDK bootstraps via the host-mapped mgmt port, receives a cluster
  map advertising internal Docker addresses (172.17.0.2:11210), tries
  to connect there, and gets "response ended prematurely". The
  `?network=external` query parameter requires
  `setupAlternateAddresses` to be configured on the server -- the
  Testcontainers.Couchbase library default setup callback does NOT
  configure alt-addresses, so host-side SDK connections to an
  isolated Couchbase container fail. The two unblocked paths are
  (a) `CouchbaseSiblingContainerProvisioner` (scheduled for v3.0.1)
  where the test/CLI process runs as a container on the same Docker
  network, or (b) calling `setupAlternateAddresses` on the server.
  Tests gated: `CouchbaseSquashDeterminismTests`,
  `CouchbaseSquashVerificationTests`,
  `CouchbaseSquashProviderIntegrationTests`. Squash correctness
  is byte-tested by 192 Couchbase unit tests in
  `Hyperbee.Migrations.Squash.Tests`.
- **Per-provider integration matrix in CI** (run_tests.yml). Each
  Postgres/Aerospike/MongoDB/OpenSearch/Couchbase job spawns ONLY its
  own provider's containers (via `HYPERBEE_TESTS_PROVIDERS_ONLY`) and
  runs ONLY tests targeting that provider (via `FullyQualifiedName`
  filter). Eliminates the resource pressure that came from one job
  spinning up all 5 provider containers simultaneously on a
  4-CPU / 16 GB GitHub-hosted runner -- the pressure was amplifying
  eventual-consistency races inside Couchbase Server and surfacing
  them as test flakiness. Unit tests run separately because they need
  no containers at all. `MultiProviderHostIntegrationTests` gets its
  own job with Postgres + MongoDB.
- **Couchbase Squash provider package ships** -- the fifth and final
  ISquashProvider implementation for the v3.0 CLI extensibility
  cascade. `CouchbaseSquashProvider` spins ephemeral Couchbase Server
  containers via `Testcontainers.Couchbase`, applies migrations through
  the discovered `IMigrationHost`, and captures via the shared
  `CouchbaseSnapshotCapture` helper. RB-3 fleet readiness probe runs
  N1QL `SELECT RAW MAX(...) FROM <bucket>.<scope>.<collection>` against
  the ledger keyspace; reads bucket/scope/collection from fleet manifest
  topology overrides. `--provider-option bucket-name=<name>` required
  for codegen (the snapshot scope is the bucket).
  `CouchbaseRestApiService` is promoted from internal to public so the
  Squash package can construct it without InternalsVisibleTo coupling.
- **CLI uses collectible `AssemblyLoadContext` for the migration assembly.**
  Previously used `Assembly.LoadFrom`, which loads into the default ALC
  and prevents unload. The collectible ALC (`MigrationAssemblyLoader`)
  resolves transitively-referenced assemblies from the migration project's
  output directory and unloads cleanly when the verb completes -- safe
  for embedding the CLI in long-running hosts. Per ADR-0024 audit
  follow-up (F-3).
- **`CouchbaseRecordStore.IntersectWithAppliedAsync` rewritten to single N1QL
  `USE KEYS` round-trip.** Previously fanned out N parallel `ExistsAsync`
  KV probes -- a 500-migration squash auto-mark opened 500 concurrent
  KV connections, risking throttle / retry storms on smaller clusters.
  The new path issues `SELECT RAW META(d).id FROM <keyspace> d USE KEYS $ids`
  for a primary-key index hit; semantically identical, one round-trip,
  no fan-out. Per ADR-0024 audit follow-up (R-16).
- **MongoDB + OpenSearch Squash provider packages** ship as part of the
  five-provider CLI extensibility cascade. `MongoDBSquashProvider`
  spins ephemeral `mongo:7` containers via `Testcontainers.MongoDb`;
  `OpenSearchSquashProvider` spins ephemeral
  `opensearchproject/opensearch:2.18.0` containers via the generic
  `Testcontainers` package. Both route migration apply through the
  discovered `IMigrationHost` and emit `.statements` script form per
  ADR-0022. RB-3 per-provider readiness probes ship in both (Mongo:
  N1QL-style aggregation over the migration ledger collection;
  OpenSearch: `_search` against the ledger index extracting the max
  version from record_id).
- **CLI is a thin dispatch shell over `ISquashProvider`** (per ADR-0024
  Week 2). The CLI assembly references zero provider packages; per-provider
  CLI implementations are discovered via the migration assembly's reference
  closure. NuGet package presence IS the registration: a migration project
  adds `Hyperbee.Migrations.Providers.{Provider}.Squash` to enable
  `hyperbee-migrations squash --provider {provider}` codegen. v3.0 ships
  `PostgresSquashProvider` and `AerospikeSquashProvider` (Week 2);
  MongoDB / OpenSearch / Couchbase follow in Week 3-4.
- **RB-4 (apply-path reflection) closed.** Provider CLI implementations
  route migration apply through the discovered `IMigrationHost`
  (ADR-0024) -- no more `ApplyToDataSourceAsync` static-method
  reflection convention. The host class is the single supported
  integration point.
- **R-5 (output file extension)**: emitted squash artifact filename uses
  `ISquashProvider.SquashFileExtension` instead of a hardcoded `.sql`.
  Postgres -> `.sql`; the four NoSQL providers -> `.statements` (per
  ADR-0022 script form).
- **R-8 (per-provider source scanner dispatch)**: scanner dispatch routes
  through `ISquashProvider.ScanSource` instead of hardcoding
  `PostgresMigrationSourceScanner.Scan`. Each provider's package exposes
  its own Roslyn scanner with provider-specific data-op heuristics.
- **R-4 (`--remove-originals` default to dry-run)**: the flag now LISTS
  matched files without deleting; actual deletion requires
  `--confirm-delete`. The version-delimited regex prevents false-positive
  matches against names that contain the version as a substring
  (`Squash_1000.cs` does not match when squashing version 100).
- **RB-3 (fleet readiness probe per-provider)**: `FleetReadinessProbe`
  (replaces v1's Postgres-only `FleetReadinessCheck`) dispatches to
  `ISquashProvider.ProbeLastAppliedVersionAsync`. Each provider's
  implementation reads schema / table / namespace / set / index names
  from the fleet manifest's `topology:` overrides; no more hardcoded
  `public.migrations`.
- **`recover from-mid-range` routes through `IMigrationHost`** (no longer
  Postgres-coupled at the CLI tier). Reads `--connection` + `--assembly`,
  activates the discovered host, persists the recovery row via the
  host's `IMigrationRecordStore`. Closes the Week 1 RB-2 "Postgres-only"
  caveat; all 5 providers participate via the host contract.
- **`recover from-mid-range` persists the acknowledgement to the ledger** so
  the runner picks it up on the next invocation, force-marks the mid-range
  squash without running its body, and deletes the recovery row. Previously
  the verb only validated the token and printed an audit summary -- the
  operator had no automated path from "token validated" to "fleet member
  unblocked"; the persisted recovery shipping in v3.0 closes that loop.
  Introduces `MigrationRecordKind.Recovery` (value 3); `RecoveryRecord` helper
  derives the deterministic row id from `(env, squashVersion)` and the
  payload from `(env, squashVersion, missing-versions)`; the runner
  re-verifies the token before consuming the row, so a stale acknowledgement
  from a previous incident with a different missing-set is rejected. v3.0
  CLI persists via Postgres only; the remaining four providers wire through
  the Week 2 `IMigrationHost` discovery contract. Per ADR-0024 audit
  follow-up (RB-2 option a).
- **README quick-start uses the typed `PostgresMigrationRunner` instead of the
  base `MigrationRunner`.** The base type works in single-provider hosts but
  throws in multi-provider hosts (per ADR-0023); the typed runner is the
  documented entry point either way. The README also flags the multi-provider
  pattern with a cross-link to the operator guide. Per ADR-0024 audit
  follow-up (R-11).
- **`squash --scan-source` is required by default; explicit bypass requires
  `--no-scan="<reason>"`** (>= 20 chars). ADR-0019 A5 source scanning is the
  default-deny annotation gate; making it opt-in let operators ship squashes
  that silently elided data ops. The bypass form preserves operator
  autonomy (e.g. cluster-only scenarios with no source) while keeping the
  choice auditable. Per ADR-0024 audit follow-up (R-6).
- **`squash --fleet-manifest` is required by default; explicit bypass requires
  `--no-fleet-manifest="<reason>"`** (>= 20 chars). ADR-0019 A2 two-phase
  fleet readiness gate degraded to a zero-phase no-op when the manifest was
  omitted, hiding mid-range fleet members. The bypass is for solo-environment
  squashes only. Per ADR-0024 audit follow-up (R-7).
- **CLI `ArgParser` whitelists flags per verb** and rejects unknown long-options
  with a did-you-mean suggestion (Damerau-Levenshtein-lite over the
  per-verb known-flag set). A non-boolean flag missing its value
  (e.g. `--connection --range 1-2`) now throws "flag --connection requires
  a value" instead of being silently treated as the string `"true"`.
  Boolean flags (`--remove-originals`, `--regenerate`) retain value-less
  semantics. Per ADR-0024 audit follow-up (R-12).
- **Fleet manifest YAML loader rejects unknown keys** instead of silently
  swallowing them. The previous `IgnoreUnmatchedProperties()` call let typos
  through (`squash-overides` parsed cleanly, `expries: 2026-06-01` produced
  the default 30-day window) -- giving the operator the illusion that the
  manifest was honored. v3.0 throws `MigrationException` wrapping the
  YamlDotNet line/column on any unknown key. Per ADR-0024 audit follow-up (R-13).
- **`RegisterBaseAliases` removes only helper-owned descriptors when a second
  provider registers.** Previously the second-provider flip called
  `RemoveAll<MigrationOptions>` / `RemoveAll<IMigrationRecordStore>` /
  `RemoveAll<MigrationRunner>`, which also wiped any user-supplied
  registrations made before the first `AddXxxMigrations` call -- a
  test-harness footgun where a bespoke fake store registered first vanished
  as soon as a real provider was added. The marker now captures the
  helper-installed `ServiceDescriptor` instances on first registration and
  removes only those on the flip; user-supplied descriptors survive. In
  multi-provider mode the throwing factory still poisons base-type
  resolution by design (operators resolve typed runners) -- R-9's guarantee
  is "your descriptor is not destroyed", not "your descriptor wins base-type
  resolution". Per ADR-0023 amendment F1 + ADR-0024 audit follow-up (R-9).
- **`AddCouchbaseMigrations` validates `BucketName` at options-factory time.**
  Missing or whitespace-only `BucketName` now throws
  `InvalidOperationException` with an operator-friendly message naming the
  field plus the canonical fix (`opts.BucketName = "..."`). Previously the
  failure surfaced as an obscure `NullReferenceException` inside the Couchbase
  SDK on the first `BucketAsync(null)` call. Per ADR-0024 audit follow-up (R-14).
- **`MidRangeSquashException` prints the recovery acknowledgement token** in
  its message and exposes it as `RecoveryToken` on the exception itself, so
  operators have the token on hand during incident response without
  recomputing it. New `MigrationOptions.EnvironmentName` property feeds the
  token derivation; when unset, the token is computed against an `<unset>`
  sentinel and the exception message includes a remediation note. Per
  ADR-0019 A3 + ADR-0024 audit follow-up (R-10).
- **Runner snapshots the applied set once at startup** instead of issuing a
  per-migration `ExistsAsync` round-trip. The loop now consults the in-memory
  snapshot to decide skip-vs-run. On a 500-migration project the runner
  formerly held the fleet lock for 500 sequential round-trips of nothing-but-
  existence-probes; the snapshot collapses that to one bulk realtime read.
  Up direction is correctness-stable (Up only adds records); Down direction
  uses the start-of-run "exists?" answer, which is the correct semantic
  (Down should revert what was present at the start, not chase concurrent
  writers). Per ADR-0024 audit follow-up (R-2). The audit's PA-8 finding
  (count-only optimization of the prior `IsLedgerEmptyAsync` helper) is
  dissolved by this change -- the full applied set is now consumed
  pervasively, so sending all ids is fully justified.
- **`MigrationOptions.LockingEnabled` default flipped to `true`.** Production-grade safety:
  the lazy path (call `AddPostgresMigrations(...)` and run) now acquires the provider's
  native distributed lock. Operators who deliberately want lockless dev/test runs must
  set `opts.LockingEnabled = false` explicitly. Existing consumers who never set the
  property pick up locking automatically on upgrade -- if you have a CI deployment that
  intentionally races (e.g., test fixtures that nuke + recreate the database between
  runs), set the property explicitly. Per ADR-0024 audit follow-up (R-1).
- All five provider record stores override `IntersectWithAppliedAsync` with a
  single-round-trip realtime read (Postgres `WHERE = ANY`, MongoDB
  `find _id $in` with majority+primary, Couchbase parallel `ExistsAsync`,
  Aerospike `BatchGet`, OpenSearch `_mget realtime=true`).
- **All five provider record stores override `IntersectWithSquashedAsync`** for
  transitive squash satisfaction (ADR-0019 A6). Postgres uses
  `WHERE kind=1 AND replaces && ARRAY[...]`; MongoDB uses `find { kind: 1, replaces: { $in: [...] } }`;
  Couchbase uses N1QL `WHERE kind = 1 AND ANY v IN replaces SATISFIES v IN [...] END`;
  OpenSearch uses `_search` with a `terms` filter on `replaces`; Aerospike uses a
  filtered server-side scan on `Kind=Squash` with client-side replaces-array intersection
  (R-15 -- previously the CHANGELOG incorrectly stated this override was deferred;
  the code at `AerospikeRecordStore.cs:309-361` has shipped the implementation from
  v3.0 day one).
- **`IMigrationRecordStore.IntersectWithSquashedAsync` DIM default is now fail-loud.**
  The DIM previously returned an empty set silently, which let mature
  environments that auto-marked an inner squash misclassify as `Fresh` against
  an outer squash. v3.0 throws `NotSupportedException` with a remediation
  message naming the store type the first time the runner reconciles a
  `Kind=Squash` descriptor against a store that hasn't overridden the method.
  v2 stores without any squash usage are untouched -- the runner reaches this
  method only when a squash descriptor is being processed. Per ADR-0024 audit
  follow-up (R-3).
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
  simultaneously; deploy v3 to all environments before squashing. Safety nets:
  the generation-time fleet readiness gate (`MidRangeFleetException`, refuses
  to create a squash that would strand a listed fleet member) and the wired
  apply-time refusal (`MidRangeSquashException`, refuses a mid-range
  environment loudly with `recover from-mid-range` recovery). The deploy-time
  fleet-staleness gate from ADR-0019 A2 was cut as redundant; see ADR-0026.

### Documentation

- [ADR-0019](docs/decisions/0019-migration-squash-replaces-graph.md) — Migration squash via Replaces graph + 19 amendments
- [ADR-0020](docs/decisions/0020-squashes-are-up-only.md) — Squashes are up-only
- [ADR-0021](docs/decisions/0021-migration-record-checksum.md) — MigrationRecord checksum
- [ADR-0022](docs/decisions/0022-script-format-resource-migrations.md) — Script-format resource migrations
- [Upgrade guide v2 → v3](docs/guides/upgrading-from-v2.md)

[3.0.0]: https://github.com/Stillpoint-Software/hyperbee.migrations/releases/tag/v3.0.0
