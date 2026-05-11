# ADR-0019: Migration Squash via `Replaces` Graph + Destructive Codegen

**Status:** Proposed (destructive-model reframe 2026-05-05; assessment 0007 amendments 2026-05-05; supersedes the additive framing in the original draft)
**Date:** 2026-05-04 (original) / 2026-05-05 (reframe + assessment-0007 amendments)
**Related design:** [docs/design/migration-squashing.md](../design/migration-squashing.md)
**Related ADRs:** ADR-0003 (Provider Record Store Contract), ADR-0004 (Reflection-Based Migration Discovery), ADR-0009 (Convention-Based Record IDs), ADR-0020 (Up-Only Squashes), ADR-0021 (Migration Record Checksum)

## Context

Long migration chains are a real problem at scale: fresh-environment provisioning slows to minutes, source-tree complexity grows monotonically, and seed-data clarity erodes as data ops accumulate across dozens of files. EF Core has had open issue #2174 ("Add support for migration squashing") since 2014; the .NET ecosystem still lacks a robust answer.

The [research artifact 0005](../research/0005-migration-squashing.md) surveyed nine ecosystems and found two dominant strategies:
- **Additive (Django-style):** new "squash" migration with a `replaces` graph; originals stay in source; mature envs auto-mark. Solves the partial-catch-up case but does NOT solve source-tree compaction.
- **Destructive (Flyway/Atlas-style):** new baseline replaces originals; originals removed from source; envs at boundaries handled cleanly; envs mid-range refused. Solves source-tree compaction (the operator's primary stated goal) at the cost of requiring fleet coordination.

Initial design adopted the additive model. After review, the operator's primary goals — **clean up potentially thousands of old migrations; improve provisioning time; improve seeding clarity** — were determined to be unachievable in the additive model. Source-tree retention defeats the compaction goal regardless of how `--prune` tooling archives the originals later. The design was reframed.

## Decision

We will adopt a **destructive squash** model: codegen-and-replace, fleet-coordinated, additive only at the *ledger* level.

### Squash workflow (operator-initiated, build-time)

1. **Operator selects a contiguous version range `[N..M]`** of migrations to squash.
2. **Tool spins an ephemeral provider container** (reusing the existing test-container infrastructure under [`tests/.../Container/`](../../tests/Hyperbee.Migrations.Integration.Tests/Container/)).
3. **Tool applies all migrations with version < N** (the residual head). Captures **snapshot A** = state at start of the squash range.
4. **Tool applies migrations [N..M]** sequentially. Captures **snapshot B** = state at end of the squash range.
5. **Tool diffs A and B** using the provider's snapshot-diff mechanism (`ISquashStrategy`). The diff is the delta the squash body must produce.
6. **Tool serializes the diff** into a migration body in the provider's native shape (`statements.json` for resource-based providers, code for code-only providers).
7. **Tool runs fleet readiness check** against a manifest of known environment ledger sources: every environment must be at version `>= M` (post-range) OR `< N` (pre-range). If any environment is mid-range, the tool refuses with a clear remediation: "sync env X past v_M before squashing."
8. **If readiness is green, the tool emits**:
   - `Squash_M.cs` — a class declaring `[Migration(version: M+ε, ReplacesRange = "N-M")]` and an `UpAsync` body that applies the diff resource.
   - The resource bytes (`Squash_M.statements.json` or `Squash_M.sql`).
   - **Removes the original migration source files for `[N..M]`** from the migrations folder.
9. **Operator commits, deploys.**

### Reconciliation per environment

When the deployed code reaches each environment, the runner reconciles:

- **Mature env** (had run all of `[N..M]` before squash shipped): ledger contains rows for every replaced version. `Replaces` graph matches → **auto-mark** the squash record without invoking `UpAsync`. The historical ledger rows for `[N..M]` are preserved forever as audit trail; the squash row is added with `Kind = Squash`. Provisioning is unchanged for this env on next migration run.

- **Fresh env** (empty ledger; never ran originals; originals no longer exist in source): runner applies the residual head (migrations with version `< N`), then runs the squash body (the A→B delta). Single-step provisioning from the pre-range state to the post-range state.

- **Mid-range env** (some `[N..M]` rows in ledger, but not all): the originals these rows correspond to no longer exist in source. The runner **refuses with `MidRangeSquashException`** naming the offending versions and the remediation paths:
  1. Restore from backup taken before any of the missing originals were applied.
  2. Use `--force-squash-from-mid-range` flag to apply the squash body's A→B delta against current state — may corrupt data if the env's actual state is not exactly B.
  3. Re-introduce the missing originals from git history (specific commit) and re-run.

This case should not occur if the fleet readiness check (step 7) was honored. It is a defense-in-depth refusal, not a normal path.

### Attribute and record contract

Extension to `[Migration]` (named-arg, no new attribute):

```csharp
[AttributeUsage(AttributeTargets.Class, AllowMultiple = false)]
public sealed class MigrationAttribute : Attribute
{
    public long Version { get; }
    public string[] Profiles { get; init; } = Array.Empty<string>();

    // Explicit list of versions this migration subsumes when applied as a squash.
    public long[] Replaces { get; init; } = Array.Empty<long>();

    // Compact range syntax — resolved at discovery against the assembly's actual
    // [Migration] versions in [start..end] inclusive. Combinable with Replaces.
    // Examples: "1000-1500", "1000-1199, 1300, 1400-1450"
    public string ReplacesRange { get; init; } = "";

    public MigrationAttribute(long version) => Version = version;
}
```

Empty `Replaces` AND empty `ReplacesRange` means "regular migration." Either non-empty makes the migration a squash. The runner resolves both into a unified sorted version set at discovery time; the resolved set is what contributes to the squash's checksum (per ADR-0021 + IR-N2 immutability rule).

### Reconciliation runtime

For each discovered migration with non-empty resolved `Replaces`:

```
1. let allReplacedApplied = await store.IntersectWithAppliedAsync(replaced);
   // Per realtime obligation: providers SHALL use realtime point-lookup APIs (_mget,
   // findOneAndProject, KV Get with mutation tokens, SELECT with FOR UPDATE), never
   // eventually-consistent search.

2. if (count(allReplacedApplied) == count(replaced)):
       // MATURE — auto-mark
       await store.WriteAsync(squashRecord, WritePrecondition.MustNotExist);
       continue;  // do not invoke UpAsync

3. if (count(allReplacedApplied) == 0):
       // FRESH — run UpAsync
       // ApplyMode = MigrationApplyMode.Fresh
       await migration.UpAsync(ctx);
       await store.WriteAsync(squashRecord, WritePrecondition.MustNotExist);
       continue;

4. // strict subset — MID-RANGE
   throw new MidRangeSquashException(squash.Version, replaced, applied: allReplacedApplied);
```

### Fleet readiness check (v1 mandatory)

The squash CLI requires a manifest before generating:

```yaml
# fleet.yml
environments:
  - name: prod-us-east
    connection: ${POSTGRES_PROD_US_EAST}
  - name: prod-eu-west
    connection: ${POSTGRES_PROD_EU_WEST}
  - name: staging
    connection: ${POSTGRES_STAGING}
  - name: ci
    ledger-export: ./fleet-snapshots/ci-ledger.json    # alternative: dump file
  - name: dev-shared
    connection: ${POSTGRES_DEV_SHARED}
```

The tool reads each environment's ledger, computes max applied version, and refuses if any falls in `[N..M)`.

Operators may opt out of named environments via `--accept-stranding=name1,name2` (per-env scoped, not blanket); doing so is logged in the squash audit trail.

### Squash body generation (per-provider — `ISquashStrategy`)

```csharp
public interface ISquashStrategy
{
    Task<SquashGenerationResult> GenerateAsync(
        ISquashGenerationContext ctx,
        IReadOnlyList<MigrationDescriptor> sourceRange,
        SquashGenerationOptions options,
        CancellationToken ct);
}

public abstract record SquashGenerationResult
{
    public sealed record Generated(
        string ResourceContent,                    // SQL or statements.json bytes
        IReadOnlyList<long> Replaces,              // versions this squash subsumes
        IReadOnlyDictionary<string, string> Diagnostics
    ) : SquashGenerationResult;
    public sealed record Unsupported(string Reason) : SquashGenerationResult;
    public sealed record Failed(string Detail, Exception? Cause) : SquashGenerationResult;
}
```

Provider implementations spin their own ephemeral container, apply migrations < N, capture A; apply [N..M], capture B; diff A vs B; serialize the diff. v1 ships with at minimum **Postgres `PgDumpSnapshotStrategy`**.

Providers without a strategy implementation register the default `NullSquashStrategy` returning `Unsupported("hand-author squashes for this provider")`. Hand-authoring remains supported but is the fallback, not the canonical path.

### Squash classification

Squashes are themselves migrations. A future operator can squash a previous squash plus subsequent migrations: `Squash_3000` with `ReplacesRange = "2000-2500"` may include a previous `Squash_2000` and the migrations that came after it. The mechanism composes naturally because squashes carry checksum-bearing ledger rows like any other migration.

## Consequences

**Positive:**
- Source tree stays small over time. Operators can squash thousands of accumulated migrations into one and the tree shrinks accordingly.
- Provisioning time reflects the current squashed state, not the historical chain.
- Codegen automates the painful part of squashing — operators don't hand-translate hundreds of statements.
- Per-original ledger history is preserved indefinitely (audit forensics still work).
- Fleet readiness check forces the discipline that EF Core's manual squash workflow assumes but never enforces.
- The Django-style `Replaces` graph still earns its keep for mature-env auto-mark.

**Negative:**
- Operators must maintain a fleet manifest (or accept `--accept-stranding`). For projects with one or two environments this is overhead; for projects with many environments it's the minimum competence the operation requires.
- Mid-range environments are a hard error, not a soft recovery. The destructive model trades the additive model's automatic catch-up for source-tree compaction.
- v1 must ship with codegen for at least one provider (Postgres). Per-provider snapshot-diff infrastructure has real implementation cost — every provider needs ephemeral-container scaffolding, snapshot capture, and deterministic diff.
- Operators who use `--force-squash-from-mid-range` accept potential data corruption. The flag exists because the alternative (refuse to run) leaves the env unrecoverable; both paths are bad.

**Neutral:**
- Squash ≠ destruction of history. Ledger rows persist forever. Git history retains the original migration files. The squash CLI's audit trail records every `--accept-stranding` use. Forensics remain possible; the migrations folder simply isn't the storage medium.
- Squashes can be re-squashed naturally; the design composes.

## Required cross-cutting amendments inherited from the multi-advocate consensus

The destructive model inherits these consensus amendments (from [docs/design/migration-squashing-consensus.md](../design/migration-squashing-consensus.md)) verbatim — they apply identically:

- **U1:** Abstract `WritePrecondition` over per-provider tokens (`MustNotExist`, `MustMatchVersion(opaqueToken)`).
- **U2:** `IntersectWithAppliedAsync(candidateIds, ct)` realtime obligation.
- **U4:** `MigrationApplyMode` enum (Fresh / PartialCatchUp; PartialCatchUp not reachable in destructive model under happy path but kept for individual original execution before squash exists).
- **U5:** DDL preserved verbatim; `SquashHints` applies only to data ops.
- **U6:** TTL-based locks must auto-renew or be configurably-long during snapshot apply.
- **U7:** Async-build barrier honored during snapshot capture (Aerospike SI build, Couchbase GSI build, OpenSearch refresh).
- **U8:** Checksum is over RAW resource bytes (pre-substitution).
- **U9:** Snapshot fidelity is per-provider, with explicit out-of-scope manifest.

The amendments to ADR-0019 specifically:
- **R-08 ("originals stay in source") is deleted.** Replaced with fleet readiness check at squash creation.
- **R-15 (`--prune` audit-aware tool) is promoted from Phase 4 to v1 mandatory.** It becomes the readiness check.
- **`IRollupStrategy` renamed `ISquashStrategy`** and promoted from Phase 2 deferred to v1 contract; at least one provider must ship a strategy in v1.

## Alternatives Considered

- **Additive (Django-style) squash with `replaces` graph and originals retained.** Solves partial-catch-up automatically but does NOT solve source-tree compaction. Rejected after operator goal clarification.
- **Hybrid: Django-style with optional `--prune` archive.** Adds two-phase deprecation discipline that operators rarely follow. Rejected as worse-than-either pure model.
- **Replay-recorder: capture every cluster mutation during apply and emit as squash.** Per-provider mutation capture is fragile; secret-leak risk; rejected per assessment.
- **Manual hand-authored only.** Forces operators to translate hundreds of statements by hand; defeats the codegen value proposition. Rejected.

## Amendments from Assessment 0007 (2026-05-05)

The full `/nop:assess` ([0007](../research/0007-migration-squashing-destructive-assessment.md)) on the destructive-model consensus + 5 implementation examples produced 9 P0 + 10 P1 amendments. The following amend the Decision section above:

### A1 (P0-1): `--skip-verify` is removed entirely

Per finding PM-1: verification cost economics (Postgres ~95s, OpenSearch 3-node ~204s, 10 dev iterations × 204s = 34 minutes) creates a slow-burn cultural drift toward `--skip-verify`; once normalized, a canonicalizer regression ships unverified, originals are destroyed, recovery is unrecoverable. The flag is **not part of v1**. Verification cost is addressed via A4 (snapshot caching + parallel A/B capture), not via an escape valve.

### A2 (P0-2): Two-phase fleet readiness gate

Per findings PM-2 + MD-2 + IR refinement: fleet manifest as single source of truth fails open (operator adds env to AWS, forgets `fleet.yml`). Two-phase gate:

**Phase 1 (squash creation, step 7 in workflow):** unchanged — read each environment's ledger from manifest, refuse if any is in `[N..M)`.

**Phase 2 (deploy time):** the squash artifact records `expected-fleet-versions: {env: minVersion}` (captured at squash creation) AND `max-staleness-window: <duration>` (default 30 days). At each environment's deploy time, the runner re-reads the ledger:
- If env not present in `expected-fleet-versions`: refuse with `UnregisteredEnvironmentException`.
- If env's actual version is below recorded minimum AND env hasn't moved within the staleness window: refuse with `StaleFleetMemberException`.

This converts silent stranding into a deploy-time refusal, which is recoverable via re-introducing originals from git OR backup-restore. The original migrations are gone, but the *exception* is loud, not silent.

### A3 (P0-3): `--force-from-current` moves to a separate `recover` subcommand

Per CP-1 synthesis + IR-CP-1: the flag's friction surface is mismatched with its blast radius. Three changes:

1. **Move out of `squash` subcommand into `dotnet hyperbee-migrations recover from-mid-range`** — separate verb makes destructive nature obvious, prevents copy-paste contamination of normal-path runbooks.
2. **Deterministic token gate.** Token = `SHA-256(env-name ‖ squash-version ‖ missing-versions-set)[:12]`. Token is reproducible across retries (so runbooks remain valid) but unique per (env, squash, gap), so it can't be muscle-memoried across incidents.
3. **Mandatory paired arguments:** `--accept-data-corruption-risk=<token>` + `--ticket-id=<>` + `--reason=<≥20 chars>`. All three logged in audit trail.

Backup-restore remains the documented primary recovery path. The flag is documented as "last resort, DBA-supervised, post-incident only."

### A4 (P0-4): Snapshot A caching + parallel A/B capture

Per findings PA-1 + PA-2: the sequential 3-container pipeline (apply A residual head, apply [N..M], apply squash for verification) is trivially parallelizable but specified sequential. Three changes:

1. **Snapshot A cached** by `hash(provider, residual-head-version-set, canonicalizer-version, topology-signature, image-version)`. First squash regeneration pays full cost; subsequent regenerations skip Container A entirely.
2. **A and B captured in parallel** via `Task.WhenAll` over independent containers. Sequential await pattern in implementation examples is a bug.
3. **Container reuse for verification:** Container A's residual-head state can be reused as the verification base after A is captured — apply the generated squash there instead of spinning a third container.

Target: OpenSearch 3-node 204s → ~70s; Postgres 95s → ~40s. Eliminates the cost basis that drives `--skip-verify` pressure.

### A5 (P0-5): `[DataMigration]` annotation is mandatory when classifier heuristic detects suspected DML

Per CP-2 (Red wins): heuristic-only classification has a false-negative rate that's unacceptable for a destructive operation. The classifier returns `RequiresAnnotation=true` when it heuristically detects possible DML on a migration class lacking either `[DataMigration]` (acknowledge → carry-forward) or `[StructuralOnly]` (assert heuristic wrong → suppress, logged in audit trail).

The squash CLI **refuses** with diagnostic naming the migration and the suspected statement/call. Author either annotates or refines. Silent false-negatives become loud false-positives — the safer error direction for destructive operations.

### A6 (P0-6): Re-squash transitivity rule

Per IR-N1: when `Squash_3000` replaces `Squash_2000` plus newer migrations, an environment that auto-marked `Squash_2000` (ledger has the squash row but not the underlying replaced rows) needs the runner to recognize the squash row as satisfying the replacement obligation transitively.

Reconciliation pseudocode amended:

```
let replacedSet = squash.Replaces  // recorded as authored, NOT transitively expanded
let satisfiedSet = await store.IntersectWithSquashedAsync(replacedSet)

// IntersectWithSquashedAsync semantics: returns versions where
//   row.Kind == Migration AND row.Id == version  -- direct match
// OR
//   row.Kind == Squash AND version ∈ row.Replaces  -- transitive match via squash row

if (satisfiedSet covers replacedSet): auto-mark
else if (satisfiedSet is empty): fresh-install
else: MidRangeSquashException
```

`Replaces` is recorded as authored in the squash; transitivity is a runtime resolution concern. Composes naturally for re-re-squashing.

### A7 (P0-9): v1 ships all five providers (RETRACTED 2026-05-09)

**Original (Assessment 0007 IR-CP-4):** v1 ships Postgres only; NoSQL providers ship in v1.1 / v1.2 because OpenSearch and Couchbase are High canonicalization risk and the first production exposure should be lower-risk.

**Retracted 2026-05-09:** v1 ships squash codegen for **all five providers** (Aerospike, Couchbase, MongoDB, OpenSearch, Postgres) together.

**Why the reversal:** validating the `ISquashStrategy` abstraction against ONE provider proves that one implementation, not the abstraction. If the shape is wrong, the gap surfaces in v1.1+ when the second provider can't fit — at which point the v1 API is already locked and the wart is permanent. Implementing all five together is the only way to know the abstraction is correct, and that proof is more valuable than the canonicalization-risk hedge the original phasing was buying.

| Provider | Snapshot strategy | Canonicalization risk |
|---|---|---|
| Postgres | `PgDumpSnapshotStrategy` | Medium (`pg_dump` is mature) |
| Aerospike | `InfoSnapshotStrategy` | Low |
| MongoDB | `IntrospectionSnapshotStrategy` | Medium-High |
| OpenSearch | `RestStateDiffStrategy` | High |
| Couchbase | `HybridStrategy` | High |

The High-risk providers (OpenSearch, Couchbase) require harder canonicalization work but ship together with the rest. Verifier-refusal rates and corpus metrics are tracked per provider; a high refusal rate on one provider does not block the others' release. **Hand-authoring is NOT a fallback** for any provider — `NullSquashStrategy` is removed from v1 (see amendment A11 deletion of `Unsupported` "hand-author" guidance).

### A8 (P1-1): Non-determinism scan in classifier

Per finding PM-6: data-op carry-forward bodies that capture wall-clock state silently produce cross-environment data divergence. The verification round at squash creation time cannot detect non-determinism that manifests at future replay time.

Classifier scans for whitelist of approved deterministic call sites; refuses when any of the following non-deterministic patterns appear without explicit `accept-non-deterministic-data-ops=true` override (with explicit override-record-list naming the migrations):

- `DateTime.Now`, `DateTime.UtcNow`, `DateTimeOffset.Now`, `DateTimeOffset.UtcNow`
- `Guid.NewGuid()`
- `Random` instantiation without explicit seed
- `Environment.MachineName`, `Environment.UserName`
- `Stopwatch.GetTimestamp()`
- `Process.Id`
- `Activity.Current?.TraceId`, `Activity.Current?.Id`
- `IPGlobalProperties.GetHostName()`
- `Assembly.GetExecutingAssembly().Location`

Whitelist approach (rather than ban-list-only) per IR refinement: author negligence is far more dangerous than author maliciousness; default-deny-on-unrecognized is the right error direction.

### A9 (P1-2): Override block uses structured fields

Per CP-5 (Red wins): `≥20 chars` reason field is theater ("blah blah blah blah twenty" passes). Replaced with structured fields:

```yaml
squash-overrides:
  accept-stranding:
    - env: dev-shared
      ticket-id: HBM-1234            # regex-validated; default ^[A-Z]+-\d+$
      owner: brentfarmer             # validated against last-90-days git authors
      reason: "Dev cluster intentionally lags main; sync after sprint review"
      expires: 2026-06-04
```

CI lint:
- `ticket-id` matches configured regex (default `^[A-Z]+-\d+$`)
- If `tracker-url` is configured in fleet.yml, ticket-id resolves over HTTP
- `owner` matches an author from last-90-days git commit log
- `expires` is present and ≤90 days from creation (P1-10 below)

Default `expires` per A18 (IR-CP-2): 30 days; 90-day hard cap.

### A10 (P1-3): pg_dump runs inside server-version-matched container

Per finding PM-3: bundling N pg_dump versions in the squash CLI image bloats and rots over 18 months. Replace with: spin server-version-matched ephemeral Postgres container; run `pg_dump` *inside* it via `docker exec`. Single canonical image; client tooling is dumper-version-clean. Cost: one extra exec hop (~50ms).

Document Docker-in-Docker prerequisite explicitly. CI environments without Docker socket access cannot run squash codegen — fail fast with clear remediation.

### A11 (P1-4): Per-environment stranding reasons

Per finding MD-1: `--accept-stranding=name1,name2,name3` is wildcard-equivalent under deadline pressure. Replaced with per-env reason requirement:

```bash
dotnet hyperbee-migrations squash --range 1000-1500 \
  --accept-stranding=dev-shared --reason-stranding=dev-shared='Decommissioning 2026-05-15 per HBM-1234' \
  --accept-stranding=ci-runner-3 --reason-stranding=ci-runner-3='Replaced 2026-04-30; pending teardown'
```

CLI refuses without one `--reason-stranding=name=<≥20 chars>` per named env. Audit trail records all reasons.

### A12 (P1-5): Rename detection is opt-in, warn-only

Per CP-3 (Red wins): edit-distance heuristic on identifiers produces too many false positives (`name`→`age` is distance 3) to be a hard gate. The verification round + mandatory `[DataMigration]` annotation (A5) carry the data-loss-prevention weight.

Field-rename detection:
- `enable-rename-heuristic: false` by default per range
- When enabled, classifier emits `Warning` (never `Refusal`) naming suspect pairs
- Author can suppress by annotating the source migration with `@rename(from: "user_id", to: "user_uid")`

### A13 (P1-6): Third artifact `Squash_M.summary.md`

Per CP-4 (Red wins): the verification round proves bytes match; it does not prove intent matches the source range. A canonicalization regression that affects both A and B identically passes verification but ships wrong-by-intent code.

Squash CLI emits a third artifact alongside the body resource:

`Squash_M.summary.md` containing:
- Statement count by category (CREATE TABLE, CREATE INDEX, ALTER TABLE, INSERT/UPDATE/DELETE, CREATE FUNCTION, etc.)
- Table list (created, dropped, modified)
- Sequence list (with setval values for non-default last_value per Postgres)
- Index list (created, dropped)
- Dropped-objects list (objects present at A but not B — visibility for review)
- Data-ops-source-list (which originals contributed carry-forward DML)
- Topology signature (recorded for replay-time compatibility check)
- Override block in effect at squash creation

PR template requires this artifact pasted into the description. Reviewers compare the summary against the migration range's commit log, not the artifact bytes.

### A14 (P1-8): Topology signature schema versioning

Per finding PM-8: when a provider adds a new topology axis (e.g., Postgres logical replication), old squashes' signatures lack the new field; new runtime's `IsCompatibleWith` doesn't know how to interpret missing values.

`ITopologySignature` artifacts carry `signature-schema-version: <int>`. Each provider ships migration logic when adding axes:

```csharp
// Example: Postgres adds replication-role in v1.5
public class PostgresTopologySignature : ITopologySignature
{
    public int SchemaVersion { get; init; }  // 1, 2, ...
    // ... existing fields ...
    public string? ReplicationRole { get; init; }  // new in schema-version 2

    public bool IsCompatibleWith(ITopologySignature other, out string? reason)
    {
        // Migrate older signatures forward with documented defaults:
        // schema-version 1 implies ReplicationRole = "primary"
        // ...
    }
}
```

Topology signature changes require a new ADR documenting back-compat semantics for each prior version. Lint at PR time. `--allow-topology-skew` becomes the explicit opt-out; never silent.

### A15 (P1-10): Override expiry default 30 days

Per IR-CP-2 (Red wins): 90 days = a full quarter, operationally indistinguishable from "permanent." 30 days = 2 sprints, aligning with sprint review cadence and forcing re-justification.

- Default `expires`: **30 days** from creation
- Hard cap: **90 days**
- CI warns at 7 days remaining
- CI refuses to apply squash with expired override
- Override renewal is cheap (re-edit YAML, re-commit, CI passes); friction is the *justification*, not the renewal

### A16 (P0-7): Generation determinism gate (C12)

Per IR-N2: C5 covers replay determinism (applying the squash twice produces same end state), not generation determinism (running squash codegen twice produces same artifact bytes). Without this, squash artifact `Checksum` (per ADR-0021) is unstable across rebuilds — re-generating to incorporate a fix produces a new checksum, breaking auto-mark on environments that already have the prior squash row.

**C12: Generation determinism gate** (added to consensus + per-provider CI):

CI test per provider: run `squash --range R` twice in fresh ephemeral containers; assert byte-equal:
- `Squash_M.{sql,statements.json}` body
- `Squash_M.summary.md` artifact
- Topology signature

Sources of nondeterminism that must be eliminated by canonicalization:
- Wall-clock timestamps in artifact headers
- GUIDs (any new generation)
- Container UUIDs / port assignments
- Dictionary iteration order (use `SortedDictionary` or sort-on-emit)

Failures gate release.

### A17 (P0-8): `Kind`/`Replaces` consistency enforcement

Per IR-N3: ADR-0021 defines `Kind = Squash` but ADR-0019 reconciliation doesn't reference `Kind`. Without consistency enforcement, the checksum is undermined — ledger-write attacker (or buggy migration) could promote a regular migration to squash retroactively without changing checksum.

Amended in ADR-0021 (see ADR-0021 amendment A1):
- Provider record stores enforce on **write** that `Kind == Squash ⟺ Replaces non-empty`
- Mismatch raises `MigrationLedgerIntegrityException`
- Runner refuses to load a ledger with inconsistent rows on **read**

Amended here (ADR-0019) reconciliation: "satisfying" predicate from A6 explicitly requires `Kind` consistency:

```
satisfies(version, row) :=
   (row.Kind == Migration AND row.Id == IdFor(version))
   OR
   (row.Kind == Squash AND version ∈ row.Replaces AND row.Replaces is non-empty)
```

A row with `Kind = Migration` AND non-empty `Replaces` (or vice versa) is `MigrationLedgerIntegrityException` at load time, never silent acceptance.

### A18 (P1-9): Verification container lifecycle (C13)

Per IR-N4: if verification fails (B' diverges from B), the ephemeral container leaks. Disk fills after a few failed attempts. Operators iterate on canonicalization regressions; failure is the expected debug path.

**C13: Verification container lifecycle** (added to consensus):

- **Success:** container torn down immediately after byte-equal assertion.
- **Failure:** container torn down by default; retained ONLY with `--keep-failed-container` flag (under labeled name; reconnect instructions printed).
- **Always:** debug summary written to `./squash-debug/<timestamp>/` containing canonicalized B and B' bodies for offline diff.
- **`try/finally`** wraps the verification block — Ctrl-C does not leak containers.

### A19: ADR-0019 negative consequences updated

The "Negative" section of the original Decision is updated with these post-amendment realities:
- Operators must now provide structured override fields (ticket-id + owner + reason); higher friction for projects without a tracker (mitigated by stub-resolves regex default)
- v1 ships squash codegen for all five providers concurrently (per A7 retraction 2026-05-09); the High-risk providers (OpenSearch, Couchbase) ship alongside the lower-risk ones because the abstraction is only proven by being implemented against the full provider matrix
- The `recover from-mid-range` subcommand exists; operators must understand it is non-normal-path tooling

## References

- Research: [docs/research/0005-migration-squashing.md](../research/0005-migration-squashing.md), Findings 1-2
- Requirements: [docs/requirements/migration-squashing.md](../requirements/migration-squashing.md)
- Design: [docs/design/migration-squashing.md](../design/migration-squashing.md)
- Multi-advocate consensus (additive, superseded): [docs/design/migration-squashing-consensus.md](../design/migration-squashing-consensus.md)
- Multi-advocate consensus (destructive, ratified): [docs/design/migration-squashing-consensus-destructive.md](../design/migration-squashing-consensus-destructive.md)
- Assessment 0006 (additive, historical): [docs/research/0006-migration-squashing-assessment.md](../research/0006-migration-squashing-assessment.md)
- **Assessment 0007 (destructive, drives this ADR's amendments):** [docs/research/0007-migration-squashing-destructive-assessment.md](../research/0007-migration-squashing-destructive-assessment.md)
- EF Core reference: [docs/research/ef-core-squash-reference.md](../research/ef-core-squash-reference.md)
- [Django squashmigrations docs](https://docs.djangoproject.com/en/5.1/topics/migrations/#squashing-migrations)
- [Flyway baseline workflow (Redgate)](https://www.red-gate.com/hub/product-learning/flyway/flyway-baselines-and-consolidations)
- [Atlas migrate diff](https://atlasgo.io/versioned/diff)
- [dotnet/efcore #2174](https://github.com/dotnet/efcore/issues/2174)
