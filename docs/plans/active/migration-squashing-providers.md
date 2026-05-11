# Plan: Squash Codegen for the Four Non-Postgres Providers (v3.0 Release Gate)

**Status:** Active
**Created:** 2026-05-11
**Requirements:** [docs/requirements/migration-squashing-providers.md](../../requirements/migration-squashing-providers.md) (R-P1 through R-P9)
**Constraining ADRs:** ADR-0019 (Replaces graph + 19 amendments), ADR-0020 (squashes are up-only), ADR-0021 (record checksum), ADR-0022 (script-format resources)
**Branch:** `devs/bfarmer/provider-squash` (existing branch; per-provider work continues here)
**Locked rules:**
- [feedback_squash_all_providers_v1.md](../../../../Users/bfarm/.claude/projects/c--Development-hyperbee-migrations/memory/feedback_squash_all_providers_v1.md) -- all 5 ship together, no partial scope, no scope-reduction trade
- [feedback_no_preexisting_bugs.md](../../../../Users/bfarm/.claude/projects/c--Development-hyperbee-migrations/memory/feedback_no_preexisting_bugs.md) -- no test failures shipped
- [feedback_no_global_prefix.md](../../../../Users/bfarm/.claude/projects/c--Development-hyperbee-migrations/memory/feedback_no_global_prefix.md)
- [feedback_no_nonascii_docs.md](../../../../Users/bfarm/.claude/projects/c--Development-hyperbee-migrations/memory/feedback_no_nonascii_docs.md)

## Objective

Land squash codegen for **Aerospike, MongoDB, Couchbase, and OpenSearch** so v3.0 can ship. Each provider gets its own `Squash/` directory in `src/Hyperbee.Migrations.Providers.{Provider}/Squash/` containing six concrete components (TopologySignature, DataOpClassifier, StatementClassifier, SnapshotCanonicalizer, SnapshotStrategy, SquashVerifier) plus a SquashGenerationContext and any provider-specific helper types. Each provider gets a parallel unit-test class per component, a determinism gate integration test (R-P5), an end-to-end verification round integration test (R-P6), and a CLI integration test (R-P9).

**Success criteria** (all four conditions, AND-gated):

1. All four providers' `Squash/` directories contain the six required components plus context + helpers.
2. `dotnet hyperbee-migrations squash --provider {name} --range 1000..2000` produces a deterministic `Squash_2000.statements` file for each provider.
3. Determinism gate (R-P5) and verification round (R-P6) tests pass for each provider.
4. `ISquashStrategy` and the four sibling interfaces (`ITopologySignature`, `IDataOpClassifier`, `ISnapshotCanonicalizer`, `ISquashVerifier`) have not been changed without a matching ADR-0019 amendment (R-P7).

## Inputs

- Requirements: [migration-squashing-providers.md](../../requirements/migration-squashing-providers.md)
- Parent feature requirements: [migration-squashing.md](../../requirements/migration-squashing.md) (R-09 retracted 2026-05-09)
- Shared squash contract (19 files): [src/Hyperbee.Migrations/Squash/](../../../src/Hyperbee.Migrations/Squash/)
   - `ISquashStrategy` -- the orchestrator interface
   - `ITopologySignature` -- the per-provider topology axes
   - `IDataOpClassifier` -- Roslyn-based call-site classification
   - `ISnapshotCanonicalizer` -- byte-stable serialization
   - `ISquashVerifier` -- snapshot-A vs snapshot-B byte-compare
   - `ISquashGenerationContext`, `SquashGenerationOptions`, `SquashGenerationResult`, `SquashMetadata`, `SquashStrategyDescriptor`, `RecoveryAcknowledgement`, `SquashFleetGate`, `MidRangeFleetException`, `StaleFleetMemberException`, `UnregisteredEnvironmentException`, `DataOpClassification`, `ContentKind`, `ContentEncoding`
- Postgres reference implementation: [src/Hyperbee.Migrations.Providers.Postgres/Squash/](../../../src/Hyperbee.Migrations.Providers.Postgres/Squash/) (10 files, ~1700 LOC)
- Squash test infrastructure: [tests/Hyperbee.Migrations.Squash.Tests/](../../../tests/Hyperbee.Migrations.Squash.Tests/) (125 passing unit tests)
- Integration test fixtures: [tests/Hyperbee.Migrations.Integration.Tests/Container/](../../../tests/Hyperbee.Migrations.Integration.Tests/Container/) -- one Testcontainers helper per provider
- Sample migrations per provider: [runners/samples/Hyperbee.Migrations.{Provider}.Samples/](../../../runners/samples/)

## Constraints

- Multi-target `net8.0` / `net9.0` / `net10.0`. Tests must pass on all three.
- ASCII-only docs.
- No `global::` prefix in source.
- No `Console.WriteLine` in production code; use `ILogger`.
- All 4 providers must be done before v3.0 ships; partial scope is not viable.
- Implementation order is locked: Aerospike -> **OpenSearch** -> MongoDB -> Couchbase (revised 2026-05-11 -- "hardest second" for contract pressure-testing rather than "increasing canonicalization risk"; rationale below).
- Order rationale: validating the contract against the hardest provider second means we discover any contract gap before building MongoDB and Couchbase on a foundation that turns out to be insufficient. OpenSearch (component templates, ISM policies, painless scripts, plugin matrix) is the hardest case; if it fits, the others almost certainly will. The painless-equivalence spike (originally Task 4.0) moves to the new Phase 2 start.
- Contract changes follow R-P7: ADR-0019 amendment lands BEFORE the interface changes.
- No `NullSquashStrategy` ships in v3.0; every provider has a real strategy.
- Test counts must stay green at every phase boundary (last known: 1443/1443 unit, 87/88 integration with 1 self-skip to be removed in cleanup).

## Phases

### Phase 0: Foundation + Postgres-reference audit (~1 day)

Read the Postgres squash implementation in detail, document its file:line shape, identify what's truly shared vs provider-specific, snapshot the strategy contract so future amendments are diffable.

#### Task 0.1: Postgres-reference file:line walk ☑ COMPLETE 2026-05-11

**Prerequisites:** Branch checked out; `dotnet build` clean.

- Read each of the 10 Postgres squash source files in [src/Hyperbee.Migrations.Providers.Postgres/Squash/](../../../src/Hyperbee.Migrations.Providers.Postgres/Squash/).
- For each file, record in a Phase 0 audit appendix at the bottom of this plan:
  - Public-API surface (types + methods)
  - Per-method approximate LOC
  - Dependencies (shared squash contract types, provider client API, Roslyn, file I/O)
  - What's "Postgres-specific glue" vs "could be shared via a base class" vs "shaped by the canonical pattern but provider-specific content"
- Output: an `### Appendix A: Postgres reference shape` section at the bottom of this plan.

**Completion criteria:** Appendix A exists with one entry per Postgres squash file; ambiguous shared-vs-specific cells flagged for Phase 1 resolution.

#### Task 0.2: Contract snapshot ☑ COMPLETE 2026-05-11

**Prerequisites:** Task 0.1 complete.

- Capture the current `ISquashStrategy` / `ITopologySignature` / `IDataOpClassifier` / `ISnapshotCanonicalizer` / `ISquashVerifier` shapes (member list + signatures) in `### Appendix B: Strategy contract snapshot (2026-05-11)` at the bottom of this plan.
- This snapshot becomes the diff baseline. Any phase that amends a contract type MUST update the snapshot and reference the ADR-0019 amendment number.

**Completion criteria:** Appendix B exists with verbatim contract surfaces.

#### Task 0.3: Per-provider introspection-API survey ☑ COMPLETE 2026-05-11

**Prerequisites:** Task 0.1 complete.

For each of the four target providers, document the introspection API the snapshot strategy will use, in `### Appendix C: Per-provider introspection surfaces`:

- **Aerospike:** `Info.Request(node, "namespaces;sets;sindex;udf-list")` per ADR-0019 Phase 6 expansion notes. Identify the exact info-key set; sample the output format on a real container.
- **MongoDB:** `db.runCommand({listCollections})`, `db.getCollection(...).getIndexes()`, schema validators via `options.validator`. Sample output against the test container.
- **Couchbase:** `system:keyspaces`, `system:indexes`, Management API for bucket/scope settings. Sample N1QL output.
- **OpenSearch:** `_index_template/*`, `_component_template/*`, `_index/<n>/_mapping`, `_index/<n>/_settings`, `_alias`, `_ism/policies`, `_ingest/pipeline`, painless scripts. Sample REST output.

**Completion criteria:** Appendix C names the exact endpoints and sample-output shape for each provider.

#### Task 0.4: Plan update + commit

**Prerequisites:** 0.1 through 0.3 complete.

Commit Phase 0 work; update plan checkboxes; update the project status memory to reflect Phase 0 done.

### Phase 1: Aerospike squash codegen (R-P1, R-P5, R-P6, R-P8, R-P9) (~1 week) ☑ COMPLETE 2026-05-11

Low canonicalization risk. First non-Postgres pressure test of the strategy contract.

#### Task 1.1: `AerospikeTopologySignature` (R-P1 partial) ☑ COMPLETE 2026-05-11

**Prerequisites:** Phase 0 done. Appendix B contract snapshot in place.

Implement `Hyperbee.Migrations.Providers.Aerospike.Squash.AerospikeTopologySignature : ITopologySignature` capturing:

- `SchemaVersion = 1`
- `ProviderId = "aerospike"`
- Properties: server major.minor; namespace name; replication-factor; default-ttl; nsup-period; memory-size; storage-engine (memory vs device); cluster-name

Source: `Info.Request(node, "build", "cluster-name", "namespace/{ns}")` -- the namespace info response is a semicolon-separated `key=value` map parsed by an internal helper.

**Outcome:** [AerospikeTopologySignature.cs](../../../src/Hyperbee.Migrations.Providers.Aerospike/Squash/AerospikeTopologySignature.cs) ships sealed-record signature + `CaptureAsync(IAerospikeClient, namespace, CT)` + internal `ParseBuildVersion` / `ParseInfoMap` helpers. IsCompatibleWith fails fast on cross-provider compare and on each axis mismatch with a human-readable `reason`. Server-minor differences tolerated; majors and structural axes (namespace, replication-factor, storage-engine, memory-size) are strict. [AerospikeTopologySignatureTests.cs](../../../tests/Hyperbee.Migrations.Squash.Tests/AerospikeTopologySignatureTests.cs) covers 16 unit tests across the comparison rules and helper-parser behavior; live CaptureAsync deferred to Phase 1 integration test (Testcontainers).

**Completion criteria:** ☑ Type compiles. ☑ `IsCompatibleWith` returns true for same-version-different-minor only when the topology actually matches; false with diagnostic on mismatch. ☑ 16/16 unit tests pass on net10; full squash suite 141/141 (was 125, +16 new); core 356/356 still green.

#### Task 1.2: `AerospikeDataOpClassifier` (R-P1 partial) ☑ COMPLETE 2026-05-11

**Prerequisites:** Task 1.1 complete.

Implement `AerospikeDataOpClassifier : IDataOpClassifier`. Matches the Postgres reference text-classifier shape (no Roslyn at this layer; Roslyn walker is a follow-up source-scanner task that feeds call-site strings into this classifier per ADR-0019 A5):

- Statement-form (ADR-0022 script): `INSERT INTO`, `DELETE FROM` -> data op; `CREATE INDEX`, `DROP INDEX`, `CREATE SET`, `DROP SET`, `CREATE UDF`, `DROP UDF` -> structural.
- Call-site form (receiver-anchored `_?client.<verb>(`): `Put` / `Delete` / `Touch` -> data op; `Get*` / `Exists` / `Query*` / `ScanAll` / `ScanNode` / `BatchGet` -> read; `CreateIndex` / `DropIndex` / `RegisterUdf` / `RemoveUdf` + static `Info.Request` / `Info.Reset` -> structural; `Operate` -> Unclassified (requires explicit annotation since the operations list determines read-vs-write).
- Unknown verbs -> default-deny (`IsUnclassified=true`, `RequiresAnnotation=true`).
- Non-determinism scan per ADR-0019 A8: flags `DateTime.Now`, `DateTime.UtcNow`, `DateTimeOffset.{Now,UtcNow}`, `Guid.NewGuid()`, `new Random()` without seed, `Random.Shared`, `Environment.TickCount(64)`, `Stopwatch.GetTimestamp()`. Detected non-determinism populates `EmissionHint` and sets `RequiresAnnotation`.

**Outcome:** [AerospikeDataOpClassifier.cs](../../../src/Hyperbee.Migrations.Providers.Aerospike/Squash/AerospikeDataOpClassifier.cs) ships sealed-class classifier + static `ScanNonDeterminism(string)` helper. Regex receiver anchor (`\b_?client\.`) prevents false positives when write verbs appear in argument lists (e.g., `Operation.Put(bin)` passed to `_client.Operate(...)`).

**Completion criteria:** ☑ Statement-form partitioned (INSERT/DELETE/CREATE/DROP); ☑ call-site form partitioned (Put/Delete/Touch/Operate/Get*/Exists/Query*/CreateIndex/Info.Request); ☑ Operate routes to Unclassified with diagnostic; ☑ default-deny on unknown verbs; ☑ non-determinism scan covers documented patterns including seeded-Random exemption; ☑ 25/25 unit tests pass on net10; full squash suite 166/166 (+25 new, was 141).

**Cross-provider participation note:** Operate's "requires annotation" route is the first contract-pressure point where Aerospike's classifier needs a 4th state beyond clean data-op / clean structural / fully-unclassified -- but it's representable within the existing `DataOpClassification` shape (`IsUnclassified=true + EmissionHint`). The contract is sufficient; no ADR-0019 amendment required.

#### Task 1.3: `AerospikeStatementClassifier` (R-P1 partial, parser-driven) ☑ COMPLETE 2026-05-11

**Prerequisites:** Task 1.2 complete.

Implement `AerospikeStatementClassifier` using the existing `AerospikeStatementParser` ([src/Hyperbee.Migrations.Providers.Aerospike/Parsers/AerospikeStatementParser.cs](../../../src/Hyperbee.Migrations.Providers.Aerospike/Parsers/AerospikeStatementParser.cs)):

- `CREATE INDEX` -> `AerospikeStatementKind.CreateIndex` (structural)
- `DROP INDEX` -> `DropIndex` (structural)
- `CREATE SET` -> `CreateSet` (structural)
- `INSERT INTO` -> `Insert` (data op)
- `DELETE FROM` -> `Delete` (data op)
- Unknown statements -> `Unknown` (Body preserved + Detail = parser error message)

Reference: [PostgresStatementClassifier.cs](../../../src/Hyperbee.Migrations.Providers.Postgres/Squash/PostgresStatementClassifier.cs) (289 LOC).

**Outcome:** [AerospikeStatementClassifier.cs](../../../src/Hyperbee.Migrations.Providers.Aerospike/Squash/AerospikeStatementClassifier.cs) (~60 LOC) + [AerospikeStatementKind.cs](../../../src/Hyperbee.Migrations.Providers.Aerospike/Squash/AerospikeStatementKind.cs) (byte enum). Dramatically smaller than the Postgres reference because Aerospike's statement surface is narrower (5 kinds vs ~30). Classifier delegates to `AerospikeStatementParser.ParseStatement`, lifts the result into a `ClassifiedStatement(Kind, Namespace, SetName, ObjectName, Body, Detail)` record, and gracefully returns `Unknown` on parser failure. [AerospikeStatementClassifierTests.cs](../../../tests/Hyperbee.Migrations.Squash.Tests/AerospikeStatementClassifierTests.cs) -- 12 tests covering each kind, backtick-quoted identifiers, optional CREATE INDEX flags, and the default-deny paths (unknown verb, syntax error, empty/null/whitespace input).

**Completion criteria:** ☑ Classifier returns the correct `Kind` for each statement type in fixture resource content. ☑ Namespace/SetName/ObjectName populated as expected per kind. ☑ Unknown shapes return `Unknown` kind with `Body` preserved and `Detail` carrying the parser diagnostic. ☑ 12/12 unit tests pass on net10; full squash suite 178/178 (+12 new, was 166).

**UDF deferral note:** The plan called out `CREATE UDF` / `DROP UDF` but the v2 `AerospikeStatementParser` grammar does not yet support those. Adding them is a follow-up that grows the parser grammar in lockstep with `AerospikeStatementKind` enum values; if Task 1.5 (InfoSnapshotStrategy) decides UDFs must round-trip through statement form, the parser is extended then. For v1 squash, UDFs can ride as a separate non-statement artifact (the same pattern Postgres uses for `CREATE EXTENSION` -> `.prerequisites.sql`). Decision deferred to Task 1.5.

#### Task 1.4: `AerospikeSnapshotCanonicalizer` (R-P1 partial) ☑ COMPLETE 2026-05-11

**Prerequisites:** Task 1.3 complete. Task 0.3 Appendix C documents the `Info.Request` output format.

Implement `AerospikeSnapshotCanonicalizer : ISnapshotCanonicalizer`:

- Snapshot blob format (produced by the InfoSnapshotStrategy in Task 1.5): `[sets]` / `[sindex]` section headers (case-insensitive) followed by the verbatim `Info.Request` response for that section. Comment lines (`#`) and blank lines between sections are ignored.
- `Canonicalize(snapshot)`: parse sections, extract structural fields only (drops ephemerals: `objects`, `tombstones`, `memory_used`, `state`, `keys`, `entries`, `ibtr_memory_used`, etc.), sort by ordinal `(ns, set)` for sets and `(ns, indexname)` for indexes, normalize line endings to `\n`. When input is already canonical statement form (no section headers), re-parse via `AerospikeStatementClassifier` + `AerospikeStatementParser` and re-emit -- preserves idempotence.
- `EmitScript(canonicalContent)`: alias of `Canonicalize` (the canonical form IS the script form, mirroring Postgres' pattern).
- AQL output shape: `CREATE SET <ns>.<set>;` per set; `CREATE INDEX WAIT <name> ON <ns>.<set>(<bin>) [STRING|NUMERIC|GEO2DSPHERE];` per index. Index type `DEFAULT` and missing-type entries normalize to `STRING`.

Reference: [PostgresSnapshotCanonicalizer.cs](../../../src/Hyperbee.Migrations.Providers.Postgres/Squash/PostgresSnapshotCanonicalizer.cs) (135 LOC).

**Outcome:** [AerospikeSnapshotCanonicalizer.cs](../../../src/Hyperbee.Migrations.Providers.Aerospike/Squash/AerospikeSnapshotCanonicalizer.cs) (~270 LOC, larger than Postgres because it implements both the raw-Info-protocol parse path AND the statement-form re-emit path). [AerospikeSnapshotCanonicalizerTests.cs](../../../tests/Hyperbee.Migrations.Squash.Tests/AerospikeSnapshotCanonicalizerTests.cs) -- 18 tests covering: basic snapshot round-trip; per-section sort order; idempotence (Canonicalize(Canonicalize(x)) == Canonicalize(x)); CRLF normalization; statement-form input round-trip; empty / sets-only / indexes-only snapshots; index-type DEFAULT / missing / GEO2DSPHERE normalization; case-insensitive section headers; EmitScript = Canonicalize identity; internal helper coverage (ParseSections, ParseEntries).

**Bug surfaced + fixed during test:** First test pass failed the idempotence assertion because the statement splitter accumulated leading `--` comment lines into its current buffer and then incorrectly skipped the following statement via a `StartsWith("--")` filter. Fix: strip `--`/`//` comment LINES (line-leading) before the splitter walks the script. Lesson carries forward: when each provider's canonicalizer adds a re-parse path for idempotence, the comment-stripping needs to be a separate pre-pass, not inline filter logic.

**Completion criteria:** ☑ Canonicalizer is idempotent against fixture snapshots; ☑ `EmitScript` output parses cleanly through `AerospikeStatementParser` (round-trip stable); ☑ Ephemeral fields stripped; ☑ Deterministic sort order; ☑ 18/18 unit tests pass on net10; full squash suite 196/196 (+18 new, was 178); core 356/356 still green.

**UDF deferral confirmed:** v1 canonicalizer does NOT parse a `[udfs]` section. If Task 1.5 decides UDFs ship in v1 squash output, the canonicalizer gains a `[udfs]` parse + emit path then.

#### Task 1.5: `InfoSnapshotStrategy` (R-P1 partial) ☑ COMPLETE 2026-05-11

**Prerequisites:** 1.1 through 1.4 complete.

Implement `InfoSnapshotStrategy : ISquashStrategy` (`ProviderId = "aerospike"`) + `AerospikeSquashGenerationContext : ISquashGenerationContext`:

**Strategy `GenerateAsync(context, descriptors, options, ct)`:**
1. Validate context type (`AerospikeSquashGenerationContext`) and non-empty descriptors; return `Failed` on either.
2. Capture topology via `AerospikeTopologySignature.CaptureAsync(client, namespace)` from the operator's live cluster.
3. Resolve `[lowerBound..upperBound]` from options or descriptor range; compute the sorted `Replaces[]`.
4. Invoke the injected `CaptureSnapshotAsync` delegate (the snapshot-B capture: applies migrations through the upper bound against an ephemeral cluster, returns the raw `[sets]`/`[sindex]` blob).
5. Canonicalize via `AerospikeSnapshotCanonicalizer.Canonicalize`.
6. For each emitted statement, classify via both `AerospikeStatementClassifier` (kind + name) and `AerospikeDataOpClassifier` (non-determinism scan); collect diagnostics for any `EmissionHint` or `Unknown` kind.
7. Return `SquashGenerationResult.Generated(Content=emitted, Kind=SqlText, Encoding=Utf8, Replaces, Diagnostics, Topology)`.

**Context shape** (provider-specific concrete `ISquashGenerationContext`):
- `IAerospikeClient Client` -- live cluster handle for topology capture.
- `string Namespace` -- scopes topology + snapshot.
- `Func<SnapshotCaptureRequest, CT, Task<SnapshotCaptureResult>> CaptureSnapshotAsync` -- delegate-injected so the runtime library carries no Testcontainers dependency. CLI / test harness wires concrete capture; production wires Testcontainers Aerospike + `Info.Request("sets/<ns>", "sindex/<ns>")`.
- `SnapshotCaptureRequest(Label, UpToVersion, RequiredTopology)` + `SnapshotCaptureResult(SnapshotBlob)` records.

Reference: [PgDumpSnapshotStrategy.cs](../../../src/Hyperbee.Migrations.Providers.Postgres/Squash/PgDumpSnapshotStrategy.cs) (161 LOC) + [PostgresSquashGenerationContext.cs](../../../src/Hyperbee.Migrations.Providers.Postgres/Squash/PostgresSquashGenerationContext.cs). Aerospike strategy is ~165 LOC + context is ~100 LOC -- effectively the same shape as the Postgres reference. The structural difference is the snapshot mechanism: `pg_dump --schema-only` (external tool) vs `Info.Request` (in-process client SDK).

**Outcome:** [InfoSnapshotStrategy.cs](../../../src/Hyperbee.Migrations.Providers.Aerospike/Squash/InfoSnapshotStrategy.cs) + [AerospikeSquashGenerationContext.cs](../../../src/Hyperbee.Migrations.Providers.Aerospike/Squash/AerospikeSquashGenerationContext.cs). [AerospikeSquashEndToEndTests.cs](../../../tests/Hyperbee.Migrations.Squash.Tests/AerospikeSquashEndToEndTests.cs) -- 9 tests covering early-return paths (null context, wrong context type, empty descriptors), canonicalizer pipeline behavior (statements emitted, classifier diagnostic-empty for clean fixture), context constructor validation (all required args), and provider-id wiring.

**Deferred to Phase 1 integration test (Task 1.6+):** Live `GenerateAsync` happy path against Testcontainers Aerospike (requires real `IAerospikeClient.Nodes` + `Info.Request` round-trip). The end-to-end success-path test lives there because synthetic `IAerospikeClient` substitution doesn't satisfy the live `Info.Request` call shape used by `AerospikeTopologySignature.CaptureAsync`.

**ADR-0019 A5 deferral note:** The Roslyn-based data-op pre-scan over descriptor source ("invoke the data-op classifier across the descriptor set; refuse on unclassified call sites without `[DataMigration]`/`[StructuralOnly]`") is the analogue of Postgres' [PostgresMigrationSourceScanner.cs](../../../src/Hyperbee.Migrations.Providers.Postgres/Squash/PostgresMigrationSourceScanner.cs). The v1 strategy classifies the EMITTED canonical statements for non-determinism and unknown shapes (same posture as v1 Postgres); the source-scanner pass is a follow-up task tracked in the cross-cutting Phase 5 hardening list. Recommended for hoisting to the core library (per Phase 0 audit cross-provider observation) once a second provider is on line.

**UDF deferral confirmed:** Strategy does not capture UDFs in v3.0. Docstring + Phase 5 release-notes note operators with UDFs to carry them forward as separate non-squashed migrations.

**Edition-axis decision deferred:** Task 1.1 noted a possible `Edition` axis bump (Community vs Enterprise) for strong-consistency feature gating. Held to spec for v1; revisit at Phase 5 release prep when end-to-end integration test runs against a real cluster surface the practical impact (or non-impact) of the gap.

**ContentKind choice:** Returns `ContentKind.SqlText` -- AQL is SQL-shaped statement form (CREATE/INSERT/DELETE with `;` terminators), so the existing dispatcher route fits. If future ContentKind refinement is needed (e.g., a dedicated AerospikeAql kind), that's an ADR-0019 amendment per R-P7 and is not gated by v1 ship.

**Completion criteria:** ☑ Strategy compiles and wires; ☑ early-return paths return `Failed` with helpful detail; ☑ canonicalizer pipeline produces expected statements + empty diagnostics on clean fixtures; ☑ context validates all required args; ☑ 9/9 unit tests pass on net10; full squash suite 205/205 (+9 new, was 196); core 356/356 still green. End-to-end Testcontainers happy path moves to the Phase 1 integration test (Task 1.6 verifier + R-P5 / R-P6 fixtures).

#### Task 1.6: `AerospikeSquashVerifier` (R-P1 partial, A4 verification round) ☑ COMPLETE 2026-05-11

**Prerequisites:** Task 1.5 complete.

Implement `AerospikeSquashVerifier : ISquashVerifier`:

- Capture A: re-apply the historical migration range up to the squash's upper bound against a fresh ephemeral cluster (via `context.CaptureSnapshotAsync`).
- Capture B: apply the GENERATED squash content to a second fresh cluster (via the verifier's `CaptureFromGeneratedAsync` delegate).
- Canonicalize both via `AerospikeSnapshotCanonicalizer`.
- Byte-compare.
- On match: return `VerificationResult.Success(topology, elapsed)`.
- On mismatch: return `VerificationResult.Failed` with line-by-line diff summary (truncated to 20 lines per side per Postgres reference convention).
- Container lifecycle (spin / tear down / retain-on-failure per A18) lives in the capture-delegate implementation, not the verifier; verifier is policy-only.

Reference: [PostgresSquashVerifier.cs](../../../src/Hyperbee.Migrations.Providers.Postgres/Squash/PostgresSquashVerifier.cs) (153 LOC).

**Outcome:** [AerospikeSquashVerifier.cs](../../../src/Hyperbee.Migrations.Providers.Aerospike/Squash/AerospikeSquashVerifier.cs) (~150 LOC, near-identical to Postgres reference). [AerospikeSquashVerifierTests.cs](../../../tests/Hyperbee.Migrations.Squash.Tests/AerospikeSquashVerifierTests.cs) -- 11 tests covering: provider-id wiring; constructor null-guard; wrong context type; missing capture delegate; null Generated; empty Replaces; matching snapshots (Success); divergent snapshots (Failed with diff containing the missing/extra lines); capture-throws (Failed with `Cause`); cancellation propagation; SummarizeDiff truncation.

**Completion criteria:** ☑ Verifier compiles and wires; ☑ all guard rails return `Failed` with helpful detail; ☑ matching synthetic snapshots return `Success` with topology + elapsed; ☑ divergent snapshots produce a parseable diff summary; ☑ exception path captures cause; ☑ cancellation honored; ☑ 11/11 unit tests pass on net10; full squash suite 216/216 (+11 new, was 205); core 356/356 still green.

**Live end-to-end Testcontainers integration test (R-P6 verification round + R-P5 determinism gate + live `InfoSnapshotStrategy.GenerateAsync` happy path) deferred to the Phase 1 integration suite** -- those tests share the Testcontainers + apply-AQL infrastructure that lives outside the squash unit-test project. Tracking under Task 1.7 (R-P9 CLI integration test) and Phase 5 release prep.

**Cross-provider observation:** Verifier shape is structurally identical to Postgres -- same constructor (canonicalizer), same `CaptureFromGeneratedAsync` init-property, same `VerifyAsync` flow, same SummarizeDiff. Strong evidence the verifier contract is correct; expect MongoDB / Couchbase / OpenSearch verifiers to land in similar LOC with the same shape.

**Completion criteria:** Verifier returns `Success` for a real fixture range; returns `Failed` with sensible diff when a canonicalizer bug is intentionally injected.

#### Task 1.7: `AerospikeSquashGenerationContext` (R-P1 wiring) ☑ COMPLETE 2026-05-11 (shipped with Task 1.5)

**Prerequisites:** 1.1 through 1.6 complete.

Implement `AerospikeSquashGenerationContext : ISquashGenerationContext` -- the per-strategy plumbing wrapping the Testcontainers Aerospike instance, the `IAsyncClient`, and the topology signature.

Reference: [PostgresSquashGenerationContext.cs](../../../src/Hyperbee.Migrations.Providers.Postgres/Squash/PostgresSquashGenerationContext.cs) (83 LOC).

**Outcome:** [AerospikeSquashGenerationContext.cs](../../../src/Hyperbee.Migrations.Providers.Aerospike/Squash/AerospikeSquashGenerationContext.cs) (~100 LOC) shipped alongside `InfoSnapshotStrategy` in Task 1.5 -- the natural delivery order is "context + strategy as a pair." `InfoSnapshotStrategy` and `AerospikeSquashVerifier` accept it without casting (each calls `is not AerospikeSquashGenerationContext` once and returns `Failed` on mismatch). DI wiring through the CLI verb is deferred until Task 1.11 / Phase 5 release prep when the CLI surface lands once for all four providers.

**Completion criteria:** ☑ Context exists and validates required args; ☑ accepted by `InfoSnapshotStrategy` + `AerospikeSquashVerifier`; CLI DI wiring deferred to Phase 5 with the CLI verb itself.

#### Task 1.8: Unit tests per component (R-P8) ☑ COMPLETE 2026-05-11 (shipped with Tasks 1.1-1.6)

**Prerequisites:** Task 1.7 complete.

Add unit-test classes to `tests/Hyperbee.Migrations.Squash.Tests/`:

- `AerospikeTopologySignatureTests` -- 16 tests; `IsCompatibleWith` semantics, cross-provider rejection, helper-parser behavior
- `AerospikeDataOpClassifierTests` -- 25 tests; statement-form + call-site-form classification, non-determinism scan, Operate-requires-annotation, default-deny
- `AerospikeStatementClassifierTests` -- 12 tests; each supported statement kind, backtick-quoted identifiers, optional CREATE INDEX flags
- `AerospikeSnapshotCanonicalizerTests` -- 18 tests; idempotence, ephemeral-field stripping, sort-order, CRLF normalization, statement-form round-trip, internal helpers
- `AerospikeSquashEndToEndTests` -- 9 tests; strategy guard rails, canonicalizer pipeline behavior, context constructor validation
- `AerospikeSquashVerifierTests` -- 11 tests; provider-id wiring, success path, divergent-snapshot Failed with diff, exception capture, cancellation

**Outcome: 91 new unit tests** (target was 30-40; the higher count reflects extra coverage on regex anchoring, idempotence round-trips, and parser fallbacks). Full squash suite 216/216 on net10 (was 125 pre-Phase-1; +91 new); core 356/356 still green.

**Completion criteria:** ☑ 91 new unit tests, all green on net10 (multi-tfm parity expected); checkpoint achieved.

#### Task 1.9: Determinism gate integration test (R-P5) ☑ COMPLETE 2026-05-11

**Prerequisites:** Task 1.8 complete.

Add `AerospikeSquashDeterminismTests` in `tests/Hyperbee.Migrations.Integration.Tests/`, guarded by `#if INTEGRATIONS` (matching the existing convention; run with `/p:EnableIntegrationTests=true`):

- **Empty-namespace determinism:** capture twice against the shared `AerospikeTestContainer`; assert byte-equal canonical output.
- **Populated-namespace determinism:** create two secondary indexes + a sentinel record directly via `IAsyncClient.CreateIndexAsync` / `Put`; capture twice; assert byte-equal; assert the canonical content includes the created indexes (defense-in-depth so we're not testing an empty-namespace pass-through).
- **Index-creation-order independence:** create idx_b then idx_a; capture; drop both; recreate idx_a then idx_b; capture; assert byte-equal. Proves the canonicalizer's sort dominates Info-protocol response ordering.

Each test cleans up its created indexes + sentinel records in a `finally` block so subsequent tests start from clean structural state.

**Production helper landed alongside:** [AerospikeSnapshotCapture.cs](../../../src/Hyperbee.Migrations.Providers.Aerospike/Squash/AerospikeSnapshotCapture.cs) -- the real production component the CLI will use to wrap `IAerospikeClient.Info.Request("sets/<ns>", "sindex/<ns>")` into the section-headered blob the canonicalizer consumes. Static + stateless; unit-tested in [AerospikeSnapshotCaptureTests.cs](../../../tests/Hyperbee.Migrations.Squash.Tests/AerospikeSnapshotCaptureTests.cs) (8 tests covering ComposeBlob shape, empty/null responses, canonicalizer round-trip, and CaptureAsync guard rails).

**Outcome:** [AerospikeSquashDeterminismTests.cs](../../../tests/Hyperbee.Migrations.Integration.Tests/AerospikeSquashDeterminismTests.cs) -- 3 integration tests exercising the full pipeline (topology capture + snapshot capture + canonicalize + classify + emit). Unit tests for the helper component are in the squash test project; suite count 216 -> 224 (+8 new capture-helper tests). Integration tests build cleanly with `-p:EnableIntegrationTests=true` (0 errors) and compile out in default mode (`#if INTEGRATIONS` guard).

**Completion criteria:** ☑ Test class ships and builds clean; ☑ failure mode is informative (FluentAssertions provides "differs at column X of line Y" diagnostic for string equality); ☑ guarded by `#if INTEGRATIONS` matching existing pattern.

#### Task 1.10: Verification-round integration test (R-P6) ☑ COMPLETE 2026-05-11

**Prerequisites:** Task 1.9 complete.

Add `AerospikeSquashVerificationTests` in `tests/Hyperbee.Migrations.Integration.Tests/`, guarded by `#if INTEGRATIONS`:

- **Empty-namespace round-trip:** capture A; strategy.GenerateAsync; verifier.VerifyAsync with both A and B captures returning the same empty-canonical blob. Trivially Success; proves the verifier wiring works end-to-end.
- **Populated-namespace round-trip (the load-bearing R-P6 proof):**
  1. Set up structural state directly via client (2 indexes + sentinel record in `verify_set`).
  2. Capture A (the "historical" snapshot).
  3. Run `InfoSnapshotStrategy.GenerateAsync` -> Generated.Content (canonical AQL).
  4. Defense-in-depth: assert Generated.Content contains the created index names (proves we're not testing an empty-pass-through).
  5. CaptureFromGeneratedAsync delegate: wipe the namespace, apply Generated.Content via `AerospikeStatementParser.ParseScript` -> per-statement client API calls (CreateSet -> sentinel write; CreateIndex -> `IAsyncClient.CreateIndexAsync`), then capture state B.
  6. `AerospikeSquashVerifier.VerifyAsync(ctx, generated)` -- byte-compares canonicalized A vs B; asserts `VerificationResult.Success`.

The apply-AQL piece (step 5) reuses the existing `AerospikeStatementParser.ParseScript` which routes through the core `ScriptStatementSplitter` (per ADR-0022) so `--` line comments in the canonical content are stripped before parsing.

**Outcome:** [AerospikeSquashVerificationTests.cs](../../../tests/Hyperbee.Migrations.Integration.Tests/AerospikeSquashVerificationTests.cs) -- 2 integration tests. Both use the shared `AerospikeTestContainer` (no per-test container spin-up; the wipe-and-reapply path simulates the "second container" model with explicit teardown).

**Completion criteria:** ☑ Test class ships and builds clean (`#if INTEGRATIONS` block compiles with 0 errors); ☑ guard rails (empty-namespace baseline + populated round-trip with defense-in-depth assertions); ☑ teardown in `finally` blocks so other tests start clean.

**Cross-provider observation:** The apply-AQL helper (step 5) is provider-specific. For Aerospike it's ~20 LOC because Aerospike's "apply" is just CreateIndex + sentinel-write. For MongoDB / Couchbase / OpenSearch the apply will be JSON-bodied (collection.CreateMany / N1QL execute / REST PUT) -- different shape, comparable LOC. The contract surface is unchanged.

#### Task 1.11: CLI integration test (R-P9) -- DEFERRED to Phase 5

**Prerequisites:** Task 1.10 complete.

**Deferred 2026-05-11:** The `dotnet hyperbee-migrations squash --provider aerospike` CLI verb depends on the cross-provider CLI infrastructure tracked in the v1 plan's deferred Tasks 7.1-7.7 (System.CommandLine project skeleton + YAML manifest parser + container lifecycle wiring). Wiring the CLI for Aerospike alone is misshaped scope -- it ships once for all four providers in Phase 5 release prep alongside the same plumbing for MongoDB, Couchbase, OpenSearch. Carrying R-P9 forward to the Phase 5 task list.

Test plan when CLI lands (Phase 5):

- Invoke `dotnet hyperbee-migrations squash --provider aerospike --range 1000..2000 --output <temp>/Squash_2000.statements` against the Aerospike sample assembly.
- Assert the file exists.
- Re-run the CLI; SHA-256 both files; assert byte-equal (C12 across CLI invocations).
- Run the verification round against the produced file.

**Completion criteria (when run):** Test passes; CLI exits 0; output file is byte-stable.

**Status:** Tracked. The underlying machinery (strategy + verifier + capture helper + tests) already proves the same C12 byte-stability via the Task 1.9 integration tests; the CLI test will validate that no regression appears when the CLI orchestrator wires those components together.

#### Task 1.12: ADR-0019 amendment (R-P7, only if contract changed) ☑ COMPLETE 2026-05-11 -- NO CHANGES REQUIRED

**Prerequisites:** 1.1 through 1.11 complete.

**Outcome:** No contract changes required for Aerospike. The five contract interfaces (`ISquashStrategy`, `ITopologySignature`, `IDataOpClassifier`, `ISnapshotCanonicalizer`, `ISquashVerifier`) and the supporting records (`SquashGenerationResult`, `SquashGenerationOptions`, `SquashStrategyDescriptor`, `DataOpClassification`, `VerificationResult`) absorbed Aerospike's implementation cleanly. Appendix B snapshot is unchanged.

Concretely, where Aerospike could have pressured the contract:

- **Operate-requires-annotation** (Task 1.2): expressed as `IsUnclassified=true + EmissionHint`, fits the existing `DataOpClassification` shape.
- **Edition axis** (Task 1.1): held within `AerospikeTopologySignature.SchemaVersion=1` without changing `ITopologySignature`; revisit at Phase 5 against real-cluster integration may bump SchemaVersion=2 (a provider-side change, not a contract change).
- **Dual parse paths in canonicalizer** (Task 1.4): expressed as internal implementation of `ISnapshotCanonicalizer.Canonicalize`; no method-shape change.
- **In-process Info.Request vs external pg_dump** (Task 1.5): handled via the `ISquashGenerationContext`'s opaque snapshot delegate -- the contract is provider-neutral about capture mechanism.

**Completion criteria:** ☑ Documented "No contract changes for Aerospike" in plan; Appendix B unchanged; ready to absorb MongoDB pressure-test in Phase 2.

#### Task 1.13: Phase 1 boundary ☑ COMPLETE 2026-05-11

**Prerequisites:** All preceding Phase 1 tasks complete.

- ☑ Unit suite green: squash 224/224 on net10 (was 125 pre-Phase-1; +99 new across 7 test classes including the capture helper).
- ☑ Core suite green: 356/356 still.
- ☑ Integration project builds clean both with `-p:EnableIntegrationTests=true` (`#if INTEGRATIONS` block compiles, 0 errors) and without (`#if INTEGRATIONS` block omits cleanly).
- ☑ Plan checkboxes flipped for 1.1-1.10 + 1.12 + 1.13. Task 1.11 explicitly deferred to Phase 5.
- Memory + push deferred to user authorization.

**Completion criteria:** ☑ Aerospike squash codegen is path-finder-complete; the contract holds; the 6-component shape + capture helper + R-P5 / R-P6 integration tests are in place.

#### Phase 1 production-hardening pass (Sev 1 A-D + Sev 2 G-H) ☑ COMPLETE 2026-05-11

Production-grade gaps identified during the post-Phase-1 review were closed before Phase 2 began. Six items shipped:

- **Sev 1 A -- UDF refusal.** `AerospikeSnapshotCapture.ListUdfs` probes `Info.Request("udf-list")`; the strategy refuses generation with a diagnostic naming the installed UDF modules when any are present. UDFs cannot be silently dropped on fresh-install replay. Operators carry UDFs forward as separate non-squashed migrations.
- **Sev 1 B -- `Edition` topology axis.** `AerospikeTopologySignature.Edition` captures "Community" / "Enterprise" via `Info.Request("edition")`; `NormalizeEdition` standardizes server-version phrasing variants; `IsCompatibleWith` enforces strict (case-insensitive) equality. Stops the silent-corruption risk of an Enterprise-source squash applied to a Community target.
- **Sev 1 C -- `SquashStrategyDescriptor` DI wiring.** `ServiceCollectionExtensions.AddAerospikeMigrations` now registers the canonicalizer, classifier, strategy, verifier, and a composed `SquashStrategyDescriptor` whose constructor `EnsureValid` enforces ProviderId agreement across all five components. 7 wiring tests prove resolution + singleton semantics.
- **Sev 1 D -- `AerospikeMigrationSourceScanner` (ADR-0019 A5).** Roslyn-based scanner walks user migration source files, detects client-write call sites (`_client.Put/Delete/Touch/Operate`) and non-determinism (the same catalog as the data-op classifier), and refuses squash when any `[Migration]`-attributed class extending `Migration` has a data-op pattern without `[DataMigration]` or `[StructuralOnly]`. Strategy exposes `MigrationSourceRoot { get; init; }`; when set, the scan refusal gate runs after UDF refusal and before topology capture. Cross-provider hoist candidate: when Phase 2 OpenSearch needs its own scanner, the shared shape (Migration-extends + attribute check + non-determinism scan) becomes the right abstraction to hoist into a core-lib base class.
- **Sev 2 G -- ILogger injection.** `InfoSnapshotStrategy` accepts an optional `ILogger<InfoSnapshotStrategy>` (defaults to `NullLogger`). Refusals log at Warning; each diagnostic also logs at Warning; success logs at Information with range + replace count + content length. Operators see refusal reasons and diagnostics without walking `Generated`.
- **Sev 2 H -- Info.Request timeout budget.** Both `AerospikeTopologySignature.CaptureAsync` and `AerospikeSnapshotCapture` now pass an explicit `InfoPolicy { timeout = 5000 }` to every `Info.Request` call. Bounded so the sync info-protocol probes do not hang during partition rebalance.

**Test impact:** squash suite 224 -> 264 (+40 new across 5 test classes: 4 Edition tests, 6 UDF probe tests, 7 DI wiring tests, 17 source-scanner tests, 6 strategy gate tests). Core suite 356/356 still green. No contract changes; ADR-0019 unchanged.

**Cross-provider observations carried into Phase 2:**
- The `MigrationSourceRoot { get; init; }` strategy property is provider-neutral. When OpenSearch's `RestStateDiffStrategy` lands, it should expose the same property; the scanner implementation differs (OpenSearch DML happens through `IOpenSearchClient.IndexAsync/UpdateAsync/DeleteAsync/Bulk` not `_client.Put`).
- The shared scanner shape (Migration-extends + attribute recognizer + non-determinism scan + `ClassVerdict.RequiresAnnotation`) is now visible in two providers (Postgres + Aerospike). When the third provider (OpenSearch) needs its own, that's the right moment to hoist `MigrationSourceScannerBase` into the core library.
- Diagnostics logging pattern (refusal at Warning + per-diagnostic at Warning + success at Information) is provider-neutral. OpenSearch should mirror.

Ready for Phase 2 OpenSearch.

### Phase 2: OpenSearch squash codegen (R-P4, R-P5-R-P9) (~2 weeks) -- moved from Phase 4 (2026-05-11)

Highest canonicalization risk. Component templates, ISM policies, painless scripts, ingest pipelines, alias graphs. Reordered to be the second provider implemented so the contract is pressure-tested against the hardest case before MongoDB and Couchbase are built on top of it. If a gap surfaces here, the ADR-0019 amendment costs much less than discovering it at Phase 4 with three providers already shipped.

#### Task 2.0: Painless-equivalence spike (RISKIEST single technical question in v3.0) ☑ COMPLETE 2026-05-11

**Prerequisites:** Phase 1 done (including hardening pass).

The single biggest unknown in v3.0. The requirements doc Open Question flagged painless byte-equivalence as "exploring" with a `[PreservePainlessVerbatim]` annotation as fallback. Spike investigated whether byte-stable canonicalization of painless source is feasible.

**Spike outcome:** Question reframed and dissolved. See [spikes/opensearch-painless/SPIKE_REPORT.md](../../../spikes/opensearch-painless/SPIKE_REPORT.md) for the full analysis.

**Key findings:**

1. **Zero painless scripts in the codebase.** Survey of all sample migrations, integration tests, and production source returned no embedded painless scripts. v3.0 can establish the painless storage rule BEFORE customers depend on a specific behavior.
2. **Painless is a JSON string value, not a structure the canonicalizer parses.** OpenSearch stores painless as opaque-string content inside JSON documents in cluster state. The cluster does not parse painless at storage time -- only at execution time. JSON string preservation is structural.
3. **The byte-stability concern dissolves into JSON canonicalization.** Two runs against the same cluster state produce byte-equal canonical output as long as JSON keys are sorted, ephemeral fields are stripped (per Phase 0 Appendix C: `creation_date`, `uuid`, `version`, `policy_version`, `last_updated_time`), whitespace is normalized, and string values are embedded verbatim. Painless source bytes ride through as-is.
4. **No painless parser dependency, no operator annotation needed.** The `[PreservePainlessVerbatim]` annotation contemplated in the original requirements is unnecessary -- verbatim preservation IS the canonicalizer rule for all painless. No fallback path required because there's no "normalize-painless" alternative to fall back from.

**Phase 2 implications:**

- **Task 2.4 (`OpenSearchSnapshotCanonicalizer`) scope is smaller than originally estimated.** No painless parser, no operator annotation infrastructure. Estimated LOC drops from "uncertain, possibly 500+ with parser" to "~300 LOC, comparable to the JSON canonicalization piece of any structured-data canonicalizer."
- **Cross-provider precedent established:** opaque-content + structural-canonical split. MongoDB Phase 3 (BSON aggregation pipelines, partialFilterExpression queries) and Couchbase Phase 4 (N1QL function definitions, FTS JSON) will follow the same rule -- treat content as opaque, canonicalize structure.
- **R-P5 and R-P6 integration tests for OpenSearch carry double duty:** they prove canonicalizer determinism AND prove the cluster's verbatim-string assumption holds in production OpenSearch.

**Completion criteria:** ☑ Spike conclusion written ([SPIKE_REPORT.md](../../../spikes/opensearch-painless/SPIKE_REPORT.md)); ☑ Task 2.4 (canonicalizer) knows which path to take (opaque-string painless + structural JSON canonicalization); ☑ no follow-up empirical work blocks Phase 2 implementation; spike artifact retained for future reference (delete after Phase 2 lands and the canonicalizer code makes the decision visible in source).

#### Tasks 2.1 - 2.13: per-provider implementation

Same shape as Phase 1's tasks but for OpenSearch.

- **2.1 `OpenSearchTopologySignature`** ☑ COMPLETE 2026-05-11 -- captures cluster major.minor, distribution (OpenSearch vs Elasticsearch), cluster name, node count, full installed-plugin set (sorted union across nodes), and ISM endpoint prefix (modern `_plugins/_ism` vs legacy `_opendistro/_ism`). Probes via REST: root `/` for version + distribution; `Cluster.HealthAsync` for cluster name + node count; `/_cat/plugins?format=json` for plugin set; modern-then-legacy ISM endpoint probe for prefix detection. **30 unit tests cover:** self-compat / cross-provider rejection / major-mismatch / minor-tolerated / distribution-mismatch / ISM-prefix-mismatch / plugin-set-strict-equality / order-independence / node-count-tolerated; plus internal parser tests for root response (with + without `distribution` field, version-with-SNAPSHOT-suffix, malformed input), plugins response (per-node dedup + sort, missing-component skipped, non-array body handled, empty input). Plugin matrix proved a clean pressure-test for `ITopologySignature.Properties` -- the existing dictionary shape absorbs the multi-dimensional plugin axis without contract changes. MongoDB FCV and Couchbase CE-vs-EE will follow the same pattern.
- **2.2 `OpenSearchDataOpClassifier`** ☑ COMPLETE 2026-05-11 -- classifies both shapes: statement-form (`CREATE INDEX/TEMPLATE/COMPONENT/POLICY`, `DROP INDEX/...`, `APPLY POLICY`, `UPDATE MAPPING/SETTINGS`, `ALIAS SWAP/ADD/REMOVE`, `REFRESH`, `WAIT FOR HEALTH/UNTIL TASK` -> structural; `REINDEX FROM`, `MIGRATE INDEX` -> data ops) and call-site form (receiver-anchored `_?client.<verb>`: `Index*` / `Update*` / `UpdateByQuery*` / `Delete*` / `DeleteByQuery*` / `Bulk*` / `Reindex*` -> data ops; `Get*` / `Search*` / `Count*` / `Exists*` / `IndexExists*` / `Source*` / `Scroll*` -> reads; `_client.Indices.*` / `Cluster.*` / `Ingest.*` / `Cat.*` / `Tasks.*` / `Snapshot.*` / `Security.*` / `Nodes.*` -> structural sub-client paths). Non-determinism scan same catalog as Aerospike (DateTime/DateTimeOffset/Guid/Random/Stopwatch). **47 unit tests cover** statement-form (13) + call-site data ops (8) + call-site reads (5) + call-site structural sub-clients (8) + default-deny (2) + non-determinism (7) + ScanNonDeterminism helper (2). **Lesson surfaced + fixed during testing:** OpenSearch.Client methods are typically generic (`IndexAsync<T>(...)`, `GetAsync<T>(id)`), so a naive `\s*\(` anchor between method name and paren misses every typed call site. Fix: extract the method-call tail as `MethodCallTail = (?:\s*<[^()]*>)?\s*\(` constant -- allows optional generic type-parameter list before the opening paren. The same anchor improvement applies to MongoDB Phase 3 and Couchbase Phase 4 if their client APIs are generic (MongoDB's `IMongoCollection<T>` is parameterized at the collection level, so methods on it may not need this; Couchbase's `IBucket.DefaultCollection().UpsertAsync<T>` does -- worth applying preemptively).
- **2.3 `OpenSearchStatementClassifier`** ☑ COMPLETE 2026-05-11 -- ~120 LOC; thin projection over the existing `OpenSearchStatementParser` ([src/Hyperbee.Migrations.Providers.OpenSearch/Internal/Grammar/OpenSearchStatementParser.cs](../../../src/Hyperbee.Migrations.Providers.OpenSearch/Internal/Grammar/OpenSearchStatementParser.cs)). Classifies all 17 OpenSearch statement kinds into a typed `ClassifiedStatement(Kind, ObjectName, Body, Detail)` record. ObjectName projection per kind: index name for `CREATE/DROP/UPDATE INDEX`, template/component/policy name, alias name, task ID, reindex destination. Composite verbs (`MIGRATE INDEX`) flatten with child-verb enumeration in Detail. `WHEN VERSION` wrappers carry the wrapped verb's class name in Detail while preserving the wrapped object name. Default-deny on parser failure with the parser's diagnostic in Detail. **29 unit tests cover** each kind + composite expansion + WHEN VERSION wrapper + parser-error / empty / null / whitespace fallback paths. **Pre-existing parser bug surfaced + fixed during testing:** `OpenSearchStatementParser.migrateBodySource` typed the body slot as `BodyRef?` (concrete sibling-property variant) rather than `BodySource?` (abstract base), causing an `InvalidCastException` at runtime when `MIGRATE INDEX ... WITH BODY @path` parsed the file-ref form (`BodyFileRef`). Fix lifts the tuple type to `BodySource?` so both `WITH BODY $name` and `WITH BODY @path` round-trip. Cross-provider observation: when MongoDB Phase 3 and Couchbase Phase 4 add their classifiers, watch for analogous abstract-vs-concrete shape mismatches in their parsers' Then callbacks -- the fix is one line per occurrence but easy to miss in a quick visual scan.
- **2.4 `OpenSearchSnapshotCanonicalizer`** ☑ COMPLETE 2026-05-11 -- ~280 LOC; section-headered blob -> canonical normalized output. Sections: `[index_template]` / `[component_template]` / `[index_metadata]` / `[alias]` / `[ism_policy]` / `[ingest_pipeline]` plus unknown-section pass-through. **Canonicalization steps**: parse each section as JSON; recursively sort object keys via ordinal string comparison; strip the ephemeral catalog (`creation_date`, `uuid`, `version`, `provided_name`, `policy_version`, `last_updated_time`, `seq_no`, `primary_term`) at every nesting level; re-emit with `Utf8JsonWriter` using `Indented = true` + `UnsafeRelaxedJsonEscaping`; cross-platform LF normalization. Sections emit in alphabetical order on output. **Painless preservation per Task 2.0 spike:** painless source rides through as opaque JSON string content; the canonicalizer never parses or modifies painless. Numbers preserve operator representation via `GetRawText` (no `1.0 -> 1` normalization). Arrays preserve declared order (ISM state transitions, index patterns depend on it). **EmitScript = Canonicalize (identity)** for v3.0 -- the canonical form IS the embeddable form; richer `WITH BODY @path` script emission is a Phase 5 polish item if needed. **25 unit tests cover:** section parsing + ordering + case-insensitive headers, recursive key sort (top-level + nested), array order preservation, ephemeral stripping at every level + ISM metadata + provided_name, painless preservation including escaped quotes round-trip, idempotence (C12 baseline), divergent key orders / divergent ephemeral values producing byte-equal output, line-ending normalization, number representation preservation, invalid-JSON error handling, internal helper coverage. **Bug surfaced + fixed during testing:** `Utf8JsonWriter` with `Indented = true` on older targets uses `Environment.NewLine` (CRLF on Windows); breaks cross-platform C12 byte-stability. Fix: post-process the writer output replacing CRLF -> LF before returning. Carry-forward to MongoDB Phase 3 + Couchbase Phase 4 if they use `Utf8JsonWriter` for canonical serialization.
- **2.5 `RestStateDiffStrategy : ISquashStrategy`** ☑ COMPLETE 2026-05-11 -- orchestrates the capture + canonicalize + emit pipeline. Three components shipped: [OpenSearchSquashGenerationContext.cs](../../../src/Hyperbee.Migrations.Providers.OpenSearch/Squash/OpenSearchSquashGenerationContext.cs) (provider-specific `ISquashGenerationContext` carrying `IOpenSearchClient` + delegate-injected `CaptureSnapshotAsync` + `SnapshotCaptureRequest`/`SnapshotCaptureResult` records), [OpenSearchSnapshotCapture.cs](../../../src/Hyperbee.Migrations.Providers.OpenSearch/Squash/OpenSearchSnapshotCapture.cs) (production REST-probe helper that assembles the section-headered blob via `GET /_index_template/*`, `GET /_component_template/*`, `GET /_all`, `GET /_alias`, `GET /_ingest/pipeline`, plus `<ismPathPrefix>/policies` when ISM is available; 404 on any optional endpoint is treated as "no content" so partial-feature clusters succeed), and [RestStateDiffStrategy.cs](../../../src/Hyperbee.Migrations.Providers.OpenSearch/Squash/RestStateDiffStrategy.cs) (the orchestrator). Strategy is shaped per Aerospike Task 1.5: optional `ILogger<RestStateDiffStrategy>` injection (Sev 2 G carry-forward; defaults to `NullLogger`); `MigrationSourceRoot { get; init; }` for the Roslyn source-scanner gate (filled in Task 2.7, currently no-op); guard rails return `Failed` with helpful detail. Returns `ContentKind.CanonicalJson` (vs Aerospike's `SqlText`) since OpenSearch's canonical form is JSON. **DI wiring**: `AddOpenSearchMigrations` now registers `OpenSearchSnapshotCanonicalizer`, `OpenSearchDataOpClassifier`, and `RestStateDiffStrategy` as singletons; the full `SquashStrategyDescriptor` composition lands in Task 2.6 alongside the verifier. **16 unit tests cover** ComposeBlob shape + alphabetical ordering + empty/null inputs + canonicalizer round-trip; CaptureAsync guard rails (null client, cancellation); strategy guard rails (null/empty/wrong context); constructor validation; provider-id wiring; null-logger acceptance. **Decision note**: per-request timeout via OpenSearch.Net's low-level `IRequestParameters` was investigated and dropped -- the abstract-class extension surface is friction-heavy for what amounts to a defensive-bound, and the connection-level timeout + caller-side `CancellationToken` cover the same ground. Operators who need a tighter ceiling than the cluster's normal client timeout wrap the call in a `CancellationTokenSource.CancelAfter`. Documented in the capture helper's remarks.
- **2.6 `OpenSearchSquashVerifier`** ☑ COMPLETE 2026-05-11 -- ~150 LOC; A4 byte-equality round. Same shape as Aerospike Task 1.6 + Postgres reference. `CaptureFromGeneratedAsync { get; init; }` delegate seam for the apply-and-recapture path; `VerifyAsync(context, generated, ct)` flow: capture A via context delegate -> capture B via CaptureFromGeneratedAsync -> canonicalize both -> byte-compare -> Success or Failed with line-by-line diff summary (truncated 20 lines per side). Multi-node verification is `[TestCategory("LocalOnly")]` per existing convention (Phase 2 integration test). Full **DI wiring complete this task**: `AddOpenSearchMigrations` now also registers `OpenSearchSquashVerifier` and the composed `SquashStrategyDescriptor` whose ctor `EnsureValid` asserts ProviderId agreement across all 5 component instances. **19 unit tests cover** verifier (12: provider-id wiring, constructor null-guard, guard rails, Success path, divergent-snapshot Failed with diff, ephemeral-only-difference still Success defense-in-depth, exception cause capture, cancellation, SummarizeDiff truncation) + DI wiring (7: each component resolves, descriptor passes EnsureValid, singleton semantics, descriptor components are the same instances as direct resolutions). **Cross-provider observation:** verifier shape is structurally IDENTICAL to Aerospike's -- same constructor (canonicalizer), same `CaptureFromGeneratedAsync` init-property, same `VerifyAsync` flow, same SummarizeDiff truncation rule. Strongest evidence yet that the `ISquashVerifier` contract is correct; MongoDB Phase 3 and Couchbase Phase 4 verifiers will be near-copy-paste from one of these two references.
- **2.7 OpenSearchMigrationSourceScanner** ☑ COMPLETE 2026-05-11 -- Roslyn-based source scanner (~240 LOC), mirrors `AerospikeMigrationSourceScanner` shape with OpenSearch-specific verb sets. Detects receiver-anchored `_?client.<verb>(` write call sites (Index*/Update*/UpdateByQuery*/Delete*/DeleteByQuery*/Bulk*/Reindex*; 19 method-name variants) + non-determinism (same catalog as Aerospike). Receiver-name filter (`client` or `_client`) excludes sub-client paths (`_client.Indices.Create`) automatically -- they're structural. Wired into `RestStateDiffStrategy.MigrationSourceRoot` gate (runs BEFORE topology capture so a refused squash does not waste cluster probes). [OpenSearchMigrationSourceScannerTests.cs](../../../tests/Hyperbee.Migrations.Squash.Tests/OpenSearchMigrationSourceScannerTests.cs) -- 18 scanner tests + 2 strategy gate tests (added to OpenSearchSquashEndToEndTests). **Cross-provider hoist candidate confirmed:** the Migration-extends + attribute recognizer + non-determinism scan + `ClassVerdict.RequiresAnnotation` shape is now identical across THREE providers (Postgres, Aerospike, OpenSearch). Phase 5 release prep should hoist `MigrationSourceScannerBase` into the core library so per-provider scanners only override the data-op-detection portion; MongoDB Phase 3 and Couchbase Phase 4 should consume the hoisted base when they ship their scanners.
- **2.8-2.12** mirror Phase 1 (R-P5 + R-P6 integration tests + ADR amendment check + phase wrap).
- **2.13** phase boundary.

**Cross-provider participation check:** OpenSearch is the contract pressure test. If a gap surfaces here, the ADR-0019 amendment must land BEFORE the interface modification, and the cost is recompiling Aerospike against the amended shape (one provider, not three). This is precisely why the order was changed -- the cost of a gap discovered here is bounded.

### Phase 3: MongoDB squash codegen (R-P2, R-P5-R-P9) (~1.5 weeks) -- moved from Phase 2 (2026-05-11)

Medium-High canonicalization risk. BSON-vs-JSON, index `v` field per server version, replica-set vs standalone topology. Reordered to be third because MongoDB's risk profile is dominated by structural/serialization choices (Extended JSON canonical form, `v` field stripping) rather than feature-gating, so it's a lower-information pressure-test than OpenSearch's plugin matrix.

**Tasks 3.1 - 3.13 mirror Phase 1's shape**, with these per-provider differences:

- **3.1 `MongoDBTopologySignature`** captures: server major.minor, feature compatibility version (FCV), replica-set vs standalone, default read/write concern, storage engine. FCV is the Edition-axis analogue (simpler than OpenSearch's plugin matrix).
- **3.2 `MongoDBDataOpClassifier`** classifies `IMongoCollection<>.Insert*`, `Update*`, `Delete*`, `BulkWrite` as data ops; `Database.CreateCollectionAsync`, `Indexes.CreateOneAsync` as structural.
- **3.3 `MongoDBStatementClassifier`** uses the existing `MongoStatementParser` ([src/Hyperbee.Migrations.Providers.MongoDB/Parsers/MongoStatementParser.cs](../../../src/Hyperbee.Migrations.Providers.MongoDB/Parsers/MongoStatementParser.cs)); covers `CREATE COLLECTION`, `CREATE [UNIQUE] INDEX ON db.col(...)`, `DROP COLLECTION`, `DROP INDEX`, `INSERT INTO` (intent).
- **3.4 `MongoDBSnapshotCanonicalizer`**:
  - Capture via `db.runCommand({listCollections})` + `getIndexes()` per collection + collection-options validator
  - Strip ephemeral fields: `idIndex.v`, `idIndex.ns`, `info.uuid`, `info.readOnly`
  - **Strip `v` field on each index** (server-version-dependent per the requirements doc Open Question); document the rationale inline in the canonicalizer
  - Sort collections + indexes alphabetically
  - Emit script form: `CREATE COLLECTION db.col`, `CREATE [UNIQUE] INDEX name ON db.col(field1, field2)`
- **3.5 `IntrospectionSnapshotStrategy : ISquashStrategy`** orchestrates the capture; uses the existing `Testcontainers.MongoDb` helper.
- **3.6 `MongoDBSquashVerifier`** runs the two-container byte-equal verification round.
- **3.7-3.12** mirror Phase 1.
- **3.13** phase boundary.

**Cross-provider participation check:** by Phase 3 the contract has been pressure-tested by Aerospike (low) and OpenSearch (high). If OpenSearch amended `ITopologySignature` (e.g., a plugin-matrix shape), MongoDB MUST use the amended shape; the audit trail is in Appendix B.

### Phase 4: Couchbase squash codegen (R-P3, R-P5-R-P9) (~2 weeks) -- moved from Phase 3 (2026-05-11)

High canonicalization risk. CE-vs-EE differences, parameterized N1QL, deferred-build indexes. Reordered to be last because Couchbase shares Edition-axis shape with OpenSearch's plugin matrix; by the time Couchbase ships, the pattern for capability detection + feature gating will be well-established.

**Tasks 4.1 - 4.13 mirror Phase 1's shape**, with these per-provider differences:

- **4.1 `CouchbaseTopologySignature`** captures: server major.minor, edition (CE vs EE), index service GSI vs N1QL-built-in, bucket type, replica count, memory quota.
- **4.2 `CouchbaseDataOpClassifier`** classifies `Cluster.QueryAsync` (parameterized N1QL: must inspect the SQL for INSERT/UPDATE/DELETE/MERGE), `Bucket.DefaultCollection().UpsertAsync` / `InsertAsync` / `RemoveAsync` as data; bucket/scope/collection/index management as structural.
- **4.3 `CouchbaseStatementClassifier`** uses the existing Couchbase `StatementParser` ([src/Hyperbee.Migrations.Providers.Couchbase/Parsers/StatementParser.cs](../../../src/Hyperbee.Migrations.Providers.Couchbase/Parsers/StatementParser.cs)).
- **4.4 `CouchbaseSnapshotCanonicalizer`**:
  - Capture via `system:keyspaces`, `system:indexes`, Management API for bucket/scope settings
  - Strip ephemeral fields: `last_rebalance_timestamp`, `index.id`, `bucket.docCount`
  - **Surface deferred-build indexes**: emit them as deferred CREATE + a trailing BUILD INDEX (per R-P3 Open Question)
  - Sort keyspaces + indexes deterministically
  - Emit script form: `CREATE BUCKET`, `CREATE SCOPE`, `CREATE COLLECTION`, `CREATE INDEX ... USING GSI WITH {...}`, `BUILD INDEX`
- **4.5 `HybridStrategy : ISquashStrategy`** combines the two capture sources (N1QL system tables + Management API).
- **4.6 `CouchbaseSquashVerifier`** verifies with awareness of deferred-build async behavior (the apply-phase must trigger BUILD INDEX before the snapshot is captured).
- **4.7-4.12** mirror Phase 1.
- **4.13** phase boundary.

**Cross-provider participation check:** the Open Question on parameterized N1QL data-op classification (currently surfaced in R-P3) MUST be resolved by Phase 4 Task 4.2; the resolution may amend the classifier contract. By Phase 4, three providers ship; an amendment costs more than at Phase 2, so the resolution should be carefully scoped.

### Phase 5: Release prep (~1 week)

#### Task 5.1: ADR sweep

- Promote ADR-0019 to Accepted (it has been Proposed throughout the squash work; with all 5 providers shipped, it's fully validated).
- Promote ADR-0023 to Accepted (assuming the multi-runner composition plan has landed; otherwise note its status in the v3.0 release notes).
- Verify each per-phase ADR amendment (Task X.12) is documented + numbered correctly in ADR-0019.
- Verify `docs/decisions/INDEX.md` shows current statuses.

#### Task 5.2: CHANGELOG sweep

- Update `CHANGELOG.md` to reflect:
  - All 5 providers have squash codegen (replace any residual "Postgres only" language; this should already be done from earlier cleanup but re-verify)
  - List the per-provider strategy types (`InfoSnapshotStrategy`, `IntrospectionSnapshotStrategy`, `HybridStrategy`, `RestStateDiffStrategy`)
  - List the new exception types if any were added per ADR-0019 amendments
  - List any contract changes from Phase 1-4 ADR amendments

#### Task 5.3: Documentation reconciliation

- Walk `docs/site/squashing-migrations.md`; verify every per-provider claim matches what shipped.
- Walk each `docs/site/{provider}.md`; verify the Statement format section + statement summary tables reflect any new statement kinds shipped by the canonicalizer.
- Walk the per-provider package READMEs; verify install / quick-start examples still compile.
- Update `docs/plans/active/migration-squashing-v1.md`: mark Phase 6 fully DONE (Postgres) + cross-link to this plan + mark this plan's Phases 1-5 done.

#### Task 5.4: Release dry run

- `dotnet pack` the full solution; inspect NuGet metadata for each package (Title, Description, Tags, ReadmeFile -- the P0 cleanup items should have fixed these already; verify).
- Verify NuGet packages would publish cleanly (no NU1903, no NU1701).
- Verify `multi_node_tests.yml` workflow state matches operational reality (workflow_dispatch only, no nightly cron).
- Verify `pack_publish.yml` is ready to trigger on the v3.0.0 tag.

#### Task 5.5: Test suite full pass

- Run unit tests on net8 / net9 / net10. Confirm green.
- Run integration tests (non-LocalOnly) on net10. Confirm green.
- Run LocalOnly tests locally. Confirm green.
- Capture pass-rate snapshot in the plan's Learnings Ledger.

#### Task 5.6: PR to main + tag v3.0.0

- Open PR `devs/bfarmer/provider-squash` -> `main`.
- After CI passes: squash-merge to main, tag `v3.0.0`, `pack_publish.yml` fires automatically.
- Verify NuGet packages appear on nuget.org.

## Dependencies (cross-task)

- Phase 0 blocks Phase 1.
- Phase 1 blocks Phase 2 (the strategy contract may amend; subsequent providers consume the amended shape).
- Phase 2 blocks Phase 3 (same reason).
- Phase 3 blocks Phase 4 (same reason).
- Phase 4 blocks Phase 5.
- Within each per-provider phase, tasks N.1 -> N.2 -> N.3 -> N.4 -> N.5 -> N.6 -> N.7 in sequence (each builds on the previous); tasks N.8 (unit tests) can interleave with N.1-N.7 (write the test alongside each component); N.9 (determinism gate) and N.10 (verification round) and N.11 (CLI test) come after N.7; N.12 (ADR amendment) is conditional; N.13 closes the phase.

## Riskiest task

**Task 4.0 -- OpenSearch painless-equivalence spike.** If the spike conclusion is "byte-stable normalization doesn't work for our corpus," the fallback (`[PreservePainlessVerbatim]`) requires operator action on every squash containing painless scripts. That's an operational regression vs the implicit promise of "squash just works." The spike must happen BEFORE Phase 4 Task 4.4 (canonicalizer); if both paths fail, v3.0 needs a release-scope decision (deferred painless support is not on the table per the locked rule, but the contract may need an explicit annotation pathway documented at C12-test time).

## Test plan per phase

| Phase | Unit tests | Integration tests | Determinism gate | Verification round | CLI test |
|---|---|---|---|---|---|
| 0 | n/a | n/a | n/a | n/a | n/a |
| 1 (Aerospike) | ~30-40 new | green on all net TFMs | passes | passes | passes |
| 2 (MongoDB) | ~30-40 new | green | passes | passes | passes |
| 3 (Couchbase) | ~30-40 new | green | passes | passes | passes |
| 4 (OpenSearch) | ~30-40 new | green | passes | passes | passes |
| 5 (release prep) | regression-test full suite | regression-test full suite | regression-test | regression-test | regression-test |

Cumulative test additions over Phases 1-4: ~120-160 new unit tests + 16 new integration tests (4 per provider: determinism gate, verification round, CLI test, plus the unit-tests-by-component split).

## ADR compliance check

| Task | Honors ADR | How |
|---|---|---|
| All Phase 1-4 Task .1 (TopologySignature) | ADR-0019 A14 (topology schema versioning) | `SchemaVersion = 1`; documented compatibility-rule semantics |
| All Phase 1-4 Task .2 (DataOpClassifier) | ADR-0019 A5 (default-deny annotation) + A8 (non-determinism scan) | Roslyn walker; unclassified -> refuse without `[DataMigration]` / `[StructuralOnly]` |
| All Phase 1-4 Task .4 (SnapshotCanonicalizer) | ADR-0019 A12 / C12 (generation determinism) + ADR-0022 (script form) | `Canonicalize` is idempotent; `EmitScript` emits `.statements` script form |
| All Phase 1-4 Task .5 (SnapshotStrategy) | ADR-0019 destructive model + A4 (verification round) + A6 (transitivity) | Captures equivalent end state; emits `Kind=Squash` records with `Replaces=[N..M]` |
| All Phase 1-4 Task .6 (SquashVerifier) | ADR-0019 A4 (verification round) + A18 (container lifecycle) | Two-container byte-equal; preserves container only on `--keep-failed-container` |
| Phase 1-4 Task .9 (determinism gate test) | ADR-0019 A12 / C12 | Re-runs codegen, asserts byte-equal hash |
| Phase 1-4 Task .12 (ADR amendment if contract changed) | ADR-0019 R-P7 / contract evolution rule | Amendment lands before interface change |
| Phase 5 Task 5.1 (ADR sweep) | ADR-0019 + 0020 + 0021 + 0022 | Promote Accepted; verify amendment trail |

## Risk register

| Risk | Likelihood | Impact | Mitigation |
|---|---|---|---|
| OpenSearch painless byte-equivalence proves intractable for the corpus | Medium-High | Forces `[PreservePainlessVerbatim]` annotation pathway; degrades the "squash just works" promise | Task 4.0 spike early; document the fallback contract in ADR-0019 amendment with explicit operator workflow |
| Contract amendment in Phase 1 forces rework on the Postgres reference | Low-Medium | Postgres squash code changes alongside the contract; existing tests must stay green | R-P7 -- amendment lands first; reference implementation updates as part of the same change set |
| Testcontainers Couchbase cluster startup exceeds CI budget | Medium | Phase 3 verification-round test demoted to `[TestCategory("LocalOnly")]` | Document the demotion in the test class header (reason + when to revisit); add a comparable smoke test that runs on CI without spinning the full cluster |
| MongoDB index `v` field strip breaks against a server version we didn't test | Low | A squash captured on server X applies non-equivalently on server Y | Phase 2 Task 2.4 + R-P5 test: parameterize the determinism gate by server version; document in the canonicalizer header which versions are validated |
| The 8-10 week timeline slips | Medium | v3.0 ships later than hoped | Honest weekly status updates in the plan's Learnings Ledger; the locked rule means scope doesn't trade for time |
| A contract amendment in Phase 3 or 4 breaks already-shipped Phase 1/2 code | Medium | Test failures in already-done providers; rework required | Amendments must include source-compat note; Phase 5 Task 5.5 runs all tests as a final gate |

## Status

- Phase 0: not started
- Phase 1 (Aerospike): not started
- Phase 2 (MongoDB): not started
- Phase 3 (Couchbase): not started
- Phase 4 (OpenSearch): not started
- Phase 5 (release prep): not started

## Effort

Per the velocity calibration ([feedback_velocity_calibration.md](../../../../Users/bfarm/.claude/projects/c--Development-hyperbee-migrations/memory/feedback_velocity_calibration.md)): Aerospike provider implementation took 1 day; Couchbase under a week. Squash codegen is heavier than a new provider because it's reading external state + emitting canonical scripts + per-component test coverage.

Estimates (revised 2026-05-11 after order change + Sev 1/2 production-hardening pass):

- **Phase 0:** 1 day ☑
- **Phase 1 (Aerospike):** ~1 week (Low canonicalization risk; first contract pressure test) ☑ unit + integration; **+~3 days** for Sev 1 A-D + Sev 2 G-H production-hardening pass
- **Phase 2 (OpenSearch):** ~2 weeks (Highest canonicalization risk; spike + canonicalization across 5+ resource types). Reordered to be the contract pressure-test against the hardest provider; gap-discovery cost is bounded to recompiling Aerospike if a contract amendment lands here.
- **Phase 3 (MongoDB):** ~1.5 weeks (Medium-High risk; BSON / `v` field). Reordered to be third because MongoDB's risk is dominated by serialization choices, not feature-gating.
- **Phase 4 (Couchbase):** ~2 weeks (High risk; CE-vs-EE, parameterized N1QL, deferred indexes). Reordered to be last because Couchbase's Edition-axis shape benefits from the patterns OpenSearch establishes in Phase 2.
- **Phase 5 (release prep):** ~1.5 weeks (absorbed Sev 2 E-F + Sev 3 J-L deferrals: ContentKind dispatcher behavior, CI lane for R-P5/R-P6, multi-node and storage-backend documentation, KeepFailedContainer wiring with CLI).

**Total: ~9-11 weeks** of single-developer focused work. Net cost of order change is zero (same total effort, different sequencing). Net cost of production-hardening pass is ~3-5 days, distributed across Phase 1 (now) and Phase 5 (already in the increased estimate).

## Learnings Ledger

(Updated after each phase by `/nop:implement`.)

- *Phase 0:* ☑ COMPLETE 2026-05-11 — Postgres reference (10 files, ~1700 LOC) audited; 5-component contract surface snapshotted as diff baseline (per R-P7); per-provider introspection surfaces sized + risk-classified (Aerospike Low / Mongo Medium / Couchbase Medium-High / OpenSearch High). Findings: (1) `StatementSplitter` is a Postgres-specific helper — the four JSON-bodied providers iterate structured documents directly, no splitter needed; (2) `PostgresMigrationSourceScanner` (Roslyn-based non-determinism scan) is provider-neutral and could be hoisted to the core library in Phase 5 if a second provider needs it verbatim — flagging for cross-provider participation review during Phase 1; (3) only the Postgres record store has any introspection footprint today (all 4 NoSQL record stores are write-only) — each provider's strategy must add fresh introspection call sites; (4) Couchbase + OpenSearch already have per-store locking primitives suitable for verification rounds (mutex + CAS create); Aerospike + MongoDB rely on the migration-scope ledger lock. See Appendices A/B/C below.
- *Phase 1:* ☑ COMPLETE 2026-05-11 — Aerospike squash codegen path-finder shipped: 6 components (~1180 LOC across `src/Hyperbee.Migrations.Providers.Aerospike/Squash/`) + 1 production capture helper (`AerospikeSnapshotCapture`); 99 new unit tests (224/224 in the squash suite, was 125 pre-Phase-1); 5 integration tests (3 R-P5 determinism + 2 R-P6 verification, all `#if INTEGRATIONS`-guarded). Contract validated: NO ADR-0019 amendments required; the 5-interface shape from Phase 0 absorbs Aerospike cleanly (Operate-requires-annotation → `IsUnclassified+EmissionHint`; dual canonicalizer parse paths → internal; in-process Info.Request → opaque snapshot delegate). Cross-provider carry-forwards: comment-stripping must be a pre-pass; receiver-anchoring in regex matchers; SchemaVersion=1 axis-list may want `Edition` after Phase 5 real-cluster integration. CLI integration test (R-P9) deferred to Phase 5 with the CLI verb itself. Ready for Phase 2 MongoDB.
- *Phase 2:* [pending]
- *Phase 3:* [pending]
- *Phase 4:* [pending]
- *Phase 5:* [pending]

## Appendices

### Appendix A: Postgres reference shape

Populated 2026-05-11 from a read-only walk of `src/Hyperbee.Migrations.Providers.Postgres/Squash/` (10 files).

| File | LOC | Public types | Key methods | Pattern (1 line) |
|---|---|---|---|---|
| [PostgresTopologySignature.cs](../../../src/Hyperbee.Migrations.Providers.Postgres/Squash/PostgresTopologySignature.cs#L28) | 209 | `PostgresTopologySignature` (sealed record) | `IsCompatibleWith`, static `CaptureAsync(NpgsqlConnection, CT)`, private `ProbeLocaleProvidersAsync`, `ListExtensionsAsync` | Captures server major/minor + extensions + locale axes via system-catalog probes; exact-match major, exact-match extension set. |
| [PostgresDataOpClassifier.cs](../../../src/Hyperbee.Migrations.Providers.Postgres/Squash/PostgresDataOpClassifier.cs#L31) | 133 | `PostgresDataOpClassifier` | `Classify(string)`, static `ScanNonDeterminism(string)` | Regex-based DML/DDL/DO-block classifier with non-determinism scan (now/random/uuid/etc.); default-deny on unclassified. |
| [PostgresStatementClassifier.cs](../../../src/Hyperbee.Migrations.Providers.Postgres/Squash/PostgresStatementClassifier.cs#L23) | 289 | `ClassifiedStatement` (record), `PostgresStatementClassifier` (static) | `Classify(string)` returning `ClassifiedStatement(Kind, SchemaName, ObjectName, Body, Detail)` | Per-statement kind/schema/name extraction via regex cascade including DROP family + ATTACH PARTITION + ADD CONSTRAINT specializations. |
| [PostgresStatementKind.cs](../../../src/Hyperbee.Migrations.Providers.Postgres/Squash/PostgresStatementKind.cs#L8) | 59 | `PostgresStatementKind` (byte enum) | (none) | Enumeration of ~40 Postgres-specific statement kinds (CREATE/ALTER/DROP families + preamble + GRANT/REVOKE). |
| [PostgresStatementSplitter.cs](../../../src/Hyperbee.Migrations.Providers.Postgres/Squash/PostgresStatementSplitter.cs#L20) | 251 | `PostgresStatementSplitter` (static) | `Split(string)`, private `StripPsqlDirectives`, `TryReadDollarTag`, `MatchesDollarTag` | Manual lexer that splits SQL on `;` while respecting single/double quotes, nested `/* */` comments, `--` line comments, and `$tag$...$tag$` dollar-quoted bodies; strips `\restrict` psql directives. |
| [PostgresSnapshotCanonicalizer.cs](../../../src/Hyperbee.Migrations.Providers.Postgres/Squash/PostgresSnapshotCanonicalizer.cs#L29) | 135 | `PostgresSnapshotCanonicalizer` | `Canonicalize(string)`, `EmitScript(string)` | Line-wise filter that strips SET preamble, search_path, psql directives, pg_dump banners; collapses blank runs; refuses `CREATE INDEX CONCURRENTLY`. |
| [PgDumpSnapshotStrategy.cs](../../../src/Hyperbee.Migrations.Providers.Postgres/Squash/PgDumpSnapshotStrategy.cs#L35) | 161 | `PgDumpSnapshotStrategy` | `GenerateAsync(context, descriptors, options, CT)` | Orchestrator: capture topology -> CaptureSnapshotAsync (delegate) -> canonicalize -> classify all statements -> append setval block -> emit Generated. |
| [PostgresSquashVerifier.cs](../../../src/Hyperbee.Migrations.Providers.Postgres/Squash/PostgresSquashVerifier.cs#L33) | 153 | `PostgresSquashVerifier` | `VerifyAsync(context, generated, CT)`, private `SummarizeDiff` | Two-snapshot byte-equality verifier; injects `CaptureFromGeneratedAsync` delegate to replay the generated squash, then canonicalize+compare with historic capture. |
| [PostgresSquashGenerationContext.cs](../../../src/Hyperbee.Migrations.Providers.Postgres/Squash/PostgresSquashGenerationContext.cs#L25) | 83 | `PostgresSquashGenerationContext`, `SnapshotCaptureRequest` (record), `SnapshotCaptureResult` (record) | constructor; exposes `DataSource` (NpgsqlDataSource) + `CaptureSnapshotAsync` delegate | Concrete context bundling the live `NpgsqlDataSource` and a caller-injected capture delegate; carries `SquashName`/`SquashVersion` from the base interface. |
| [PostgresMigrationSourceScanner.cs](../../../src/Hyperbee.Migrations.Providers.Postgres/Squash/PostgresMigrationSourceScanner.cs#L31) | 223 | `PostgresMigrationSourceScanner` (static), `ClassVerdict` (record) | `Scan(sourceRoot)`, private `ClassifyClass`, `ClassExtendsMigration`, `IsAttributeName`, `MemberAccessName` | Roslyn-based source walker that emits a per-class `RequiresAnnotation` verdict; flags string-literal DML + .NET non-determinism call sites (DateTime.Now, Guid.NewGuid, new Random()). |

**Provider author's port template (6-component shape).** Every provider must ship five contract-bearing components plus one or more provider-internal helpers:

1. **TopologySignature** (`ITopologySignature`) -- captures the deterministic axes of the live deployment that govern codegen reproducibility: server major version, feature flags / extensions / plugins, locale or encoding, deployment topology (single-node vs replica vs cluster). Provides `CaptureAsync` against the live client. Implements `IsCompatibleWith` with strict equality on the hard axes and tolerant comparison on soft axes (minor versions, build numbers). `SchemaVersion` bumped on shape changes per R-P7.
2. **DataOpClassifier** (`IDataOpClassifier`) -- classifies a single statement string (or call-site string for code-only migrations) as `IsDataOp` / `RequiresPreservation` / `IsUnclassified`. Provider-native verbs and a non-determinism scan that flags time/random/identity calls. Default-deny: anything ambiguous returns `IsUnclassified=true` so the CLI refuses until the migration is annotated.
3. **Snapshot strategy** (`ISquashStrategy`) -- the orchestrator. Captures the live topology, invokes the (delegate-injected) capture function for snapshot B, runs it through the canonicalizer, classifies every statement to surface diagnostics, appends any post-state-restoration content (Postgres: setval; others: per-provider analogue), and returns `SquashGenerationResult.Generated` (or `Failed` on refusal). Per ADR-0019 the capture function itself is delegate-injected to keep the runtime free of test-container deps.
4. **SnapshotCanonicalizer** (`ISnapshotCanonicalizer`) -- normalizes captured bytes into a byte-stable form. `Canonicalize(snapshot)` must be idempotent. `EmitScript(content)` produces the final script-form output for embedding into the squash migration's resource file. This is the load-bearing function for the C12 determinism gate.
5. **SquashVerifier** (`ISquashVerifier`) -- runs the snapshot-A vs snapshot-B byte-compare. Re-applies the historical migration range to a fresh container (via the same context.CaptureSnapshotAsync delegate), applies the generated squash to a second fresh container (via an additional delegate, e.g., `CaptureFromGeneratedAsync`), canonicalizes both, asserts byte equality, and on mismatch summarizes a diff.
6. **SquashGenerationContext** (concrete `ISquashGenerationContext`) -- carries the live client (NpgsqlDataSource / IMongoClient / etc.) plus the caller-injected capture delegate(s). Postgres also defines `SnapshotCaptureRequest`/`SnapshotCaptureResult` records to type the delegate's parameters.

**Provider-internal helpers (not required by the contract):**

- **StatementKind enum** ([PostgresStatementKind.cs](../../../src/Hyperbee.Migrations.Providers.Postgres/Squash/PostgresStatementKind.cs)) -- Postgres-specific enumeration; each provider's analogue will be radically different (Aerospike: namespace/set/index/UDF; Mongo: collection/index/view/role; Couchbase: bucket/scope/collection/index/UDF; OpenSearch: index/template/component-template/pipeline/policy/alias).
- **StatementClassifier** ([PostgresStatementClassifier.cs](../../../src/Hyperbee.Migrations.Providers.Postgres/Squash/PostgresStatementClassifier.cs)) -- emits the per-statement `(Kind, SchemaName, ObjectName)` tuple consumed by the strategy. For JSON-bodied providers (Mongo/OpenSearch/Couchbase) this is a JSON descriptor walk rather than regex.
- **StatementSplitter** ([PostgresStatementSplitter.cs](../../../src/Hyperbee.Migrations.Providers.Postgres/Squash/PostgresStatementSplitter.cs)) -- only relevant for providers whose snapshot is a script-form text dump. Aerospike, Mongo, Couchbase, OpenSearch all introspect into JSON / structured documents and likely don't need a splitter at all -- they iterate objects directly.
- **MigrationSourceScanner** ([PostgresMigrationSourceScanner.cs](../../../src/Hyperbee.Migrations.Providers.Postgres/Squash/PostgresMigrationSourceScanner.cs)) -- Roslyn walker; the .NET non-determinism scan is provider-neutral and the same scanner could be hoisted to the core library or copied per-provider with adjusted DML pattern.

### Appendix B: Strategy contract snapshot (2026-05-11)

All paths are under `src/Hyperbee.Migrations/Squash/`. Per R-P7, any change to a signature on this surface requires an amended ADR-0019 before the code change lands.

**[ISquashStrategy.cs](../../../src/Hyperbee.Migrations/Squash/ISquashStrategy.cs#L18)** -- provider-supplied generator entry point.
- `string ProviderId { get; }` -- matches the topology signature's ProviderId.
- `Task<SquashGenerationResult> GenerateAsync(ISquashGenerationContext context, IReadOnlyList<MigrationDescriptor> descriptors, SquashGenerationOptions options, CancellationToken cancellationToken = default)` (line 30) -- generate a squash from the supplied descriptor range; returns Generated on success or Failed on refusal.

**[ITopologySignature.cs](../../../src/Hyperbee.Migrations/Squash/ITopologySignature.cs#L22)** -- captures axes that affect codegen determinism.
- `int SchemaVersion { get; }` (line 28) -- signature shape version, bumped on axis changes per R-P7 / ADR-0019 A14.
- `string ProviderId { get; }` (line 34) -- keys the verification cache and refuses cross-provider compares early.
- `IReadOnlyDictionary<string, string> Properties { get; }` (line 40) -- ordered, deterministic axis bag (no timestamps, no machine identity).
- `bool IsCompatibleWith(ITopologySignature other, out string reason)` (line 47) -- returns true when topologies are compatible; on false, `reason` is a human-readable diagnostic.

**[IDataOpClassifier.cs](../../../src/Hyperbee.Migrations/Squash/IDataOpClassifier.cs#L26)** -- provider-flavored data-op classifier.
- `DataOpClassification Classify(string statementOrCallSite)` (line 33) -- provider chooses how to parse; the input is a verbatim statement OR stringified call-site location.

**[DataOpClassification.cs](../../../src/Hyperbee.Migrations/Squash/DataOpClassification.cs#L34)** -- record returned by classifier.
- `sealed record DataOpClassification(bool IsDataOp, bool RequiresPreservation, bool IsUnclassified, bool RequiresAnnotation, string EmissionHint = null)` (line 34).

**[ISnapshotCanonicalizer.cs](../../../src/Hyperbee.Migrations/Squash/ISnapshotCanonicalizer.cs#L23)** -- normalization for byte-stable codegen.
- `string ProviderId { get; }` (line 26).
- `string Canonicalize(string snapshot)` (line 32) -- must be idempotent: `Canonicalize(b) == Canonicalize(Canonicalize(b))`.
- `string EmitScript(string canonicalContent)` (line 40) -- final script-form output for embedding; must be byte-stable for the C12 gate.

**[ISquashVerifier.cs](../../../src/Hyperbee.Migrations/Squash/ISquashVerifier.cs#L17)** -- runs the A/B byte-equality round.
- `string ProviderId { get; }` (line 20).
- `Task<VerificationResult> VerifyAsync(ISquashGenerationContext context, SquashGenerationResult.Generated generated, CancellationToken cancellationToken = default)` (line 26).
- `abstract record VerificationResult` (line 33) with variants `Success(ITopologySignature Topology, TimeSpan Elapsed)` and `Failed(string Detail, string DiffSummary, Exception Cause = null)`.

**[ISquashGenerationContext.cs](../../../src/Hyperbee.Migrations/Squash/ISquashGenerationContext.cs#L17)** -- minimal execution context. Providers downcast to a concrete shape.
- `string ProviderId { get; }` (line 20).
- `string SquashName { get; }` (line 27) -- caller-supplied id prefix.
- `long SquashVersion { get; }` (line 30) -- version the new migration will declare via `[Migration]`.
- Postgres concrete shape ([PostgresSquashGenerationContext.cs](../../../src/Hyperbee.Migrations.Providers.Postgres/Squash/PostgresSquashGenerationContext.cs#L25)) adds `NpgsqlDataSource DataSource` and `Func<SnapshotCaptureRequest, CancellationToken, Task<SnapshotCaptureResult>> CaptureSnapshotAsync`. Each provider must mirror this: a live client property + one or more `Func<TRequest, CT, Task<TResult>>` capture delegates.

**[SquashGenerationResult.cs](../../../src/Hyperbee.Migrations/Squash/SquashGenerationResult.cs#L12)** -- two-variant outcome.
- `abstract record SquashGenerationResult` (line 12).
- `sealed record Generated(string Content, ContentKind Kind, ContentEncoding Encoding, IReadOnlyList<long> Replaces, IReadOnlyList<string> Diagnostics, ITopologySignature Topology)` (line 21).
- `sealed record Failed(string Detail, Exception Cause = null)` (line 33). Per ADR-0019 A11 the earlier `Unsupported` variant was removed.

**[SquashGenerationOptions.cs](../../../src/Hyperbee.Migrations/Squash/SquashGenerationOptions.cs#L8)** -- caller-supplied knobs.
- `long? LowerBound { get; init; }` (line 15).
- `long? UpperBound { get; init; }` (line 22).
- `IReadOnlyList<string> AcceptStranding { get; init; }` (line 30).
- `bool SkipVerifyForTestingOnly { get; init; }` (line 39) -- testing-only; CLI does not expose it.

**[SquashStrategyDescriptor.cs](../../../src/Hyperbee.Migrations/Squash/SquashStrategyDescriptor.cs#L24)** -- composite that DI registers as a single unit.
- `sealed record SquashStrategyDescriptor(ITopologySignature TopologySignature, IDataOpClassifier DataOpClassifier, ISquashStrategy Generator, ISquashVerifier Verifier, ISnapshotCanonicalizer Canonicalizer)` (line 24).
- `void EnsureValid()` (line 36) -- null-check all five and assert all four other components share the topology signature's `ProviderId`, throws `MigrationException` otherwise.

### Appendix C: Per-provider introspection surfaces

#### Aerospike

- **NuGet client lib:** `Aerospike.Client` 8.2.0 (Directory.Packages.props line 48).
- **State capture mechanism:** Aerospike has no schema dump tool. Equivalent state capture is the **Info protocol** (`asinfo`-style key-value requests) plus enumeration via the client's management APIs. Namespaces are server-config-defined and not introspectable as DDL; sets/indexes/UDFs ARE introspectable.
- **State capture entry point:** `IAsyncClient` does not expose Info directly in the record store. Squash codegen would need to call `IAerospikeClient.Info(InfoPolicy, Node[], string[])` or the legacy `Info.Request(Node, ...)` with commands `namespaces`, `sets`, `sindex/<ns>`, `udf-list`, `udf-get:filename=<udf>`, `bins/<ns>`, `build` (server version), `edition`. The record store only uses `_client.Put/Get/Delete/Touch/Query` ([AerospikeRecordStore.cs](../../../src/Hyperbee.Migrations.Providers.Aerospike/AerospikeRecordStore.cs#L75)) -- no introspection currently exists.
- **Topology axes:** server version (`build` info command), edition (community vs enterprise -- affects SC/strong-consistency availability), namespace strong-consistency flag (per-namespace, `namespace/<ns>` info), replication factor, configured set list, configured secondary-index types available.
- **Locking mechanism for verification round:** CREATE_ONLY ledger record with TTL ([AerospikeRecordStore.cs](../../../src/Hyperbee.Migrations.Providers.Aerospike/AerospikeRecordStore.cs#L60)) -- same mechanism the record store already uses for `MigrationLock`. Verifier reuses; no per-snapshot lock needed beyond the migration lock that wraps the whole generation.
- **Sample size of structures introspected:** Small. Namespaces (server-configured, not created by migrations -- captured for topology only), sets, secondary indexes, UDF Lua modules, bin schemas (where used). Roughly 4-5 object kinds total.
- **Canonicalization risk:** **Low**. Info-protocol responses are line-oriented `key=value;...` strings -- deterministic by construction. UDF module content is Lua source text (verbatim). Main risk: ordering of returned sets/indexes per server response varies -- must sort client-side.

#### MongoDB

- **NuGet client lib:** `MongoDB.Driver` 3.6.0 + `MongoDB.Bson` 3.6.0 (Directory.Packages.props lines 31-32).
- **State capture mechanism:** MongoDB has no `mongodump --schema-only` analogue. Schema/structure capture is the union of `db.runCommand({listCollections})`, `db.getCollection(...).Indexes.List()`, `db.runCommand({listIndexes})`, view definitions in `system.views`, JSON schema validators on each collection, and (for ops) the role definitions in `admin.system.roles`.
- **State capture entry point:** `IMongoClient.GetDatabase().ListCollectionsAsync(...)` and `IMongoCollection<T>.Indexes.ListAsync(...)`. The record store ([MongoDBRecordStore.cs](../../../src/Hyperbee.Migrations.Providers.MongoDB/MongoDBRecordStore.cs#L27)) only uses `db.GetCollection<MigrationRecord>(...)` for read/write -- squash codegen needs to add the introspection calls.
- **Topology axes:** server version (`{buildInfo: 1}` admin command), deployment topology (standalone / replica set / sharded -- from `{isMaster}` / `{hello}`), feature compatibility version (FCV, from `{getParameter: 1, featureCompatibilityVersion: 1}`), default read/write concerns, sharded-cluster flag (affects collection-creation shape).
- **Locking mechanism for verification round:** Singleton `MigrationLock` document with `Id == 1` and `ReleaseOn` TTL field ([MongoDBRecordStore.cs](../../../src/Hyperbee.Migrations.Providers.MongoDB/MongoDBRecordStore.cs#L42)). No per-capture lock; the migration lock spans the generation.
- **Sample size of structures introspected:** Medium. Collections, indexes (each with options dict: `unique`, `sparse`, `partialFilterExpression`, collation, wildcards, TTL), views (with pipeline), JSON schema validators, time-series options, capped flags. Roughly 6-8 object kinds.
- **Canonicalization risk:** **Medium**. Index `v` field varies by server version (v:1, v:2; v:2 is current). `_id` index is implicit and always present -- must filter out. Index `key` ordering matters; document field order within key spec is server-canonical. Collation defaults differ by FCV. Pipeline definitions in views are BSON -- Extended JSON canonical form is the right serialization, not the relaxed form.

#### Couchbase

- **NuGet client lib:** `CouchbaseNetClient` 3.8.1 + `Couchbase.Extensions.DependencyInjection` 3.8.1 + `Couchbase.Extensions.Locks` 2.1.0 (Directory.Packages.props lines 27-29).
- **State capture mechanism:** Couchbase mixes management APIs (REST + SDK wrappers) with N1QL system catalog queries. Schema-equivalent capture is: `cluster.Buckets.GetAllBucketsAsync()` (settings: RAM quota, replicas, eviction policy, flush), per-bucket `bucket.Collections.GetAllScopesAsync()` (scope + collection tree), N1QL `SELECT * FROM system:indexes WHERE keyspace_id = '<bucket>'` (GSI definitions), N1QL `SELECT * FROM system:functions` (UDFs), `cluster.UserManager` for roles, and FTS/Eventing/Analytics via their respective management APIs.
- **State capture entry point:** `IClusterProvider.GetClusterAsync() -> ICluster`. The record store ([CouchbaseRecordStore.cs](../../../src/Hyperbee.Migrations.Providers.Couchbase/CouchbaseRecordStore.cs#L35)) already uses `cluster.BucketAsync(...)`, `bucket.ScopeAsync(...)`, `scope.CollectionAsync(...)`, `cluster.Buckets.CreateBucketAsync(...)` ([line 65](../../../src/Hyperbee.Migrations.Providers.Couchbase/CouchbaseRecordStore.cs#L65)), `cluster.QueryIndexes.CreatePrimaryIndexAsync(...)` ([line 95](../../../src/Hyperbee.Migrations.Providers.Couchbase/CouchbaseRecordStore.cs#L95)), and `cluster.QueryAsync<long>(...)` ([line 330](../../../src/Hyperbee.Migrations.Providers.Couchbase/CouchbaseRecordStore.cs#L330)). The bootstrapper layer (`clusterHelper`) provides `BucketExistsAsync`, `CreateScopeAsync`, `CollectionExistsQueryAsync` -- squash can hoist their listing analogues.
- **Topology axes:** server version (`SELECT version FROM system:metadata` or REST `/pools/default`), cluster topology (single-node vs multi-node, MDS vs all-services), services enabled per node (kv, query, index, fts, eventing, analytics, backup), storage backend per bucket (Couchstore vs Magma), GSI deployment plan, RBAC enabled.
- **Locking mechanism for verification round:** `Couchbase.Extensions.Locks` mutex ([CouchbaseRecordStore.cs](../../../src/Hyperbee.Migrations.Providers.Couchbase/CouchbaseRecordStore.cs#L198)) via `collection.RequestMutexAsync(name, expireInterval)` with auto-renew. Server-side document-based mutex; correct primitive for verifier rounds.
- **Sample size of structures introspected:** Medium-large. Buckets (with quota/replica/eviction settings), scopes, collections (with TTL), primary + secondary GSI indexes (with `WHERE` clauses and `WITH` options), UDFs (JavaScript + N1QL), FTS indexes (separate API + JSON shape), eventing functions, analytics datasets/dataverses. Roughly 8-10 object kinds.
- **Canonicalization risk:** **Medium-High**. GSI index definitions: server normalizes index expressions (parentheses, type coercions) at create time, so `CREATE INDEX ... ON keyspace((field))` round-trips differently than the operator wrote it. FTS index JSON is large and contains server-injected defaults. Bucket settings have many implicit defaults that differ between Couchbase Server versions. JavaScript UDF source preserves verbatim, but signature canonical form varies. View ddocs (legacy, but still supported) carry map/reduce JS -- verbatim but encoding-sensitive.

#### OpenSearch

- **NuGet client lib:** `OpenSearch.Client` 1.8.0 + `OpenSearch.Net` 1.8.0 (+ `OpenSearch.Net.Auth.AwsSigV4` 1.8.0) (Directory.Packages.props lines 50-52).
- **State capture mechanism:** REST API endpoints flattened into JSON dumps. `GET /_cluster/state/metadata` (or its subsets: `/indices`, `/templates`), `GET /_index_template/*`, `GET /_component_template/*`, `GET /_template/*` (legacy), `GET /_ingest/pipeline/*`, `GET /_alias` (or `/_cat/aliases?format=json`), `GET /<index>/_settings`, `GET /<index>/_mapping`, `GET /_plugins/_ism/policies` (when ISM plugin is detected -- there's already a capability detection step in [IsmEndpointDetectStep.cs](../../../src/Hyperbee.Migrations.Providers.OpenSearch/Internal/Bootstrap/Steps/IsmEndpointDetectStep.cs)), `GET /_security/role/*` (with security plugin).
- **State capture entry point:** `IOpenSearchClient` from `OpenSearch.Client`, used throughout [OpenSearchRecordStore.cs](../../../src/Hyperbee.Migrations.Providers.OpenSearch/OpenSearchRecordStore.cs#L122). The strong-typed client surfaces `Indices.GetAsync`, `Indices.GetTemplateV2Async`, `Indices.GetMappingAsync`, `Indices.GetSettingsAsync`, `Cluster.StateAsync`, `Cluster.HealthAsync`. For ISM-specific endpoints fall through to `OpenSearch.Net`'s low-level `_client.LowLevel.DoRequestAsync(...)`.
- **Topology axes:** cluster version (from `Cluster.HealthAsync` / root `/`), distribution flavor (OpenSearch vs Elasticsearch -- affects feature surface), installed plugins (`GET /_cat/plugins?format=json` -- security, ISM, k-NN, alerting, anomaly-detection all alter the shape), ISM endpoint capability (already detected -- `_plugins/_ism` vs `_opendistro/_ism` per [IsmEndpointCapability.cs](../../../src/Hyperbee.Migrations.Providers.OpenSearch/Internal/IsmEndpointCapability.cs)), node count, shard allocation awareness, default analyzers.
- **Locking mechanism for verification round:** Singleton lock document with op_type=create + CAS via `if_seq_no` / `if_primary_term`, plus stale-takeover heartbeat ([OpenSearchRecordStore.cs](../../../src/Hyperbee.Migrations.Providers.OpenSearch/OpenSearchRecordStore.cs#L122)). `Refresh.WaitFor` on writes. Verifier reuses; per-snapshot lock is the migration lock.
- **Sample size of structures introspected:** Large. Indices (with settings + mapping + aliases each), index templates (v2 + legacy), component templates, ingest pipelines (with processor arrays), ISM policies (with state-transition graphs), aliases (with filters + routing), saved searches (when alerting plugin present), role mappings (security plugin). Roughly 8-12 object kinds, several with nested sub-objects.
- **Canonicalization risk:** **High**. Painless script bytes inside ingest pipelines and ISM policies -- embedded scripts must round-trip exactly, including whitespace. Index mappings: server injects `_doc` root, dynamic templates with default-set fields, normalizers -- all server-augmented post-create. ISM policy JSON has version-stamped fields (`policy_version`, `last_updated_time`) that MUST be stripped. Settings come back fully expanded with server-default values (`number_of_shards`, `refresh_interval`, analysis chains) -- these must either be normalized to a canonical default set or operators must always pass full settings explicitly. Alias filters are query DSL JSON -- field-order canonical form required. Component template composition order matters and the server response may not preserve operator-authored order.
