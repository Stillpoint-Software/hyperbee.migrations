# ADR-0019: Migration Squash via `Replaces` Graph + Destructive Codegen

**Status:** Accepted
**Date:** 2026-05-04 (decided) / 2026-05-11 (accepted after all five providers shipped)
**Related ADRs:** ADR-0003 (Provider Record Store Contract), ADR-0004 (Reflection-Based Migration Discovery), ADR-0009 (Convention-Based Record IDs), ADR-0020 (Up-Only Squashes), ADR-0021 (Migration Record Checksum), ADR-0024 (Migration Host Discovery), ADR-0025 (NullSquashStrategy as Extension Point), ADR-0026 (Deploy-Time Fleet Gate Cut)

## Context

Long migration chains are a real problem at scale: fresh-environment
provisioning slows to minutes, the source tree grows monotonically, and
seed-data clarity erodes as data ops accumulate across dozens of files.
The .NET ecosystem lacks a robust answer (EF Core issue #2174, "migration
squashing", open since 2014).

Two strategies exist in the wider ecosystem:

- **Additive (Django-style):** a new squash migration with a `replaces`
  graph; originals stay in source; mature environments auto-mark. Solves
  partial catch-up but does not compact the source tree.
- **Destructive (Flyway/Atlas-style):** a generated baseline replaces the
  originals, which are removed from source; environments at range
  boundaries are handled cleanly; mid-range environments are refused.
  Compacts the source tree at the cost of requiring fleet coordination.

The operator's primary goals -- compact thousands of accumulated
migrations, improve provisioning time, improve seeding clarity -- are
unachievable in the additive model, because retaining originals in source
defeats compaction regardless of later archival tooling. The destructive
model is therefore adopted, additive only at the *ledger* level (history
rows are never destroyed).

## Decision

Adopt a **destructive squash** model: codegen-and-replace, fleet-coordinated
at generation time, additive at the ledger.

### Squash workflow (operator-initiated, build-time)

1. Operator selects a contiguous version range `[N..M]`.
2. The tool spins an ephemeral provider container (the squash provider
   packages own this; the CLI references no provider, per ADR-0024).
3. Apply all migrations with version `< N` (the residual head); capture
   **snapshot A**.
4. Apply migrations `[N..M]` sequentially; capture **snapshot B**.
5. Diff A and B via the provider's `ISquashStrategy`. The diff is the
   delta the squash body must produce.
6. Serialize the diff into the provider's native body shape (the
   `.statements` script form per ADR-0022, or `.sql` for Postgres).
7. Run the generation-time fleet readiness gate (below).
8. If readiness is green, emit:
   - `Squash_M.cs` -- a class declaring `[Migration(M, ReplacesRange = "N-M")]`
     with an `UpAsync` body that applies the diff resource.
   - the resource bytes,
   - `Squash_M.summary.md` (the human-review artifact, below).
   Then remove the original `[N..M]` source files.
9. Operator reviews the summary, commits, and deploys.

Snapshot A is cached by `hash(provider, residual-head version set,
canonicalizer version, topology signature, image version)`; A and B are
captured in parallel over independent containers; the verification base
reuses Container A's residual-head state rather than spinning a third
container. There is no `--skip-verify` escape valve -- verification cost
is addressed by caching/parallelism, not by an opt-out, because a
normalized skip would ship a canonicalizer regression against destroyed
originals.

### Reconciliation per environment

For each discovered migration whose resolved `Replaces` set is non-empty,
the runner resolves satisfaction transitively:

```
satisfies(version, row) :=
   (row.Kind == Migration AND row.Id == IdFor(version))
   OR
   (row.Kind == Squash   AND version in row.Replaces AND row.Replaces non-empty)

satisfied = store.IntersectWithSquashedAsync(squash.Replaces)

if satisfied covers Replaces:   auto-mark  (write squash row, do not run UpAsync)
elif satisfied is empty:        fresh      (run UpAsync, then write squash row)
else:                           throw MidRangeSquashException(version, missing)
```

- **Mature env** -- ledger satisfies every replaced version (directly, or
  transitively via an inner squash row): the squash row is written with
  `Kind = Squash` and `UpAsync` is not invoked. Historical rows for
  `[N..M]` are preserved forever as audit trail.
- **Fresh env** -- empty ledger: the runner applies the residual head then
  runs the squash body (the A->B delta) as a single-step baseline.
- **Mid-range env** -- a strict subset of `[N..M]` is satisfied and the
  originals no longer exist in source: the runner refuses with
  `MidRangeSquashException` naming the missing versions. Recovery is the
  `recover from-mid-range` subcommand (below) or backup-restore. This
  path should not occur when the generation-time gate was honored; it is
  a loud defense-in-depth refusal, never silent stranding.

`Replaces` is recorded as authored (not transitively expanded);
transitivity is a runtime resolution concern, so re-squashing a prior
squash plus later migrations composes naturally.

### Attribute and record contract

`[Migration]` is extended with named arguments (no new attribute):

```csharp
public sealed class MigrationAttribute : Attribute
{
    public long   Version       { get; }
    public string[] Profiles    { get; init; } = Array.Empty<string>();

    // Explicit list of versions this migration subsumes as a squash.
    public long[] Replaces      { get; init; } = Array.Empty<long>();

    // Compact range, resolved at discovery against the assembly's actual
    // [Migration] versions, [start..end] inclusive, combinable with
    // Replaces. e.g. "1000-1500", "1000-1199, 1300, 1400-1450"
    public string ReplacesRange { get; init; } = "";

    public MigrationAttribute(long version) => Version = version;
}
```

Empty `Replaces` and empty `ReplacesRange` means a regular migration;
either non-empty makes it a squash. The runner resolves both into one
sorted version set at discovery time; that resolved set contributes to
the squash's checksum (ADR-0021), so the checksum is immutable for a
given authored range.

Record-store consistency is enforced on write and read:
`Kind == Squash` iff `Replaces` is non-empty; a mismatched row raises
`MigrationLedgerIntegrityException` (the runner refuses to load a ledger
containing one). This closes a checksum-bypass where a regular migration
could be retroactively promoted to a squash.

### Squash body generation (per provider -- `ISquashStrategy`)

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
        string ResourceContent,
        IReadOnlyList<long> Replaces,
        IReadOnlyDictionary<string, string> Diagnostics) : SquashGenerationResult;

    public sealed record Failed(string Detail, Exception? Cause) : SquashGenerationResult;
}
```

All five providers ship a real strategy in v1 -- Postgres
`PgDumpSnapshotStrategy`, Aerospike `InfoSnapshotStrategy`, MongoDB
`IntrospectionSnapshotStrategy`, OpenSearch `RestStateDiffStrategy`,
Couchbase `HybridStrategy`. Shipping all five together (rather than
Postgres-first with NoSQL deferred) is deliberate: validating the
`ISquashStrategy` abstraction against one provider proves that
implementation, not the abstraction; a shape gap would otherwise surface
only after the v1 API was locked. Hand-authoring is not a fallback for
any provider. `NullSquashStrategy` is retained as a public extension
point for third-party providers (ADR-0025), not as a first-party path,
which is why `SquashGenerationResult` has no `Unsupported` variant.

`pg_dump` runs *inside* a server-version-matched ephemeral Postgres
container (via `docker exec`), so the CLI image carries no bundled dumper
versions to rot. Docker socket access is a documented prerequisite;
environments without it cannot run squash codegen and fail fast.

### Data-op classification (mandatory annotation)

Heuristic-only classification has a false-negative rate unacceptable for
a destructive operation. The Roslyn source scanner returns
"requires annotation" when it detects suspected DML on a migration class
lacking either `[DataMigration]` (acknowledge -> carry forward) or
`[StructuralOnly]` (assert the heuristic wrong -> suppress, audited). The
CLI refuses with a diagnostic naming the migration and statement; the
author annotates or refines. Silent false-negatives become loud
false-positives -- the safe error direction here.

The scanner also default-denies non-deterministic data ops: `DateTime.Now`
/`UtcNow`, `DateTimeOffset.Now`/`UtcNow`, `Guid.NewGuid()`, unseeded
`Random`, `Environment.MachineName`/`UserName`, `Stopwatch.GetTimestamp()`,
`Process.Id`, `Activity.Current` ids, host name, executing-assembly
location. These produce cross-environment data divergence at replay time
that the generation-time verification round cannot detect, so they are
refused unless explicitly acknowledged with a named override list.
Field-rename detection is opt-in and warn-only (edit-distance heuristics
produce too many false positives to gate on); the verification round and
mandatory `[DataMigration]` annotation carry the data-loss-prevention
weight.

### Fleet readiness gate (generation time)

The squash CLI requires a fleet manifest before generating (required by
default; an explicit, audited `--no-fleet-manifest="<reason>"` bypass
exists for solo-environment squashes). It reads each environment's
ledger, computes the max applied version, and refuses
(`MidRangeFleetException`) if any environment is mid-range `[N..M)`.

Per-environment stranding overrides use structured fields, not a
free-text reason (a character-count reason is theater):

```yaml
squash-overrides:
  accept-stranding:
    - env: dev-shared
      ticket-id: HBM-1234            # regex-validated, default ^[A-Z]+-\d+$
      owner: brentfarmer             # validated against last-90-days git authors
      reason: "Dev cluster intentionally lags main; sync after sprint review"
      expires: 2026-06-04            # default 30 days, 90-day hard cap
```

CI lints the override (ticket-id regex, optional tracker-URL resolution,
owner against recent git authors, expiry present and within cap; warns at
7 days remaining; refuses an expired override).

A *deploy-time* second phase (re-checking fleet staleness at each
environment via `expected-fleet-versions` + `max-staleness-window`,
`UnregisteredEnvironmentException` / `StaleFleetMemberException`) was
specified here originally but **cut before ship as redundant** -- the
wired apply-time `MidRangeSquashException` refusal plus `recover
from-mid-range` and ledger-integrity checking already make silent
stranding a loud, recoverable error. See ADR-0026.

### Recovery (`recover from-mid-range`)

Recovery from a mid-range environment is a separate verb
(`hyperbee-migrations recover from-mid-range`), not a `squash` flag, so
its destructive nature is obvious and it cannot contaminate normal-path
runbooks by copy-paste. It is gated by a deterministic acknowledgement
token = `SHA-256(env-name | squash-version | missing-versions-set)[:12]`
-- reproducible across retries (runbooks stay valid) but unique per
`(env, squash, gap)` so it cannot be muscle-memoried across incidents.
The verb persists the acknowledgement to the ledger; the runner consumes
it on the next run, force-marks the squash without running its body, and
deletes the recovery row, re-verifying the token first. Backup-restore
remains the documented primary recovery path; this verb is
"last resort, DBA-supervised, post-incident."

### Review artifact and determinism gate

The CLI emits `Squash_M.summary.md` alongside the body: statement counts
by category, table/sequence/index created-dropped-modified lists,
dropped-object visibility, data-op source list, topology signature, and
the override block in effect. Verification proves the *bytes* match; the
summary lets reviewers confirm *intent* matches the source range's commit
log (a canonicalizer regression affecting A and B identically passes
byte verification but is wrong by intent).

A **generation determinism gate** runs in per-provider CI: run squash
codegen twice in fresh containers and assert byte-equal body, summary,
and topology signature. Canonicalization must eliminate wall-clock
timestamps, GUIDs, container UUIDs/ports, and nondeterministic dictionary
ordering. `ITopologySignature` artifacts carry a `signature-schema-version`;
adding a topology axis requires a new ADR documenting back-compat
defaults for prior versions, and `--allow-topology-skew` is the explicit,
never-silent opt-out. The verification container is always torn down
(`try/finally`, Ctrl-C safe); a failed run retains it only with
`--keep-failed-container` and always writes canonicalized B/B' to
`./squash-debug/<timestamp>/` for offline diff. Determinism-gate
failures gate release.

## Consequences

**Positive**
- The source tree stays small over time; thousands of migrations compact
  to one and provisioning reflects current state, not history.
- Codegen automates the painful translation; per-original ledger history
  is preserved indefinitely for forensics.
- The generation-time gate enforces the fleet discipline that manual
  squash workflows assume but never check.
- The `Replaces` graph still earns its keep for mature-env auto-mark, and
  composes for re-squashing.

**Negative**
- Operators must maintain a fleet manifest (or take an audited bypass).
  For one- or two-environment projects this is overhead; for many it is
  the minimum operational competence.
- Mid-range environments are a hard, loud error with a supervised
  recovery path, not an automatic catch-up. This is the deliberate trade
  for source-tree compaction.
- v1 carries per-provider snapshot/diff infrastructure cost across all
  five providers (ephemeral containers, snapshot capture, deterministic
  diff). Docker socket access is required to generate squashes.
- Structured override fields raise friction for projects without an issue
  tracker (mitigated by a stub-resolving regex default).

**Neutral**
- Squash is not destruction of history: ledger rows persist forever, git
  retains the original files, and the CLI audit trail records every
  stranding override. The migrations folder simply stops being the
  storage medium for history.

## Alternatives Considered

- **Additive squash, originals retained.** Automatic partial catch-up but
  no source-tree compaction. Rejected after operator-goal clarification.
- **Hybrid additive + `--prune` archive.** Adds two-phase deprecation
  discipline operators rarely follow; worse than either pure model.
- **Replay-recorder (capture every cluster mutation during apply).**
  Per-provider mutation capture is fragile with secret-leak risk.
- **Manual hand-authored only.** Forces hand-translation of hundreds of
  statements; defeats the codegen value proposition.

## References

- [Django squashmigrations](https://docs.djangoproject.com/en/5.1/topics/migrations/#squashing-migrations)
- [Flyway baselines and consolidations](https://www.red-gate.com/hub/product-learning/flyway/flyway-baselines-and-consolidations)
- [Atlas migrate diff](https://atlasgo.io/versioned/diff)
- [dotnet/efcore #2174](https://github.com/dotnet/efcore/issues/2174)
