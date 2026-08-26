# Changelog

All notable changes to **Hyperbee.Migrations** are documented in this file.

The format is based on [Keep a Changelog](https://keepachangelog.com/en/1.1.0/),
and this project adheres to [Semantic Versioning](https://semver.org/spec/v2.0.0.html).

## [Unreleased]

### Added

- **OpenSearch: `configureSettings` escape hatch on both client factories.**
  `AddOpenSearchClient` and `AddOpenSearchAwsClient` (and both `IConfiguration` overloads)
  gain an `Action<ConnectionSettings>` parameter, applied **last** — after the endpoint
  and authentication wiring — so a consumer can override anything the library set.

  **Purely additive: source- and binary-compatible with 3.1.x.** This ships as four new
  overloads rather than as an appended optional parameter. Appending one would have been
  source-compatible but not binary-compatible: it changes the existing method's signature,
  so the 3.1.x entry point stops existing and any assembly compiled against it throws
  `MissingMethodException` until recompiled. A minor release must not do that. The four
  3.1.x signatures are preserved exactly and forward to the new ones, and a reflection
  test pins all eight so the guarantee cannot regress silently.

  OpenSearch is the only provider whose client the library constructs; the other four
  resolve a consumer-registered client and already have full control. Until now the only
  way to reach `ConnectionSettings` — for `RequestTimeout`, `MaximumRetries`,
  `EnableHttpCompression`, a proxy, `ServerCertificateValidationCallback` on a
  self-signed development cluster, `DisableDirectStreaming` while debugging, or a
  `DefaultMappingFor` over your *own* document types — was to stop calling the factory
  and hand-roll the registration, forking the auth-mode switch, the AWS-endpoint
  loud-fail, and the mutual-exclusion guard along with it.

  Because the hook makes it reachable, registration now validates one ledger property
  through the configured inferrer and fails with a pointed message if field-name
  inference is no longer camelCase — the ledger index carries a `strict` camelCase
  mapping that such a change would otherwise break at first write with an opaque
  `strict_dynamic_mapping_exception`. Validation runs only when a hook is supplied.

  This is *not* the remedy for the OpenSearch `_mget` defect below; see ADR-0029.

### Fixed

- **OpenSearch: every migration run failed on a stock client (regression, v3.0.0–v3.1.0).**
  `IntersectWithAppliedAsync` issues one `_mget`, which carries an index in the URL *and*
  in each body entry. The URL index was set explicitly from
  `OpenSearchMigrationOptions.LedgerIndex`, but the body entries were left to resolve via
  `IndexName.From<OpenSearchMigrationRecord>()` — CLR-type inference that reads
  `ConnectionSettings.DefaultMappingFor<T>()` / `DefaultIndex()`. Neither
  `AddOpenSearchClient` nor `AddOpenSearchAwsClient` configures either, so request
  serialization threw `Index name is null for the given type and no default index is set`
  **before any byte reached the wire**. `MigrationRunner.RunAsync` calls
  `IntersectWithAppliedAsync` unconditionally whenever at least one migration is
  discovered, so this broke every OpenSearch run — including through the library's own
  `Hyperbee.MigrationRunner.OpenSearch` and the CLI. The `_mget` now sets the ledger index
  per operation. No API change; no consumer action required. Consumers who worked around
  this by declaring their own `DefaultMappingFor<OpenSearchMigrationRecord>` or by forking
  a client factory can remove both.

  Affects `Hyperbee.Migrations.Providers.OpenSearch` and
  `Hyperbee.Migrations.Providers.OpenSearch.Aws` 3.0.0 and 3.1.0. Not AWS-specific.

- **MongoDB: squash reconciliation silently covered nothing (regression, v3.0.0–v3.1.0).**
  `IntersectWithSquashedAsync` built its filter half typed and half literal — a typed
  expression for `Kind` (which renders through the BSON class map, producing `Kind`) and
  a raw string for `"replaces"` (which renders verbatim). The driver's default element
  name is the member name, so the writer stores `Replaces`; the rendered filter
  `{ "Kind": 1, "replaces": { "$in": [...] } }` could never match. The method returned an
  empty set for every input, so a squash was never recognized as covering its replaced
  versions and **those migrations re-ran**. The failure was silent because an empty set
  is also the correct answer whenever nothing is squashed. Both terms are now typed.

  No wire change: the fix corrects the query to match what the writer already produces.
  Deliberately *not* fixed by pinning element names with `[BsonElement]` or a registered
  class map — pinning would orphan any deployment whose consumer registered a global
  naming convention.

- **Couchbase: ledger documents now serialize through a pinned serializer.**
  `IntersectWithSquashedAsync` must name ledger fields as text (`m.kind`, `m.replaces`)
  because N1QL has no typed field reference, but ledger documents serialized through
  `ClusterOptions.Serializer` — consumer-owned configuration. A consumer registering a
  System.Text.Json serializer, or a Newtonsoft one without the camelCase resolver, wrote
  `Kind`/`Replaces` and the squash query silently matched nothing, with the same
  re-run consequence as the MongoDB defect above. Ledger KV reads and writes now use a
  library-owned `DefaultSerializer` in its default configuration.

  **Behavior note:** this is byte-for-byte identical for consumers on the stock Couchbase
  serializer, which is the default. Consumers who set a *custom* `ClusterOptions.Serializer`
  will see new ledger rows written in the canonical camelCase shape. Existing rows stay
  readable by key (`ExistsAsync`, `IntersectWithAppliedAsync` are key-based and unaffected);
  `ReadAsync` on a row written under a custom shape may return null `RunOn`/`Checksum`,
  which can cause one extra cron evaluation. Squash reconciliation, which was broken for
  this configuration, starts working.

### Decisions

- [ADR-0029](docs/decisions/0029-ledger-wire-contract-is-library-owned.md) — the ledger's
  wire contract is library-owned, never inherited from consumer-configured client
  inference. Rule 1: ledger requests carry their target explicitly. Rule 2: every
  reference to a ledger field routes through the same serialization path as the writer.
  Rule 3: a wire-test tier between mock-tier and container-tier.
- [ADR-0030](docs/decisions/0030-connection-settings-escape-hatch.md) — `ConnectionSettings`
  escape hatch on the OpenSearch client factories

### Tests

- New wire-shape test tier for the OpenSearch record store
  (`OpenSearchLedgerWireTests`): the real client and real serializer over an
  `InMemoryConnection`, so request construction and serialization actually execute with
  only the socket faked. This is the tier that was missing — the existing provider unit
  tests substitute `IOpenSearchClient` (a substitute never serializes) and the
  container-backed tests are compile-gated and excluded from CI. Includes a generalized
  probe asserting that *no* ledger operation depends on type→index inference, which
  guards the next regression rather than only this one.
- Two OpenSearch record-store integration tests for `IntersectWithAppliedAsync`, one of
  which drives the client produced by `services.AddOpenSearchClient(...)` — previously
  nothing exercised the shipped registration path end-to-end.
- `MongoDBLedgerWireTests` and `CouchbaseLedgerWireTests` — render the real queries the
  record stores issue and compare them against the real serializer output, with no mock
  and no container. The MongoDB assertions are convention-*independent* on purpose: they
  assert that the field a query asks for is the field the writer wrote, not that a field
  has a particular casing, so they still pass for a consumer who registered a camelCase
  convention. Pinning a literal casing would have made the tests agree with the bug.
- `MongoDBRecordStoreIntegrationTests` — squash and applied reconciliation against a real
  MongoDB. `IntersectWithSquashedAsync` previously had no coverage at any tier.
- `OpenSearchConnectionSettingsHookTests` — the hook reaches the resolved client on both
  registration paths, runs after auth wiring (a consumer override wins), stays optional,
  and loud-fails on a ledger-breaking field-name inferrer. Includes an ADR-0029
  cross-check that a consumer `DefaultIndex` does not become the ledger's index.

## [3.1.0] — 2026-05-29 — Interruption-Safe Ledger (crash / SIGTERM safety)

A migration interrupted mid-run — a SIGTERM from an orchestrator (Argo Rollouts
pre-init, a Kubernetes Job timeout), a SIGKILL after the grace period, or node
death — is now safe to restart. Previously the ledger row was written only after
the migration body completed, so an interruption left no record and the next run
re-ran the whole migration, double-applying non-idempotent data operations. v3.1
adds a two-tier safety model. Backward compatible: existing migrations and custom
`IMigrationRecordStore` implementations keep working unchanged.

### Highlights

- **Tier 1 — in-flight sentinel (all providers, fail-closed).** The runner writes a
  durable `Kind=InProgress` sentinel row before a migration body and deletes it after
  the journal write. On restart, a leftover sentinel means the migration was
  interrupted: `[DataMigration]` and unannotated migrations **fail closed**
  (`MigrationInterruptedException`) until an operator sets `ForceResume`;
  `[StructuralOnly]` migrations reap the sentinel and re-run (idempotent replay); cron
  and non-journaled migrations are exempt. Mirrors Flyway's failed-state /
  golang-migrate's dirty-flag discipline.
- **Tier 2 — transaction-scoped apply (Postgres, fail-clean).** Where the engine can
  wrap a migration's body and its journal write in one transaction, an interruption
  rolls back both atomically — no partial data, no ledger row, no sentinel, no operator
  step. Shipped for **Postgres** (a shared `NpgsqlConnection`/transaction enrolled by
  both the resource runner and the record store). The capability seam lets any provider
  opt in.
- **Per-provider tiers.** Postgres = Tier 2 (fail-clean). MongoDB, Couchbase, Aerospike,
  OpenSearch = Tier 1 (fail-closed) — for three of them that is the only engine-correct
  option (no transaction can wrap DDL-heavy migrations); the MongoDB seam is ready for a
  future replica-set opt-in. Tier 1 is the universal net for every provider.
- **Postgres ledger constraint widened.** The `kind` CHECK constraint now accepts
  `Recovery (3)` and `InProgress (4)`, with an idempotent in-place upgrade for existing
  deployments.
- **MongoDB `CreateCollection` is now idempotent** (guard-on-exists), matching the
  Couchbase/Aerospike create style, so a structural replay is a no-op.

### New public surface

- `MigrationRecordKind.InProgress`, `InProgressRecord`, `MigrationInterruptedException`
- `MigrationOptions.ForceResume` (promoted from the OpenSearch provider options)
- `ITransactionalRecordStore`, `IMigrationTransactionScope`, `MigrationContext.AmbientTransaction`

### Decisions

- [ADR-0027](docs/decisions/0027-interruption-safe-ledger.md) — Tier-1 marker-before-work
- [ADR-0028](docs/decisions/0028-transaction-scoped-apply.md) — Tier-2 transaction-scoped apply

### Tests

428 core + 889 squash unit tests (per target framework); 7 Postgres integration tests
including atomic body+journal rollback/commit and interrupt → fail-clean restart.

## [3.0.0] — 2026-05-12 — Migration Squashing (all 5 providers)

This release ships the destructive-model migration squash feature plus
the universal script-format resource form and the CLI extensibility
contract that lets each provider plug into `hyperbee-migrations squash`.
v3.0 is a major release because of two breaking changes around
`IMigrationRecordStore` and provider record-store schemas — both protected by
safe back-compat paths so existing v2 migrations and consumers keep working
unchanged.

The full release-readiness audit is closed: 5 release-blockers + 17
Redesigns + the F-tier deferred items are resolved.
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
  Couchbase `HybridStrategy`. Shipping all five together is deliberate:
  the strategy abstraction is only proven correct by being implemented
  against the full provider matrix. **Outcome:** the 5-interface contract
  held for all five providers without modification.
- **Universal `.pql` script form** — the recommended shape for resource
  migrations. All four NoSQL providers accept multi-statement `.pql`
  (*Provider Query Language*) files with `--`/`//`/`/* */` comments and
  `;` terminators; Postgres accepts its native `.sql` and `.pql`. The
  legacy `.statements.json` JSON-array form continues to apply unchanged
  (backward-compatible, not recommended for new work).
- **Reversible migrations via `.down.pql`** (OpenSearch) — pair a
  `<name>.pql` Up script with a sibling `<name>.down.pql` Down script;
  the down script is dispatched in written order (author-owned teardown),
  preserving the R-19 partial-rollback ledger semantics. The legacy
  per-entry `rollback` field in `.statements.json` (auto-reverse) remains
  supported. Missing `.down.pql` ⇒ loud `RollbackNotSupportedException`
  before any mutation. Squashes are up-only, so generated squashes carry
  no down script.
- **Fleet readiness gate** — the squash CLI refuses generation while any
  registered fleet member is mid-range (`MidRangeFleetException`), and the
  runner refuses a mid-range environment loudly at apply time
  (`MidRangeSquashException`, with a `recover from-mid-range` escape hatch).
  A deploy-time fleet-staleness gate was specified during design but cut
  as redundant before ship (the apply-time refusal already makes the
  dangerous case loud and recoverable).
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
  opaque painless preservation,
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
- `[DataMigration]` and `[StructuralOnly]` attributes -- the
  Roslyn-based source scanners refuse squash generation if a migration class
  matches the data-op heuristic without an explicit annotation.
- Generation-time fleet gate types: `SquashMetadata`, `SquashFleetGate`
  (`EnsureGenerable`), `MidRangeFleetException`. (The deploy-time half --
  `EnsureDeployable`, `StaleFleetMemberException`,
  `UnregisteredEnvironmentException` -- was cut before ship.)
- `RecoveryAcknowledgement` — deterministic 12-char token for the
  `recover from-mid-range` escape hatch.
- **Per-provider `MigrationRunner` subclasses** (`PostgresMigrationRunner`,
  `MongoDBMigrationRunner`, `CouchbaseMigrationRunner`,
  `OpenSearchMigrationRunner`, `AerospikeMigrationRunner`). Each provides
  a unique DI handle so multi-provider hosts can resolve and run each
  provider's runner independently. See the multi-provider
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
  embeddings.
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
 . `MigrationAssemblyLoader` now defers shared-type
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
  Closes the CREATE/APPLY/DETACH/DROP symmetry for ISM policy management. `DROP POLICY <id> [IF EXISTS]` deletes
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
  for embedding the CLI in long-running hosts.
- **`CouchbaseRecordStore.IntersectWithAppliedAsync` rewritten to single N1QL
  `USE KEYS` round-trip.** Previously fanned out N parallel `ExistsAsync`
  KV probes -- a 500-migration squash auto-mark opened 500 concurrent
  KV connections, risking throttle / retry storms on smaller clusters.
  The new path issues `SELECT RAW META(d).id FROM <keyspace> d USE KEYS $ids`
  for a primary-key index hit; semantically identical, one round-trip,
  no fan-out.
- **MongoDB + OpenSearch Squash provider packages** ship as part of the
  five-provider CLI extensibility cascade. `MongoDBSquashProvider`
  spins ephemeral `mongo:7` containers via `Testcontainers.MongoDb`;
  `OpenSearchSquashProvider` spins ephemeral
  `opensearchproject/opensearch:2.18.0` containers via the generic
  `Testcontainers` package. Both route migration apply through the
  discovered `IMigrationHost` and emit `.pql` script form.
  RB-3 per-provider readiness probes ship in both (Mongo:
  N1QL-style aggregation over the migration ledger collection;
  OpenSearch: `_search` against the ledger index extracting the max
  version from record_id).
- **CLI is a thin dispatch shell over `ISquashProvider`.** The CLI
  assembly references zero provider packages; per-provider
  CLI implementations are discovered via the migration assembly's reference
  closure. NuGet package presence IS the registration: a migration project
  adds `Hyperbee.Migrations.Providers.{Provider}.Squash` to enable
  `hyperbee-migrations squash --provider {provider}` codegen. v3.0 ships
  `PostgresSquashProvider` and `AerospikeSquashProvider` (Week 2);
  MongoDB / OpenSearch / Couchbase follow in Week 3-4.
- **RB-4 (apply-path reflection) closed.** Provider CLI implementations
  route migration apply through the discovered `IMigrationHost`
  -- no more `ApplyToDataSourceAsync` static-method
  reflection convention. The host class is the single supported
  integration point.
- **R-5 (output file extension)**: emitted squash artifact filename uses
  `ISquashProvider.SquashFileExtension` instead of a hardcoded `.sql`.
  Postgres -> `.sql`; the four NoSQL providers -> `.pql`
  (the recommended script form).
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
  the Week 2 `IMigrationHost` discovery contract.
- **README quick-start uses the typed `PostgresMigrationRunner` instead of the
  base `MigrationRunner`.** The base type works in single-provider hosts but
  throws in multi-provider hosts; the typed runner is the
  documented entry point either way. The README also flags the multi-provider
  pattern with a cross-link to the operator guide.
- **`squash --scan-source` is required by default; explicit bypass requires
  `--no-scan="<reason>"`** (>= 20 chars). Source scanning is the
  default-deny annotation gate; making it opt-in let operators ship squashes
  that silently elided data ops. The bypass form preserves operator
  autonomy (e.g. cluster-only scenarios with no source) while keeping the
  choice auditable.
- **`squash --fleet-manifest` is required by default; explicit bypass requires
  `--no-fleet-manifest="<reason>"`** (>= 20 chars). The fleet readiness gate degraded to a zero-phase no-op when the manifest was
  omitted, hiding mid-range fleet members. The bypass is for solo-environment
  squashes only.
- **CLI `ArgParser` whitelists flags per verb** and rejects unknown long-options
  with a did-you-mean suggestion (Damerau-Levenshtein-lite over the
  per-verb known-flag set). A non-boolean flag missing its value
  (e.g. `--connection --range 1-2`) now throws "flag --connection requires
  a value" instead of being silently treated as the string `"true"`.
  Boolean flags (`--remove-originals`, `--regenerate`) retain value-less
  semantics.
- **Fleet manifest YAML loader rejects unknown keys** instead of silently
  swallowing them. The previous `IgnoreUnmatchedProperties()` call let typos
  through (`squash-overides` parsed cleanly, `expries: 2026-06-01` produced
  the default 30-day window) -- giving the operator the illusion that the
  manifest was honored. v3.0 throws `MigrationException` wrapping the
  YamlDotNet line/column on any unknown key.
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
  resolution".
- **`AddCouchbaseMigrations` validates `BucketName` at options-factory time.**
  Missing or whitespace-only `BucketName` now throws
  `InvalidOperationException` with an operator-friendly message naming the
  field plus the canonical fix (`opts.BucketName = "..."`). Previously the
  failure surfaced as an obscure `NullReferenceException` inside the Couchbase
  SDK on the first `BucketAsync(null)` call.
- **`MidRangeSquashException` prints the recovery acknowledgement token** in
  its message and exposes it as `RecoveryToken` on the exception itself, so
  operators have the token on hand during incident response without
  recomputing it. New `MigrationOptions.EnvironmentName` property feeds the
  token derivation; when unset, the token is computed against an `<unset>`
  sentinel and the exception message includes a remediation note. Per
- **Runner snapshots the applied set once at startup** instead of issuing a
  per-migration `ExistsAsync` round-trip. The loop now consults the in-memory
  snapshot to decide skip-vs-run. On a 500-migration project the runner
  formerly held the fleet lock for 500 sequential round-trips of nothing-but-
  existence-probes; the snapshot collapses that to one bulk realtime read.
  Up direction is correctness-stable (Up only adds records); Down direction
  uses the start-of-run "exists?" answer, which is the correct semantic
  (Down should revert what was present at the start, not chase concurrent
  writers). The audit's PA-8 finding
  (count-only optimization of the prior `IsLedgerEmptyAsync` helper) is
  dissolved by this change -- the full applied set is now consumed
  pervasively, so sending all ids is fully justified.
- **`MigrationOptions.LockingEnabled` default flipped to `true`.** Production-grade safety:
  the lazy path (call `AddPostgresMigrations(...)` and run) now acquires the provider's
  native distributed lock. Operators who deliberately want lockless dev/test runs must
  set `opts.LockingEnabled = false` explicitly. Existing consumers who never set the
  property pick up locking automatically on upgrade -- if you have a CI deployment that
  intentionally races (e.g., test fixtures that nuke + recreate the database between
  runs), set the property explicitly.
- All five provider record stores override `IntersectWithAppliedAsync` with a
  single-round-trip realtime read (Postgres `WHERE = ANY`, MongoDB
  `find _id $in` with majority+primary, Couchbase parallel `ExistsAsync`,
  Aerospike `BatchGet`, OpenSearch `_mget realtime=true`).
- **All five provider record stores override `IntersectWithSquashedAsync`** for
  transitive squash satisfaction. Postgres uses
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
  method only when a squash descriptor is being processed.
- OpenSearch ledger index strict mapping extended with `kind` (byte) and
  `replaces` (long[]) fields. Existing v2-era indices receive an additive
  `PUT _mapping` patch on bootstrap, idempotent and IAM-aware.
- `MigrationDescriptor` (previously a private record on `MigrationRunner`)
  is now a public core type so squash strategies can consume it.
- **`MigrationRunner` accepts `ILoggerFactory` in addition to
  `ILogger<MigrationRunner>`.** The new
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
  hosts are unaffected. (See the multi-provider hosts
  operator guide at `docs/site/multi-provider-hosts.md`.)

### Pre-ship hardening

The v3.0 pre-ship audit closed the following before release. No behavior
change for correctly-configured consumers; these are robustness,
doc-accuracy, and dead-code items.

- **Couchbase GSI rebalance flake fixed at the root.** Index DDL during an
  index-service rebalance ("rebalance in progress") was retried blindly,
  leaving create-after-create and create-after-drop races. The provider now
  exposes a single `CouchbaseIndexRetry` helper:
  `WithRebalanceRetryAsync` (one 60x3s backstop -- single source of truth for
  the retry budget), `WaitForIndexReadyAsync` (delegates to the SDK
  `WatchIndexesAsync`; unnamed primary watched as `#primary`), and
  `WaitForIndexDroppedAsync` (polls `GetAllIndexesAsync` until the index is
  gone). `CouchbaseRecordStore` and `CouchbaseResourceRunner` both route
  through it; the prior triplicated rebalance-retry loop is consolidated.
  A second axis was closed for the squash integration fixtures: cluster
  idle (KV `rebalance` task) is necessary but not sufficient for GSI DDL --
  the index service runs its own initial topology placement on a fresh
  cluster that the cluster-tasks endpoint does not surface. The isolated
  Couchbase test container now performs a bounded indexer-DDL warmup
  (sentinel create/ready/drop) before any test body, so the index service
  is provably past initial placement before the first real CREATE INDEX.
  The CI per-provider matrix is green 23/23 with the 6 previously-LocalOnly
  Couchbase squash tests now running in CI via configured alternate addresses.
- **Aerospike lock-disabled readiness gate.** `AerospikeRecordStore.InitializeAsync`
  previously only checked `_client.Connected`; on the lock-disabled path the
  first ledger read could hit a not-yet-warm cluster. It now runs a sentinel
  probe filtered by the existing `IsTransientClusterError` predicate with a
  60s bound, throwing a clear `MigrationException` on timeout.
- **Documentation corrections.** `docs/site/squashing-migrations.md`: removed
  the stale `ApplyToDataSourceAsync` apply-path (the CLI applies via the
  discovered `IMigrationHost`), corrected the CLI invocation example and the
  `recover from-mid-range` flag list to match `RecoverVerb`, and fixed the
  Aerospike `IntersectWithSquashedAsync` transitivity caveat. `CHANGELOG.md`
  internal contradiction on the Aerospike override reconciled (R-15 shipped).
  `docs/site/supported-versions.md`: non-ASCII em-dashes replaced (just-the-docs
  ASCII constraint). Top-level `README.md`: added a "What's new in v3.0" section.
- **Dead code removed.** `ICouchbaseRestApiService.GetNodeStatusesAsync`
  (+impl, +`RestApi.GetNodeStatuses`), `GetClusterInfoAsync` (+impl; the
  still-used `RestApi.GetClusterInfo` is retained), and the no-timeout
  `WaitUntilBucketReadyAsync` overload had no callers and were deleted.
- **Two confirm-intent decisions recorded.** `NullSquashStrategy` is retained
  as a public extension point (no first-party provider uses it); the
  deploy-time fleet gate (`SquashFleetGate.EnsureDeployable` +
  `StaleFleetMemberException` + `UnregisteredEnvironmentException`) was cut
  as redundant before ship.
- **Accidental-drift cleanup.** MongoDB / Postgres `appsettings.json` Serilog
  `Override` key corrected from the copy-pasted `"Couchbase"` to `"MongoDB"` /
  `"Npgsql"`. Couchbase runner DI helpers renamed to the `Add{Provider}Provider`
  / `Add{Provider}Migrations` convention used by the other four. Stale test
  namespace `Hyperbee.Migrations.Tests.Squash.Cli` -> `.Squash`.

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
  fleet-staleness gate was cut as redundant before ship.

### Documentation

- [Squashing migrations](docs/site/squashing-migrations.md) — the full operator guide
- [Resource migrations](docs/site/resource-migrations.md) — the `.pql` script form + `.down.pql` reversibility
- [Multi-provider hosts](docs/site/multi-provider-hosts.md)
- [Upgrade guide v2 → v3](docs/guides/upgrading-from-v2.md)

[3.1.0]: https://github.com/Stillpoint-Software/hyperbee.migrations/releases/tag/v3.1.0
[3.0.0]: https://github.com/Stillpoint-Software/hyperbee.migrations/releases/tag/v3.0.0
