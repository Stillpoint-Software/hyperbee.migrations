# Migration Squashing — Destructive Model Consensus

**Status:** Ratified (Round 1b 2026-05-05); hardened with Assessment 0007 amendments (2026-05-05); ready for `/nop:plan`
**Inputs:** ADR-0019 (destructive reframe) + Round 1a friction analyses (5 advocates) + EF Core reference + Round 1b refinements (5 advocates) + Assessment 0007 (full /nop:assess)
**Disposition:** Cross-cutting universal contracts + per-provider commitments + remaining concerns + 9 P0 + 10 P1 + 5 P2 amendments from Assessment 0007

> **NOTE 2026-05-05 — Assessment 0007 amendments folded in.** The original 11 universal items (C1–C11) and per-provider commitments survive intact. Two new universal items added (C12 generation determinism, C13 verification container lifecycle) plus the Postgres-only v1 sequencing decision. See "Amendments from Assessment 0007" section near the bottom of this document for the full list. The amendments are hardening, not redesign — the architecture is unchanged.

---

## Unanimous convergence (all 5 advocates agree)

These items emerged independently in Round 1a and were ratified explicitly by all 5 advocates in Round 1b. They are the ratified contract.

### C1: `IDataOpClassifier` is framework-level

The data-migration hole is the dominant universal concern. Every advocate independently flagged it; every advocate independently arrived at "scan source migrations, classify each statement/call as DDL or DML, carry DML verbatim, refuse on unclassified." Consensus on the abstraction:

```csharp
public interface IDataOpClassifier
{
    DataOpClassification Classify(StatementOrCallSite candidate);
}

public sealed record DataOpClassification(
    bool IsDataOp,
    bool RequiresPreservation,        // True = carry verbatim into squash data-ops resource
    bool IsUnclassified,              // True = refuse the squash with diagnostic
    string? EmissionHint              // Provider-specific hint; e.g., "embed-as-sql", "carry-as-csharp-fragment"
);
```

Per-provider classifier registrations:
- **Postgres:** regex/lex over SQL keywords (`INSERT|UPDATE|DELETE|TRUNCATE|MERGE|COPY|SELECT INTO|CREATE TABLE AS SELECT`); `DO $$` blocks containing DML flagged conservatively as `RequiresPreservation=true`; functions containing DML in their body classified as DDL (definition is structural; invocation would be data).
- **MongoDB:** Roslyn AST scan over `IMongoCollection<T>` method invocations: `InsertOne/InsertMany`, `UpdateOne/UpdateMany`, `DeleteOne/DeleteMany`, `BulkWrite`, `FindOneAndUpdate/Replace/Delete`, aggregation pipelines containing `$out`/`$merge`, `MapReduce`.
- **Couchbase:** N1QL parser detects `INSERT INTO`, `UPSERT INTO`, `UPDATE`, `DELETE FROM`, `MERGE INTO`. KV-direct ops (rare in resource model) detected via SDK call AST scan.
- **OpenSearch:** AST type matching against `CompositeStatementAst` raw body; explicit refusal for `_bulk`, `_update_by_query`, `_delete_by_query`, `_reindex` unless overridden.
- **Aerospike:** Roslyn AST scan over `IAerospikeClient` method calls; `[DataMigration]` attribute as explicit author marker; heuristic detection of `client.Put`/`client.Operate` outside structural-DDL helpers.

### C2: Verification round is mandatory

The squash CLI does not commit until it has executed:

1. Spin a *third* ephemeral container.
2. Apply migrations < N (residual head).
3. Apply the *generated* squash body.
4. Re-snapshot (call this B').
5. Byte-compare canonicalized B' against canonicalized B from the original generation pass.
6. If divergent, refuse the squash and emit the diff for operator review.

This is the only honest gate. Codegen bugs are caught at squash creation, not at production deploy.

### C3: Unified `--squash-overrides` block in `fleet.yml`

OpenSearch advocate's proposal, adopted unanimously. Replaces the proliferating per-provider opt-out flags (`--accept-data-op-loss`, `--allow-sharded-codegen`, `--accept-fts-out-of-scope`, `--accept-bucket-drop`, `--accept-version-skew`, etc.). Single config surface:

```yaml
fleet:
  environments: [...]
  squash-overrides:
    accept-stranding: [dev-shared, ci-runner-3]    # named envs only, no blanket "*"
    aerospike:
      accept-data-replay: false
    opensearch:
      accept-ism-drift: true                       # mature envs may have ISM-transitioned past A
      accept-data-op-loss: false
    mongodb:
      allow-sharded-codegen: false
      target-topology: replica-set                  # required field; refuses on mismatch
    postgres:
      allow-version-skew: false                     # client/server major version mismatch
      strip-create-index-concurrently: true        # default true; documented behavior
    couchbase:
      gsi-build-timeout-seconds: 600
      accept-fts-out-of-scope: false
      accept-ledger-bootstrap-in-squash: false
```

The squash artifact's header records which overrides were active at generation; replay-time the runtime can re-validate against the same overrides.

### C4: In-process diff — no external dependency

No Migra, no Atlas, no apgdiff/pg_diff, no third-party diff binaries. Each provider implements its diff in-process using Roslyn (for code AST scanning) and provider-specific parsing/canonicalization for state.

### C5: Determinism via round-trip CI test

For each provider, CI runs:

1. Spin ephemeral container; apply fixture migration set; capture canonicalized snapshot.
2. Spin a *second* ephemeral container; apply same migration set; capture canonicalized snapshot.
3. Assert byte-equal.

Run on every PR. A canonicalization regression is detected immediately.

### C6: Async-build barrier is fleet-wide

For providers with asynchronous index/SI/refresh builds (Aerospike, Couchbase, MongoDB, OpenSearch), the snapshot capture MUST wait for fleet-wide build completion across all replica/shard nodes — not just primary-local. Couchbase advocate's articulation: "primary-local would let us snapshot a state that doesn't exist on secondaries, which would produce a squash that replays inconsistently against a fresh cluster."

Per-provider:
- Aerospike: poll `info("sindex-stat:")` until all SIs report `state=RW` on every node.
- Couchbase: poll `system:all_indexes` for `state=online` AND `build_progress=100` on every replica.
- MongoDB: poll for index build completion on primary AND all secondaries (RS) before snapshot.
- OpenSearch: explicit `_refresh` on all primary+replica shards; verify `_cluster/health?wait_for_status=green` before snapshot.

Timeout configurable via `--squash-overrides`; default 10 minutes; on timeout the squash refuses rather than snapshotting partial state.

### C7: No-op range refuses by default

Originally a tension. Couchbase moved to refuse-by-default in Round 1b; OpenSearch agreed; Aerospike already there; Postgres softened to "refuse only when *both* structural and data-ops bodies are empty." MongoDB held position but signaled willingness via `--allow-empty` escape. Final consensus:

- Empty structural diff + empty data-ops body → **refuse** with diagnostic.
- Empty structural diff + non-empty data-ops body → **emit** with header comment `-- squash: no structural delta in range A..B; data ops carried forward verbatim`.
- Non-empty structural diff (whether or not data-ops also present) → emit normally.

`--allow-empty` override available for the deliberate "consolidate ledger rows for source-tree compaction even though there's nothing to apply" case.

### C8: `ContentKind` enum on `SquashGenerationResult`

Postgres advocate's contribution. Accommodates the code-vs-resource asymmetry without forcing a structured intermediate:

```csharp
public abstract record SquashGenerationResult
{
    public sealed record Generated(
        ReadOnlyMemory<byte> ResourceContent,
        ContentKind          Kind,
        ContentEncoding      Encoding,
        IReadOnlyList<long>  Replaces,
        IReadOnlyDictionary<string, string> Diagnostics,
        ITopologySignature   Topology
    ) : SquashGenerationResult;

    public sealed record Unsupported(string Reason) : SquashGenerationResult;
    public sealed record Failed(string Detail, Exception? Cause) : SquashGenerationResult;
}

public enum ContentKind { SqlText, CSharpSource, CanonicalJson, OpaqueBinary }
public enum ContentEncoding { Utf8, Utf8Bom, Raw }
```

Provider decides Kind; framework treats bytes as opaque for storage/hashing/transport but uses Kind for emission path (file extension, formatter, verifier dispatch).

### C9: `ITopologySignature` for cross-provider environment shape

Postgres's "version pinning" generalized:

```csharp
public interface ITopologySignature
{
    string ProviderId { get; }
    IReadOnlyDictionary<string, string> Properties { get; }
    bool IsCompatibleWith(ITopologySignature other, out string? incompatibilityReason);
}
```

Per-provider topology axes:
- **Postgres:** `{server_major, extensions[], collation_provider, locale_provider}`
- **MongoDB:** `{topology: standalone|replica-set|sharded, server_major}`
- **Couchbase:** `{edition: ce|ee, server_major, services[]}`
- **OpenSearch:** `{node_count, server_major, plugins[]}`
- **Aerospike:** `{node_count, edition, server_major}`

Squash artifact records its origin topology; replay against materially different target requires `--allow-topology-skew` opt-in.

### C10: `--accept-stranding` — named environments only, no blanket wildcard

Aerospike advocate's strict position; ratified by all. `--accept-stranding=*` is forbidden. Each stranded environment must be named explicitly; the named list goes into the squash audit trail.

### C11: Round-trip canonicalization risk labels in U9 manifest

Aerospike advocate's contribution: providers carry a per-canonicalization-risk label that operators see when invoking the squash CLI:

| Provider | Canonicalization risk | Reasons |
|---|---|---|
| Aerospike | **Low** | Narrow surface (sets, SI, UDF); few canonicalization knobs |
| Postgres | **Medium** | Server-version syntax drift; locale-dependent COLLATE; extension internals |
| MongoDB | **Medium-High** | BSON field-order vs JSON Schema semantics; collation expansion; `uuid` strip |
| Couchbase | **High** | GSI build queue; bucket settings auto-injection; multi-resource (REST + N1QL) |
| OpenSearch | **High** | Mapping auto-injection; `_meta` discriminator; painless whitespace; component-template merging |

This is informational — drives operator review depth and CI test rigor expectations.

---

## Architecture (consensus contract)

```
ISquashStrategy           (framework — orchestration)
  ├─ ITopologySignature   (provider — environment shape capture)
  ├─ IDataOpClassifier    (provider — DDL/DML separation)
  ├─ ISquashGenerator     (provider — produces ResourceContent + ContentKind)
  ├─ ISquashVerifier      (provider — fresh-container apply + compare)
  └─ ISnapshotCanonicalizer (provider — for determinism)

squash-overrides:         (CLI — unified per-provider override block in fleet.yml)
```

The framework owns:
- Squash CLI verb (`dotnet hyperbee-migrations squash --range N-M --provider <p>`)
- Fleet readiness check loop (per ADR-0019 step 7)
- Verification round orchestration (C2)
- `[Migration].Replaces` / `ReplacesRange` resolution and immutability checking (per IR-N2)
- Source-file removal at squash creation
- `MigrationRecordKind` writing (per ADR-0021)

Each provider owns:
- Its `ITopologySignature` implementation
- Its `IDataOpClassifier` implementation
- Its `ISquashGenerator` (snapshot capture + canonicalization + diff + emission)
- Its `ISquashVerifier` (fresh-container apply + post-snapshot + compare)
- Its `ISnapshotCanonicalizer` (provider-specific normalization)

---

## Per-provider commitments (Round 1b ratified)

### Aerospike

- Emits **two artifacts per squash**: structural JSON manifest (diffable) + replay-captured data-ops C# body (opaque, marked).
- Hybrid model: structural ops are diffed, data ops are replay-captured.
- `IDataOpClassifier` recognizes `[DataMigration]` attribute + heuristic `client.Put`/`client.Operate` detection.
- 3-node RF=2 codegen container by default to mirror common production topology.
- SI build worst-case: ~90s on test corpus; bounds CI step but doesn't dominate.
- Canonicalization: low complexity (sort SI list, sort sets, hash UDF bytes, normalize bin-name case).

### OpenSearch

- **REST-state-diff is canonical**; AST fusion is a fast-path optimization for ranges with only direct PUT/DELETE on named resources.
- Per-resource canonicalization with JSON-pointer-keyed array-ordering table.
- Painless scripts AST-parsed and pretty-printed.
- Mapping renames require explicit operator annotation (`@rename` directive).
- Topology requirement: minimum 3-node verifier for ranges touching ILM/ISM or shard allocation; single-node permitted for index-template/mapping/pipeline-only.
- AWS Managed minimum IAM grant: `indices:data/read/mget` on `.migrations` only.
- Component-template merging: hash un-merged for authoring identity, canonicalize merged for equivalence.

### MongoDB

- Rejects EF Core consultant's "replay-only" framing. **Produces structural diff** over collection options/indexes/validators **plus carry-forward** for data ops. Two pluggable axes: `ISnapshotStrategy` + `IDataOpClassifier`.
- `IntrospectionSnapshotStrategy` explicit IN/OUT scope (per Round 1a manifest).
- JSON Schema validator canonicalization: 8 specific rules (sort `properties`, sort `required`, sort `bsonType` arrays, sort `enum`, normalize `type` vs `bsonType` to `bsonType`, preserve `allOf`/`anyOf`/`oneOf` order — semantically meaningful).
- Topology pinning required: `target-topology: standalone|replica-set|sharded` mandatory in fleet.yml.
- Sharded refused unless `--squash-overrides.mongodb.allow-sharded-codegen=true` with required shard-key declarations.
- Atlas Search refused (out of v1 scope).
- Strategy emits `statements.json` (Parlot Mongo-shell-like), symmetric with Postgres `.sql`.

### Postgres

- v1 path-finder. `pg_dump --schema-only` post-processed via canonicalization pipeline.
- pg_dump version pinning via shipped CLI image (Postgres 14/15/16/17 dumpers bundled); selected by `server_version_num`.
- Statement classifier ~600 LOC C# (parses pg_dump text into typed statement list).
- Sequence `setval` post-emission for sequences with non-default `last_value`.
- `CREATE INDEX CONCURRENTLY` deliberately stripped (squash runs in transaction; CONCURRENTLY incompatible).
- Verification: in-process dump-vs-dump byte-compare after canonicalization.
- Output: single `Squash_M.sql` via `PostgresResourceRunner.AllSqlFromAsync`.
- Extension drift flagged as topology-incompatibility.

### Couchbase

- Hybrid: structural codegen + verbatim data-op carry-forward.
- Three-resource emission: `statements.json` (N1QL DDL) + `bucket-settings.json` (REST API calls) + `data-ops.json` manifest pointing at carry-forward `.cs` fragments.
- Cluster-level snapshot (not bucket-level); transitively captures buckets referenced by cross-bucket N1QL.
- Fleet-wide GSI build barrier with configurable timeout (default 600s).
- N1QL parser canonicalizes WHERE clauses via predicate AST: identifier quoting normalized, keyword case upper, AND/OR commutative-children sorted by hash, literal values verbatim.
- CE/EE feature gating; squash refuses if source uses EE-only features and target is CE.
- Companion `bootstrap.cs` for ranges containing ledger-bootstrap migration.
- FTS, Eventing, Analytics explicitly out of scope; refuse with clear diagnostic.

---

## Open issues for `/nop:assess` to stress-test

Surfaced in Round 1b as remaining concerns; flagging for the next assessment pass:

1. **Verification round cost on multi-node topologies.** OpenSearch ~204s, Couchbase ~150s, MongoDB sharded heavy. May need `--skip-verify` escape; if so, when is it ever safe? (Answer should probably be "never for production-bound squashes; allowed for dev-iteration only with loud warnings.")

2. **Replay non-determinism from `Now()`/`Guid.NewGuid()` in carried-forward data ops** (Aerospike, MongoDB raised). Need authoring-guidance doc; classifier could detect these patterns and warn.

3. **Component-template / extension version drift** across server minor versions. Canonicalizer needs version-pinned defaults tables; canonicalization spec should be a versioned artifact alongside the squash.

4. **Cross-bucket N1QL with parameterized bucket names** (Couchbase). Current plan refuses; need confirmation this isn't common in production migrations.

5. **`IDataOpClassifier` false negatives** in Postgres functions/procedures with internal DML (`DO $$`, `CREATE FUNCTION ... LANGUAGE plpgsql`). Conservative classifier flags `DO` blocks containing DML keywords as `RequiresPreservation=true` — over-preservation safer than data loss.

6. **Index `v` field stripping in MongoDB** vs server-version pinning. Current plan strips with a logged diagnostic; alternative is force codegen container to match server version exactly.

7. **Painless script semantic equivalence** in OpenSearch — two scripts with different variable names diff as non-equal even when semantically identical. Acceptable for now; future concern if false-positives become noisy.

8. **Mid-range environment recovery via `--force-squash-from-mid-range`** — ADR-0019 documents this exists but doesn't specify behavior in detail. The `/nop:assess` pass should pressure-test the mid-range recovery story.

---

## Summary

Strong convergence across all 5 advocates plus EF Core consultant input. The destructive-model design is materially safer and more universal than the additive-model design that preceded it. The contract structure (`ISquashStrategy` orchestration with 5 pluggable per-provider components, `--squash-overrides` unified config, mandatory verification, fleet-wide async barriers, framework-level `IDataOpClassifier`) accommodates the variance across the 5 current providers while enforcing universal correctness invariants.

**Ready for implementation examples** — each advocate writes a basic-but-not-sugar-coated concrete code example for their provider, then `/nop:assess` against the consensus + examples, then final hardening.

---

## Amendments from Assessment 0007 (2026-05-05) — Hardening Pass

The full `/nop:assess` ([0007](../research/0007-migration-squashing-destructive-assessment.md)) on the destructive-model consensus + 5 implementation examples produced **9 P0 + 10 P1 + 5 P2 amendments**. The architecture is unchanged; the amendments harden specific gaps. All 8 open issues from the consensus's "remaining concerns" section get explicit resolutions.

### New universal items

#### C12: Generation determinism gate (P0-7)

**Source:** Independent Review IR-N2.
**Concern:** C5 covers replay determinism, not generation determinism. Without C12, the squash artifact's checksum (per ADR-0021) is unstable across rebuilds — re-generating to incorporate a fix produces a new checksum, breaking auto-mark on environments that already have the prior squash row.
**Contract:** Per provider, CI test runs `squash --range R` twice in fresh ephemeral containers; asserts byte-equal:
- `Squash_M.{sql,statements.json}` body
- `Squash_M.summary.md` artifact (per C13a below)
- Topology signature

Sources of nondeterminism eliminated by canonicalization:
- Wall-clock timestamps in artifact headers
- GUIDs (any new generation in codegen output)
- Container UUIDs / port assignments
- Dictionary iteration order (use `SortedDictionary` or sort-on-emit)

**Failures gate release.**

#### C13: Verification container lifecycle (P1-9)

**Source:** Independent Review IR-N4.
**Concern:** If C2 verification fails (B' diverges from B), the ephemeral container leaks. Operators iterate on canonicalization regressions — failure is the expected debug path.
**Contract:**
- **Success:** container torn down immediately after byte-equal assertion.
- **Failure:** container torn down by default; retained ONLY with `--keep-failed-container` flag (under labeled name; reconnect instructions printed).
- **Always:** debug summary written to `./squash-debug/<timestamp>/` containing canonicalized B and B' bodies for offline diff.
- **`try/finally`** wraps verification block — Ctrl-C does not leak containers.

#### C13a: Squash summary artifact (P1-6)

**Source:** Assessment CP-4 (Red wins).
**Concern:** C2 verification proves bytes match; doesn't prove intent matches the source range. A canonicalization regression that affects both A and B identically passes verification but ships wrong-by-intent code.
**Contract:** Squash CLI emits a third artifact alongside the body resource:

`Squash_M.summary.md` containing:
- Statement count by category (CREATE TABLE, CREATE INDEX, ALTER TABLE, INSERT/UPDATE/DELETE, etc.)
- Table list (created, dropped, modified)
- Sequence list with `setval` values for non-default `last_value` (Postgres)
- Index list (created, dropped)
- Dropped-objects list
- Data-ops-source-list (which originals contributed carry-forward DML)
- Topology signature recorded for replay-time compatibility check
- Override block in effect at squash creation

PR template requires this artifact pasted into the description. Reviewers compare summary against the migration range's commit log, not the artifact bytes.

### Universal escape-hatch hardening

#### `--skip-verify` deleted (P0-1)

Open issue #1's own conclusion ("never for production-bound") shipped as contract. **Removed entirely** from v1 CLI surface — both squash generation and runtime. Verification cost addressed via C12-adjacent caching (next item), not via an escape valve.

#### Snapshot A caching + parallel A/B capture (P0-4)

Three changes that together reduce verification cost ~3x:

1. **Snapshot A cached** by `hash(provider, residual-head-version-set, canonicalizer-version, topology-signature, image-version)`. First squash regeneration pays full cost; subsequent regenerations skip Container A entirely.
2. **A and B captured in parallel** via `Task.WhenAll`. Sequential await pattern in implementation examples is a bug.
3. **Container reuse for verification:** Container A's residual-head state reused as verification base — apply generated squash there instead of spinning a third container.

Target: OpenSearch 3-node 204s → ~70s; Postgres 95s → ~40s. Eliminates the cost basis driving `--skip-verify` pressure.

#### Two-phase fleet readiness gate (P0-2)

Per findings PM-2 + MD-2 + IR refinement: fleet manifest as single source of truth fails open. Two-phase gate:

**Phase 1 (squash creation):** unchanged.
**Phase 2 (deploy time):** squash artifact records `expected-fleet-versions: {env: minVersion}` (captured at squash creation) AND `max-staleness-window: <duration>` (default 30 days). At each environment's deploy time, the runner re-reads the ledger:
- Env not present in `expected-fleet-versions` → `UnregisteredEnvironmentException`.
- Env's actual version below recorded minimum AND env hasn't moved within staleness window → `StaleFleetMemberException`.

Converts silent stranding into recoverable deploy-time refusal.

#### Mid-range recovery moves to separate `recover` subcommand (P0-3)

Per CP-1 synthesis + IR-CP-1: `--force-squash-from-mid-range` is moved out of `squash` subcommand into `dotnet hyperbee-migrations recover from-mid-range`. Separate verb + deterministic-but-stable token gate (token = `SHA-256(env-name ‖ squash-version ‖ missing-versions)[:12]`) + mandatory `--ticket-id` + `--reason=<≥20 chars>`. Backup-restore remains documented primary recovery; flag is "last resort, DBA-supervised, post-incident only."

### Provider strategy contract hardening

#### Composite-descriptor registration (P1-7)

Per finding MD-11: `ISquashStrategy` registration takes one composite descriptor with all 5 pluggable components (`ITopologySignature`, `IDataOpClassifier`, `ISquashGenerator`, `ISquashVerifier`, `ISnapshotCanonicalizer`). `NotImplementedException` from any component fails registration validation, not silent runtime failure.

Lazy implementer who stubs the canonicalizer or verifier discovers the failure at startup with a descriptive error, not at first squash creation against production migrations.

#### Mandatory `[DataMigration]` annotation (P0-5)

Per CP-2 (Red wins): `IDataOpClassifier` returns `RequiresAnnotation=true` whenever heuristic detects possible DML on a migration class lacking either `[DataMigration]` (acknowledge → carry-forward) or `[StructuralOnly]` (assert heuristic wrong → suppress, logged). Squash CLI **refuses** with diagnostic naming the migration and suspect statement/call.

Silent false-negatives (data loss) become loud false-positives (annotation friction) — the safer error direction for destructive operations.

#### Non-determinism scan (P1-1)

Classifier scans for non-deterministic patterns; refuses unless `accept-non-deterministic-data-ops=true` override (with explicit override-record-list naming the migrations):

`DateTime.Now/UtcNow`, `DateTimeOffset.Now/UtcNow`, `Guid.NewGuid()`, `Random` sans seed, `Environment.MachineName/UserName`, `Stopwatch.GetTimestamp()`, `Process.Id`, `Activity.Current?.TraceId`, `IPGlobalProperties.GetHostName()`, `Assembly.GetExecutingAssembly().Location`.

Whitelist approach (rather than ban-list-only) per IR refinement.

#### Single-compilation-per-assembly classifier contract (P2-1)

Per finding PA-4: per-migration semantic-model construction is expressly forbidden. Classifier creates one `CSharpCompilation` per assembly; AST visitors share the semantic model. 50s → 2.7s on 500-migration ranges.

### Override block hardening

#### Structured fields with ticket-id, owner, expiry (P1-2 + IR-CP-2 + P1-10)

Per CP-5 (Red wins) + IR-CP-2 (Red wins): replace `≥20 chars` reason theater with structured fields:

```yaml
squash-overrides:
  accept-stranding:
    - env: dev-shared
      ticket-id: HBM-1234           # regex-validated; default ^[A-Z]+-\d+$
      owner: brentfarmer            # validated against last-90-days git authors
      reason: "Dev cluster intentionally lags main; sync after sprint review"
      expires: 2026-06-04           # ISO date; default 30 days from creation; max 90 days
```

CI lint:
- `ticket-id` matches configured regex
- If `tracker-url` configured, ticket-id resolves over HTTP
- `owner` matches author from last-90-days git commit log
- `expires` present and ≤90 days from creation
- CI warns at 7 days remaining; refuses to apply squash with expired override

### Topology hardening

#### Schema versioning + migration-forward (P1-8)

Per finding PM-8: `ITopologySignature` artifacts carry `signature-schema-version: <int>`. Each provider ships migration logic when adding axes:

```csharp
// Postgres adds replication-role in v1.5
public class PostgresTopologySignature : ITopologySignature
{
    public int SchemaVersion { get; init; }  // 1, 2, ...
    public string? ReplicationRole { get; init; }  // new in schema-version 2

    public bool IsCompatibleWith(ITopologySignature other, out string? reason)
    {
        // Migrate older signatures forward with documented defaults:
        // schema-version 1 implies ReplicationRole = "primary"
    }
}
```

Topology signature changes require a new ADR documenting back-compat semantics for each prior version. `--allow-topology-skew` becomes the explicit opt-out; never silent.

### v1 ship sequencing

#### Postgres only in v1; v1.1 Aerospike+MongoDB; v1.2 Couchbase+OpenSearch (P0-9 / IR-CP-4)

Per IR-CP-4 (Red wins): OpenSearch and Couchbase are High canonicalization risk per C11; MongoDB is Medium-High. Shipping High-risk providers in v1 means production migrations exercise canonicalizer for the first time in customer destructive squashes.

| Phase | Providers shipping `ISquashStrategy` |
|---|---|
| **v1** | Postgres only (`PgDumpSnapshotStrategy`) |
| **v1.1** (~3 months after v1) | Aerospike (`InfoSnapshotStrategy`) + MongoDB (`IntrospectionSnapshotStrategy`) |
| **v1.2** (~6 months after v1) | Couchbase (`HybridStrategy`) + OpenSearch (`RestStateDiffStrategy`) |

v1 promotion gate: Postgres metrics under thresholds for ≥1 release cycle (verifier-refusal rate <5%, canonicalization memory <500MB, classifier LOC <1500).

Other providers in v1 ship `NullSquashStrategy` returning `Unsupported(...)` — but **`Unsupported` no longer suggests "hand-author"** (per MD-8 deletion below). Hand-authoring a destructive squash is unrealistic; the diagnostic points at the squash CLI roadmap.

### Removals

#### `SquashGenerationResult.Unsupported("hand-author")` deleted (P0-1 / MD-8)

Per MD-8: the `Unsupported` result variant promised a viable manual path that doesn't exist. Provider without an `ISquashStrategy` doesn't get a squash CLI verb at all — clean refusal at registration time.

#### `--skip-verify` flag deleted (P0-1) — see above

#### `MD-9` operator-machine pg_dump version drift subsumed (P1-3)

Resolved by P1-3 redesign — pg_dump runs inside server-version-matched ephemeral container via `docker exec`, never on operator machine. The standalone concern disappears.

### Re-squash transitivity (P0-6)

Per IR-N1: when `Squash_3000` replaces `Squash_2000` plus newer migrations, an environment that auto-marked `Squash_2000` (ledger has the squash row but not underlying replaced rows) needs the runner to recognize the squash row as satisfying the replacement obligation transitively.

Reconciliation pseudocode amended:

```
let replacedSet = squash.Replaces  // recorded as authored, NOT transitively expanded
let satisfiedSet = await store.LoadSatisfyingRowsAsync(replacedSet)

// LoadSatisfyingRowsAsync semantics: returns versions where
//   row.Kind == Migration AND row.Id == version  -- direct match
// OR
//   row.Kind == Squash AND version ∈ row.Replaces  -- transitive match via squash row

if (satisfiedSet covers replacedSet): auto-mark
else if (satisfiedSet is empty): fresh-install
else: MidRangeSquashException
```

`Replaces` recorded as authored; transitivity is runtime resolution.

### Open issues from original consensus — resolutions

| Open issue | Resolution |
|---|---|
| 1. Verification round cost on multi-node topologies | P0-1 + P0-4: delete `--skip-verify`; address cost via parallel A/B + snapshot A caching |
| 2. Replay non-determinism (`Now()`/`Guid.NewGuid()`) | P1-1: classifier non-determinism scan; refuse unless explicit override |
| 3. Component-template/extension version drift | P1-8: ITopologySignature schema versioning; canonicalizer-version retention (ADR-0021 A2) |
| 4. Cross-bucket N1QL with parameterized bucket names (Couchbase) | Deferred to v1.2 (Couchbase phase); current refuse-with-diagnostic is correct interim posture |
| 5. `IDataOpClassifier` false negatives in Postgres functions | P0-5: mandatory `[DataMigration]` annotation when heuristic suspects DML |
| 6. Index `v` field stripping in MongoDB vs version pinning | Deferred to v1.1 (MongoDB phase); current strip-with-diagnostic is correct interim posture |
| 7. Painless script semantic equivalence (OpenSearch) | Monitor: track verifier-refusal rate; escalate at >20% by month 6 (deferred to v1.2) |
| 8. Mid-range environment recovery via `--force-squash-from-mid-range` | P0-3: moved to separate `recover` subcommand; deterministic token gate; mandatory ticket-id + reason |

### Per-provider commitments updated

The original Round 1b per-provider commitments survive intact, with these v1-scope amendments:

- **Aerospike, MongoDB, OpenSearch, Couchbase:** ship `NullSquashStrategy` returning `Unsupported` in v1; full strategies in v1.1/v1.2 per sequencing above. Hand-authoring is NOT a documented fallback (the diagnostic points at the roadmap, not at do-it-yourself).
- **Postgres:** v1 path-finder; all hardening amendments apply (server-version-matched ephemeral container per P1-3, snapshot A caching per P0-4, etc.).

### Net assessment

The destructive model is materially safer with these P0+P1 amendments than the additive model that preceded it. **The center holds** — every Critical/High finding from Assessment 0007 is addressed; no architectural gut-rebuild required. Ready for `/nop:plan`.
