# Migration Squashing -- Per-Provider Implementations (v3.0 Release Gate)

**Status:** Draft
**Date:** 2026-05-11
**Parent requirements:** [migration-squashing.md](migration-squashing.md) (the broader feature spec)
**Constraining ADRs:** ADR-0019 (Replaces graph + amendments through A19), ADR-0020 (squashes are up-only -- Accepted), ADR-0021 (record checksum -- Accepted), ADR-0022 (script-format resources -- Accepted)
**Locked rules:** [feedback_squash_all_providers_v1.md](../../../../Users/bfarm/.claude/projects/c--Development-hyperbee-migrations/memory/feedback_squash_all_providers_v1.md) (v3.0 ships when all 5 providers have working squash codegen, no exceptions)

## Problem

`Hyperbee.Migrations` v3.0 has a locked release rule: ship squash codegen for all 5 providers, or do not ship. The shared squash contract surface (`src/Hyperbee.Migrations/Squash/`, 19 files: `ISquashStrategy`, `ITopologySignature`, `IDataOpClassifier`, `ISnapshotCanonicalizer`, `ISquashVerifier`, plus supporting types) is shipped. The Postgres reference implementation (`src/Hyperbee.Migrations.Providers.Postgres/Squash/`, 10 files, ~1700 LOC) is shipped. The other four providers' `Squash/` directories are empty.

**The cost of inaction** is that v3.0 cannot ship. The strategy abstraction has been pressure-tested against exactly one implementation; the all-providers-or-nothing rule exists precisely because shipping one validates one implementation, not the abstraction. Operators of the four un-shipped providers cannot compact long migration histories; their fresh-environment provisioning continues to grow linearly.

The shape of the work is uniform per provider (six component types each), but the canonicalization complexity varies materially: Aerospike (Low), OpenSearch (High), MongoDB (Medium-High), Couchbase (High). Implementation order revised 2026-05-11 to Aerospike -> **OpenSearch** -> MongoDB -> Couchbase: Aerospike first hardens the contract against a low-risk case; OpenSearch second pressure-tests the contract against the hardest provider while only one prior implementation has to recompile if a contract gap surfaces; MongoDB and Couchbase inherit the pattern.

## Requirements

### Theme 1: Per-provider strategy implementation

Each of the four providers must ship a concrete `ISquashStrategy` implementation plus its supporting types, in the same shape as the Postgres reference. R-P1 through R-P4 capture this per provider.

#### R-P1: Aerospike ships `InfoSnapshotStrategy` + supporting types

**Actor:** Hyperbee.Migrations runtime -- invoked when `dotnet hyperbee-migrations squash --provider aerospike --range N..M` runs
**Intention:**
- *Immediate:* The squash CLI produces a deterministic `Squash_M.statements` file for an Aerospike migration range
- *Outcome:* Aerospike operators can compact long migration histories; the strategy abstraction is validated against a low-canonicalization-risk provider before MongoDB/Couchbase/OpenSearch press on it
- *Metric:* `Hyperbee.Migrations.Providers.Aerospike.Squash` namespace contains `AerospikeTopologySignature`, `AerospikeDataOpClassifier`, `AerospikeStatementClassifier`, `AerospikeSnapshotCanonicalizer`, `InfoSnapshotStrategy`, `AerospikeSquashVerifier` and the CLI produces a passing snapshot-A vs snapshot-B byte-equal verification round

**Friction today:**
- Current: `src/Hyperbee.Migrations.Providers.Aerospike/Squash/` does not exist
- Failure mode: CLI invoked with `--provider aerospike` falls through to no strategy registration (or to the now-obsolete `NullSquashStrategy`) and refuses with no path forward
- Frequency: every Aerospike operator who reaches the painful migration count

**Given:** An Aerospike cluster has migrations 1000..2000 applied; the operator runs `dotnet hyperbee-migrations squash --provider aerospike --range 1000..2000`
**When:** The strategy executes its generate -> verify -> emit loop
**Then:**
- `AerospikeTopologySignature` captures namespace name, namespace memory/storage settings, default-TTL, NSUP-period, replication-factor, server major.minor
- `InfoSnapshotStrategy` captures namespace + sets + secondary indexes via `Info.Request(node, "namespaces;sets;sindex;udf-list")`
- `AerospikeSnapshotCanonicalizer.Canonicalize` strips ephemeral fields (current memory usage, current record count, tend-time stamps), sorts sets / indexes by name, normalizes line endings
- `AerospikeSnapshotCanonicalizer.EmitScript` emits a `.statements` file using AQL-flavored syntax (`CREATE SET`, `CREATE INDEX WAIT ... STRING/NUMERIC/GEO2DSPHERE`, `CREATE UDF`)
- `AerospikeDataOpClassifier.Classify` scans the migration source via Roslyn; `_client.Put(...)`, `_client.Delete(...)`, `_client.Operate(...)` -> `IsDataOp = true`; `Info.Request` and admin operations -> `IsDataOp = false`; unknown call sites -> `IsUnclassified = true`
- `AerospikeStatementClassifier` parses resource files (`.statements` / `.statements.json`) and classifies each statement; `CREATE INDEX` / `CREATE SET` / `DROP INDEX` are structural, `INSERT INTO` / `DELETE FROM` are data
- `AerospikeSquashVerifier.VerifyAsync` spins two ephemeral Aerospike containers via Testcontainers, applies 1000..2000 to A, applies the generated squash to B, captures canonical snapshots from both, asserts byte-equality

**Otherwise:**
- If the migration range contains unclassified data ops, the generator surfaces `SquashGenerationResult.Failed` naming the migration and call site
- If verification fails (snapshot A != snapshot B), `VerificationResult.Failed` carries the diff summary; container teardown is automatic unless `--keep-failed-container` is set

**Depends on:** parent R-01 (checksum), R-02 (Replaces), R-04 (auto-mark), R-13 (verification round)
**Priority:** Must -- v3.0 release gate
**Confidence:** High (Low canonicalization risk per ADR-0019 A7)

#### R-P2: MongoDB ships `IntrospectionSnapshotStrategy` + supporting types

**Actor:** Hyperbee.Migrations runtime -- invoked when `dotnet hyperbee-migrations squash --provider mongodb --range N..M` runs
**Intention:**
- *Immediate:* The squash CLI produces a deterministic `Squash_M.statements` file for a MongoDB migration range
- *Outcome:* MongoDB operators get squash; the strategy contract is pressure-tested against a Medium-High canonicalization-risk provider
- *Metric:* `Hyperbee.Migrations.Providers.MongoDB.Squash` namespace contains the six component types; CLI produces a passing verification round

**Friction today:** Same as R-P1 against MongoDB.

**Given:** A MongoDB cluster has migrations 1000..2000 applied; the operator runs the squash CLI
**When:** The strategy executes its generate -> verify -> emit loop
**Then:**
- `MongoDBTopologySignature` captures server major.minor, feature compatibility version (FCV), replica-set vs standalone, default read/write concern, storage engine
- `IntrospectionSnapshotStrategy` enumerates collections via `db.runCommand({listCollections})`, indexes via `db.getCollection(...).getIndexes()` per collection, schema validators via the collection's `options.validator`, time-series options where applicable
- `MongoDBSnapshotCanonicalizer.Canonicalize` strips index `v` field (server-version-dependent), sorts collections + indexes by name, normalizes BSON-vs-JSON representation to JSON
- `MongoDBSnapshotCanonicalizer.EmitScript` emits a `.statements` file using the SQL-flavored DSL (`CREATE COLLECTION db.col`, `CREATE [UNIQUE] INDEX name ON db.col(field1, field2)`, etc.)
- `MongoDBDataOpClassifier` classifies `Collection.Insert*`, `Collection.Update*`, `Collection.Delete*` as data ops; index/collection management as structural
- `MongoDBStatementClassifier` parses `.statements` / `.statements.json` and classifies each statement
- `MongoDBSquashVerifier` runs the two-container byte-equal verification round via Testcontainers

**Otherwise:** Same shape as R-P1.

**Depends on:** R-P1 (the strategy contract should be stable by the time MongoDB starts; if R-P1 surfaces a contract gap, it must be amended in ADR-0019 before R-P2 begins)
**Priority:** Must -- v3.0 release gate
**Confidence:** Medium (BSON canonicalization is tricky; index `v` field stripping is per-server-version)

#### R-P3: Couchbase ships `HybridStrategy` + supporting types

**Actor:** Hyperbee.Migrations runtime -- invoked when `dotnet hyperbee-migrations squash --provider couchbase --range N..M` runs
**Intention:**
- *Immediate:* The squash CLI produces a deterministic `Squash_M.statements` file for a Couchbase migration range
- *Outcome:* Couchbase operators get squash; the strategy contract is pressure-tested against a High canonicalization-risk provider
- *Metric:* `Hyperbee.Migrations.Providers.Couchbase.Squash` namespace contains the six component types; CLI produces a passing verification round

**Friction today:** Same as R-P1 against Couchbase.

**Given:** A Couchbase cluster has migrations 1000..2000 applied; the operator runs the squash CLI
**When:** The strategy executes its generate -> verify -> emit loop
**Then:**
- `CouchbaseTopologySignature` captures server major.minor, edition (CE vs EE), index service GSI vs N1QL-built-in, bucket type, replica count, memory quota
- `HybridStrategy` combines two sources: (a) `system:keyspaces` + `system:indexes` queries for collections and indexes; (b) bucket/scope settings via the Management API
- `CouchbaseSnapshotCanonicalizer.Canonicalize` strips ephemeral fields (current docs count, current item count, last-rebalance timestamp), sorts keyspaces + indexes deterministically, normalizes N1QL whitespace
- `CouchbaseSnapshotCanonicalizer.EmitScript` emits a `.statements` file using N1QL-flavored DSL (`CREATE BUCKET`, `CREATE SCOPE`, `CREATE COLLECTION`, `CREATE INDEX ... USING GSI WITH {...}`, `BUILD INDEX`)
- `CouchbaseDataOpClassifier` classifies cluster query/upsert/delete as data ops; bucket/scope/collection/index management as structural
- `CouchbaseStatementClassifier` parses `.statements` / `.statements.json` per the N1QL parser; classifies parameterized inserts/updates as data ops
- `CouchbaseSquashVerifier` runs the two-container byte-equal verification round; verifier accounts for index-build async behavior (deferred index commits not visible until BUILD INDEX is dispatched)

**Otherwise:** Same shape as R-P1; additionally, indexes in `deferred` state at snapshot time are surfaced as a non-fatal warning -- the squash output should always emit them with the deferred-build hint.

**Depends on:** R-P1, R-P2 (contract amendments from earlier providers must land first)
**Priority:** Must -- v3.0 release gate
**Confidence:** Low (canonicalization across CE/EE differences, parameterized N1QL, deferred-build indexes; expect contract pressure)

#### R-P4: OpenSearch ships `RestStateDiffStrategy` + supporting types

**Actor:** Hyperbee.Migrations runtime -- invoked when `dotnet hyperbee-migrations squash --provider opensearch --range N..M` runs
**Intention:**
- *Immediate:* The squash CLI produces a deterministic `Squash_M.statements` file for an OpenSearch migration range
- *Outcome:* OpenSearch operators get squash; the strategy contract is pressure-tested against the highest-complexity provider (component templates, ISM policies, ingest pipelines, aliases, painless scripts)
- *Metric:* `Hyperbee.Migrations.Providers.OpenSearch.Squash` namespace contains the six component types; CLI produces a passing verification round

**Friction today:** Same as R-P1 against OpenSearch.

**Given:** An OpenSearch cluster has migrations 1000..2000 applied; the operator runs the squash CLI
**When:** The strategy executes its generate -> verify -> emit loop
**Then:**
- `OpenSearchTopologySignature` captures cluster major.minor, distribution (OpenSearch CE vs AWS Managed), ISM plugin presence + version, ingest-pipeline plugin presence, active component-template references
- `RestStateDiffStrategy` captures via REST: `_index_template/*`, `_component_template/*`, `_index/<each>/_mapping`, `_index/<each>/_settings`, `_alias`, `_ism/policies` (where ISM plugin is present), `_ingest/pipeline`, painless scripts
- `OpenSearchSnapshotCanonicalizer.Canonicalize` strips ephemeral fields (creation_date, uuid, version), sorts indexes + templates + policies deterministically, normalizes painless script whitespace + variable-rename equivalence (the highest-risk piece per ADR-0019 -- if not solvable byte-exact, fall back to AST-equivalence)
- `OpenSearchSnapshotCanonicalizer.EmitScript` emits a `.statements` file using the OpenSearch DSL (`CREATE TEMPLATE`, `CREATE COMPONENT`, `CREATE INDEX ... WITH BODY @body.json`, `CREATE POLICY ...`, `BODIES { ... }` header for inline bodies)
- `OpenSearchDataOpClassifier` classifies `_bulk` calls + `Index`/`Update`/`Delete` calls as data ops; index/template/policy management as structural
- `OpenSearchStatementClassifier` parses `.statements` / `.statements.json` using the existing OpenSearch DSL parser; classifies REINDEX FROM... TO as a special case (reindex is structural movement of data ops; the squash must capture both source and target states)
- `OpenSearchSquashVerifier` runs the two-container byte-equal verification round on a single-node cluster (multi-node verification is gated `[TestCategory("LocalOnly")]` per existing convention)

**Otherwise:** If painless script canonicalization cannot be made byte-exact within the strategy's scope, `SquashGenerationResult.Failed` surfaces the offending script with a diff and asks the operator to manually normalize or to annotate the migration with `[PreservePainlessVerbatim]` (which forces the script through unchanged but fails C12 if the original itself wasn't byte-stable).

**Depends on:** R-P1, R-P2, R-P3 (highest-risk; contract should be hardened by the time this starts)
**Priority:** Must -- v3.0 release gate
**Confidence:** Low (component templates, ISM policies, painless scripts, ingest pipelines, alias graphs -- the largest canonicalization surface of the five providers)

### Theme 2: Determinism + verification

#### R-P5: Per-provider determinism gate (C12) test passes for each strategy

**Actor:** Continuous integration; gates merges into the squash branch
**Intention:**
- *Immediate:* Re-running squash codegen against the same migration range produces byte-identical output
- *Outcome:* Squashes that ship are reproducible by any operator with the same source range, providing a build-time guarantee against time-of-day / hostname / process-id contamination
- *Metric:* For each provider, a test runs `GenerateAsync` twice against the same fixture migration range and asserts SHA-256 byte equality of the emitted script

**Friction today:**
- Current: Postgres has a determinism gate test; the other four providers have nothing
- Failure mode: A canonicalizer that depends on non-deterministic input (sort-order-dependent, timestamp-leaking, machine-id-leaking) ships and operators get squashes that diff against themselves
- Frequency: every CI run after a canonicalizer regression

**Given:** A fixture migration range and a snapshot captured against a controlled ephemeral container
**When:** The provider's strategy runs `GenerateAsync` twice in succession
**Then:** Both runs produce identical SHA-256 hashes of the emitted `.statements` content
**Otherwise:** Test fails naming the byte offset of first divergence

**Depends on:** R-P1, R-P2, R-P3, R-P4 (one test per provider)
**Priority:** Must -- C12 contract from ADR-0019; release-blocking
**Confidence:** High (mechanical; the difficulty is making the canonicalizer deterministic, not the test)

#### R-P6: End-to-end verification-round integration test passes for each provider

**Actor:** Continuous integration (or LocalOnly per existing test convention)
**Intention:**
- *Immediate:* The full snapshot-A -> apply [N..M] -> snapshot-B -> diff loop runs against a real provider via Testcontainers and asserts byte-equality
- *Outcome:* The strategy is validated against actual provider behavior, not against mocks
- *Metric:* For each provider, an integration test runs the full loop using a Testcontainers-spun instance and the verifier returns `VerificationResult.Success`

**Friction today:** Same as R-P5 -- only Postgres has this test today.

**Given:** A fresh ephemeral provider container; a fixture migration range
**When:** The verifier runs snapshot-A capture, applies the squash to a second container, captures snapshot-B, canonicalizes both, byte-compares
**Then:** `VerificationResult.Success` is returned; both containers torn down on success
**Otherwise:** `VerificationResult.Failed` carries the diff summary; the squash CLI surfaces the failure; failed containers are torn down unless `--keep-failed-container` is set (per ADR-0019 A18)

**Depends on:** R-P5 (a non-deterministic generator means the verification cannot trust byte-equality), R-P1..R-P4
**Priority:** Must -- A4 verification round from ADR-0019; release-blocking
**Confidence:** Medium (Testcontainers wall-clock cost is real; tests may need to be `[TestCategory("LocalOnly")]` per provider if container startup is too slow for CI)

### Theme 3: Strategy abstraction stability

#### R-P7: `ISquashStrategy` contract evolves only via amended ADR-0019 + sign-off

**Actor:** Single developer (Brent); applies to each new provider implementation
**Intention:**
- *Immediate:* The strategy abstraction's pressure-test feedback loop is captured in the audit trail
- *Outcome:* When a provider implementation surfaces a gap in the contract (e.g., "this provider needs an additional callback") the change is documented in ADR-0019 BEFORE the contract changes, not after
- *Metric:* Every shipped contract change has a matching ADR-0019 amendment entry with rationale and a pointer to the surfacing provider

**Friction today:**
- Current: Pressure-testing only happened against Postgres (the reference); the contract has not been amended since shipping
- Failure mode: A provider implementation silently extends the contract surface (or copies the contract into a forked file), losing the audit trail and the all-providers-conformance guarantee
- Frequency: every provider implementation that genuinely needs to push on the contract

**Given:** A provider implementation has discovered a contract gap (e.g., `ISquashVerifier` needs a `WarmupAsync` hook because the provider's startup is too slow)
**When:** The developer is about to amend the interface
**Then:** ADR-0019 gets a new amendment entry (Axx) documenting: the surfacing provider; the gap; the proposed contract change; whether it's source-compatible for existing implementations; the implementations updated to match. The contract change lands AFTER the ADR amendment is written.
**Otherwise:** Contract changes that ship without an ADR amendment are rolled back; the developer files the amendment retroactively before re-attempting

**Priority:** Must -- preserves the audit-trail rule from `feedback_docs_about_system_not_history.md` and supports the "all 5 ship together" rule by keeping the contract consistent
**Confidence:** High (process rule, not an implementation requirement)

### Theme 4: Test coverage per provider

#### R-P8: Each provider's squash code has a parallel unit-test class per component

**Actor:** Test suite
**Intention:**
- *Immediate:* Each component (topology / classifier / canonicalizer / strategy / verifier) has dedicated unit tests
- *Outcome:* Regressions surface against fast unit feedback, not slow Testcontainers integration runs
- *Metric:* Six unit-test classes per provider in `tests/Hyperbee.Migrations.Squash.Tests/` (or per-provider subdirectory)

**Friction today:**
- Current: Postgres has 4 unit-test classes covering its squash components; the other four providers have nothing
- Failure mode: Without unit tests, the canonicalizer's edge cases (timestamps, ordering, line endings, empty / single-item / large fixtures) only surface in slow integration tests
- Frequency: every developer change to a canonicalizer

**Given:** A provider's squash component (e.g., `AerospikeSnapshotCanonicalizer`)
**When:** The developer makes a change
**Then:** A dedicated unit-test class in the squash test project exercises the component against fixture inputs; the test runs in milliseconds, not seconds
**Otherwise:** PRs that change squash components without parallel unit tests are rejected at review

**Depends on:** R-P1..R-P4
**Priority:** Should -- safety net for canonicalization changes
**Confidence:** High

#### R-P9: Per-provider squash CLI integration test runs the full operator scenario

**Actor:** Test suite
**Intention:**
- *Immediate:* The exact CLI invocation an operator would type produces the expected squash output
- *Outcome:* CLI verb routing, fleet manifest loading, source scanner integration, generation, verification, and file emission are all tested end-to-end
- *Metric:* For each provider, one integration test that invokes the CLI verb against a fixture migration range and asserts the emitted file is present + byte-stable

**Friction today:**
- Current: Postgres has end-to-end squash integration tests; the others have none
- Failure mode: CLI verb regressions (argument parsing, provider dispatch) surface only when an operator types the command

**Given:** A fixture migration assembly + an ephemeral provider container
**When:** The test invokes `dotnet hyperbee-migrations squash --provider {name} --range 1000..2000 --output /tmp/Squash_2000.statements`
**Then:** The file exists, is byte-stable across re-runs (C12), and the verification round (R-P6) passes against it
**Otherwise:** Test fails with the CLI's stderr capture for debugging

**Depends on:** R-P5, R-P6
**Priority:** Should -- catches CLI regressions
**Confidence:** Medium (the squash CLI itself has minimal test coverage today; this requirement implicitly demands CLI test coverage for the verb)

## Constraints

- Multi-targets `net8.0` / `net9.0` / `net10.0`; tests must pass on all three frameworks
- ASCII-only docs (per `feedback_no_nonascii_docs.md`)
- No `global::` prefix (per `feedback_no_global_prefix.md`)
- No `Console.WriteLine` in production code (use `ILogger`)
- Backward compatibility: existing v2 migrations must continue to work; squash is additive
- Order of implementation locked (revised 2026-05-11): Aerospike -> **OpenSearch** -> MongoDB -> Couchbase
- All 4 providers must be done before v3.0 ships; partial scope is not on the table (per `feedback_squash_all_providers_v1.md`)
- No test failures shipped (per `feedback_no_preexisting_bugs.md`)

## Trust boundaries

**Autonomous (system decides without human):**
- Per-provider canonicalization rule choices (what bytes go into the snapshot, what gets stripped) -- the developer iterates within the contract
- Test fixture content (which migration ranges are used to exercise the strategy)
- Per-provider classifier rules (which call sites are data ops)

**Escalate (human approves before proceeding):**
- Any contract change to `ISquashStrategy`, `ITopologySignature`, `IDataOpClassifier`, `ISnapshotCanonicalizer`, `ISquashVerifier` -- update ADR-0019 first, get explicit OK before changing the interface
- Decisions to mark a provider's integration test `[TestCategory("LocalOnly")]` -- document the reason
- Decisions to use mocks instead of real Testcontainers for any verification-round test -- the byte-equal assertion against real provider behavior is the load-bearing test

**Forbidden:**
- Shipping a provider's strategy without a passing C12 determinism gate (R-P5)
- Shipping any provider before all 4 are ready (the all-providers-or-nothing rule)
- Forking the strategy contract into a per-provider copy
- Suppressing `VerificationResult.Failed` and shipping the squash anyway
- Skipping ADR-0019 amendment when changing the strategy contract

## Out of Scope

- Multi-node verification rounds (single-node verification is sufficient for v3.0 byte-equal contract; multi-node is a future hardening)
- Cross-provider squash (one squash spanning multiple providers; the multi-runner ADR-0023 establishes that cross-store coordination is application-layer concern)
- Squash CLI ergonomics improvements beyond what the Postgres CLI already has (the CLI shape is locked at v1; v3.0 inherits it)
- Snapshot caching across runs (per ADR-0019 A4 the squash CLI already caches snapshot A; this work consumes that cache but does not change its shape)
- Painless script AST-equivalence (R-P4) -- if byte-exact canonicalization isn't achievable for painless within v3.0, the fallback is operator-annotated `[PreservePainlessVerbatim]` with C12 enforcement; full AST equivalence is a v3.1 hardening

## Decisions & Open Questions

### Decided

- **All 4 remaining providers ship in v3.0 (locked rule).** Postgres-only paths are off the table; partial scope is not a negotiating lever. -- *Influences: every requirement here*
- **Order: Aerospike, OpenSearch, MongoDB, Couchbase (revised 2026-05-11).** Optimizes for contract validation rather than gradual complexity. Aerospike (Low) hardens the contract against a friendly case; OpenSearch (High) pressure-tests against the hardest provider while only one prior implementation has to recompile if a contract gap surfaces; MongoDB (Medium-High) and Couchbase (High) inherit the established pattern. -- *Influences: depends-on chain across R-P1 -> R-P4 -> R-P2 -> R-P3 (new sequence)* -- *Reverses: original "increasing canonicalization risk" ordering which optimized for build-confidence-gradually*
- **Strategy contract is amend-via-ADR-0019, not fork.** Captured as R-P7. -- *Influences: shared squash contract surface*
- **Determinism is C12-byte-equal, not AST-equal.** A canonicalizer that produces byte-stable output is the gate. -- *Influences: R-P5*
- **Verification round byte-compares snapshot A vs snapshot B from real provider containers, not mocks.** -- *Influences: R-P6*
- **No `NullSquashStrategy` in v3.0.** Removed by the retraction; v3.0 ships real strategies for all 5. -- *Influences: registration shape in `Add{Provider}Migrations`*

### Open

- **Canonicalization of MongoDB index `v` field.** Status: exploring. MongoDB stamps an index version on creation that varies by server version; squash output captured against MongoDB 6 and replayed on MongoDB 7 will diff. Leaning: strip the `v` field entirely from canonical output and document that the squash's effective index version is server-determined at apply time. Depends on: validating that strip-and-replay produces equivalent runtime indexes. Influences: R-P2.
- **Canonicalization of OpenSearch painless scripts.** Status: exploring. Painless scripts that produce identical results may differ in source bytes (whitespace, variable names). Leaning: in v3.0 require source-byte stability (operators must commit the exact script bytes the squash will reproduce). Full AST-equivalence is v3.1. Depends on: surveying real-world painless scripts in production migrations. Influences: R-P4.
- **Couchbase deferred-index handling.** Status: exploring. Deferred-build indexes appear in `system:indexes` as `state = 'deferred'` and don't materialize until `BUILD INDEX` is dispatched. Squash output should always include the deferred-then-built sequence to make the squash idempotent. Depends on: validating that the canonicalizer emits both phases. Influences: R-P3.
- **Whether to register strategies via assembly attributes or DI.** Status: deferred. Postgres registers via DI (`AddPostgresMigrations` extension). The same pattern continues for the others. Worth revisiting if multi-runner composition (ADR-0023) ships a per-provider runner subclass that needs strategy injection -- they should remain DI-resolvable. Depends on: ADR-0023 implementation. Influences: registration shape.
- **CI test split: LocalOnly vs CI.** Status: exploring. Multi-node OpenSearch tests are already `[TestCategory("LocalOnly")]` because GitHub-hosted runners cannot sustain 3 JVMs. Single-node verification rounds for the four providers SHOULD fit on CI runners but each has its own resource shape. Leaning: start with all on CI; demote to LocalOnly only when a specific provider's container exceeds runner budget. Depends on: measuring wall-clock + memory on the CI runner per provider. Influences: R-P6, R-P9.
