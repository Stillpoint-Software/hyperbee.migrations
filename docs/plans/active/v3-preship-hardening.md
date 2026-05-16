# v3.0 Pre-Ship Hardening

**Status:** Active
**Created:** 2026-05-16
**Branch:** `devs/bfarmer/provider-squash` (current; no new branch — this is the
release-blocking tail of the in-flight v3.0 work)
**Source:** Five-agent pre-ship audit (bootstrapper consistency, runner/squash
pattern consistency, DRY/SOLID, documentation, dead code) run 2026-05-16.

---

## Process

ITRV discipline (`/nop:implement`): Implement -> Test -> Reflect -> Verify.
Every code task ends with the full local validation gate green before commit.
Documentation tasks end with the doc cross-checked against shipped code/ADRs.
Each phase ends with a clean checkpoint: rebase, push, full CI matrix green,
plan checkboxes + Status Summary updated.

Per-commit and per-phase commits require user approval. No `Co-Authored-By`
footers (global instruction). No `global::` prefix (locked feedback).

---

## Objective

Close every defect surfaced by the pre-ship audit so v3.0 ships production-grade
with no operator-facing inaccuracies, no readiness races, no dead code, and no
accidental pattern drift — without re-opening the completed
`SquashCli -> Squash` rename or the all-5-provider squash work.

**Success criteria:**
- Zero factually-wrong operator-facing documentation (CLI flags, apply path,
  exception model all match shipped code + ADR-0019/ADR-0024).
- `docs/site/` is ASCII-only (Jekyll just-the-docs constraint holds).
- Top-level `README.md` advertises v3.0 squash + CLI + `.Squash` packages.
- Aerospike readiness has an explicit gate on the lock-disabled path (no
  silent first-op race).
- The GSI rebalance-retry policy has a single source of truth.
- Definitely-dead code removed; the two confirm-intent items resolved by an
  explicit recorded decision (ADR), not a silent delete.
- Accidental drift (Serilog `Override` keys, Couchbase runner DI naming, stale
  test namespace) corrected.
- Full CI matrix 23/23 + 413 unit + 884 squash-unit tests green at every phase
  boundary.

**Constraints (cross-cutting prerequisites for every task):**
- `docs/site/**` must be ASCII-only.
- No `global::` prefix.
- All-5-providers-or-nothing release rule still holds.
- The `SquashCli -> Squash` rename is complete and verified — do NOT redo or
  reopen it.
- Backward compatibility: existing v2 migrations keep working; these are
  hardening changes, not behavior changes (except the Aerospike readiness
  gate, which only adds a wait — no semantic change to a healthy cluster).
- ADRs 0001-0024 remain in force.

**Tech stack:** .NET 8/9/10 multi-target; MSTest; Testcontainers; GitHub
Actions per-provider matrix (23 jobs). Validation commands in Style Reference.

---

## Style Reference

(Audit already performed by the five-agent pre-ship sweep — this is the Style
Reference for this plan.)

- **Validation gate (local):**
  - Build: `dotnet build Hyperbee.Migrations.slnx --nologo`
  - Unit: `dotnet test tests/Hyperbee.Migrations.Tests -f net10.0` (expect 413)
  - Squash unit: `dotnet test tests/Hyperbee.Migrations.Squash.Tests -f net10.0`
    (expect 884)
  - Couchbase integration (when Couchbase code touched):
    `dotnet test tests/Hyperbee.Migrations.Integration.Tests -f net10.0`
    `-p:EnableIntegrationTests=true --filter "FullyQualifiedName~CouchbaseSquash|FullyQualifiedName~CouchbaseRunnerTest"`
  - CI: `gh workflow run "Run Tests"` then watch — expect 23/23.
- **Retry-helper pattern reference:** the existing `BuildIndexesAsync`
  message-filtered `InternalServerFailureException` retry in
  `CouchbaseResourceRunner` is the canonical shape; the consolidated helper
  must preserve `maxAttempts`/delay as the single source of truth.
- **Bootstrapper pattern reference:** `CouchbaseBootstrapper` /
  `OpenSearchBootstrapper` are the reference shape for any readiness gate.
  The Aerospike gate reuses the existing `IsTransientClusterError` predicate
  in `AerospikeRecordStore` — do not invent a new transient classifier.
- **CI flake awareness:** Couchbase integration jobs flake on Docker registry
  pulls and rare >3-min rebalance windows; a single red Couchbase job that is
  the known rebalance/registry flake (not a new failure mode) is retried, not
  treated as a regression. New failure modes are regressions.
- **Anti-patterns to avoid:** non-ASCII in `docs/site`; `global::`; silent
  deletes of confirm-intent code; reopening the SquashCli rename.

---

## Git Workflow

Continue on `devs/bfarmer/provider-squash`. Snapshot tags per phase:

| Tag | When |
|-----|------|
| `v3-preship-phase-0` | Phase 0 complete (baseline green + decisions recorded) |
| `v3-preship-phase-1` | Doc ship-blockers complete |
| `v3-preship-phase-2` | README + Aerospike readiness gate complete |
| `v3-preship-phase-3` | DRY consolidation + dead-code removal complete |
| `v3-preship-phase-4` | Gated-decision execution + drift cleanup complete |
| `v3-preship-phase-5` | Release prep complete — ready to merge to main |

---

## Phase 0 — Baseline + Gated Decisions

**Goal:** Confirm a green baseline and resolve the two confirm-intent
decisions so downstream phases are unblocked. No production code changes.

**Prerequisites:** Audit complete (done). On `devs/bfarmer/provider-squash`.

**Completion criteria:** Baseline CI green recorded; ADR(s) written for the two
gated decisions; plan committed; INDEX.md updated.

**Testing strategy:** Re-confirm last green CI run id; no new tests.

### Task 0.1 — Baseline confirmation — **Done**
- Baseline: CI run `25953176574` = 23/23 on commit `c221fd4`; local 413 unit +
  884 squash-unit. Working tree clean except this plan + INDEX.
- **Completion:** baseline recorded in Learnings Ledger.

### Task 0.2 — GATED DECISION: `NullSquashStrategy` — **Done**
- **Decision: RETAIN as a public extension point** (user-chosen 2026-05-16).
- Recorded in [ADR-0025](../../decisions/0025-nullsquashstrategy-retained-as-extension-point.md).
- Phase 4 Task 4.1 executes documentation-only corrections (no deletion):
  scrub stale `ISquashStrategy.cs:12-14` remark; add explicit
  "extension point, no first-party use" notes; reconcile
  `SquashGenerationResult.cs:9` + `SquashStrategyDescriptor.cs:19` crefs.
  Contract test stays.
- **Completion:** ADR-0025 written + indexed; decision unambiguous.

### Task 0.3 — GATED DECISION: `SquashFleetGate.EnsureDeployable` — **Done**
- **Decision: CUT** (user-chosen 2026-05-16; rationale re-substantiated
  after a runtime trace of the apply path).
- Recorded in [ADR-0026](../../decisions/0026-deploy-time-fleet-gate-cut.md)
  (supersedes ADR-0019 A2's deploy-time half; generation-time
  `EnsureGenerable` untouched).
- Key finding that substantiates the cut: the silent-stranding failure A2
  targeted is **already** converted to a loud, recoverable apply-time
  refusal by the wired `MigrationRunner` `MidRangeSquashException`
  reconciliation path + `recover from-mid-range` verb + ADR-0021
  Kind/Replaces integrity. `EnsureDeployable` is redundant, never-wired
  defense-in-depth; cutting it removes a misleading unwired net, not
  protection. Industry-consistent (no mainstream tool ships a deploy-time
  staleness gate).
- Phase 4 Task 4.2 executes the delete + doc-surface correction;
  Phase 1 adds a short operator-doc note (fleet responsibility +
  `MidRangeSquashException` -> `recover` path).
- **Completion:** ADR-0026 written + indexed; decision unambiguous.

> **Decisions:** Task 0.2 -> [ADR-0025](../../decisions/0025-nullsquashstrategy-retained-as-extension-point.md)
> (retain, doc-only fixes). Task 0.3 -> [ADR-0026](../../decisions/0026-deploy-time-fleet-gate-cut.md)
> (cut, amends ADR-0019). Both resolved; Phase 4 executes them.

---

## Phase 1 — Documentation Ship-Blockers

**Goal:** Every operator-facing doc matches shipped code + ADRs. Docs-only —
independently shippable, zero code risk.

**Prerequisites:** Phase 0 complete.

**Completion criteria:** Each corrected claim verified against the actual
source (`SquashVerb`/`RecoverVerb` flag contracts, ADR-0019 A2, ADR-0024,
`AerospikeRecordStore`); `docs/site/**` ASCII-only; Jekyll site builds.

**Testing strategy:** Cross-reference each edited line against the cited
source file:line. If a local Jekyll build is available, build it; otherwise
ASCII-scan `docs/site/**` programmatically.

### Task 1.1 — Fix `docs/site/squashing-migrations.md` factual errors
- Replace the removed `ApplyToDataSourceAsync` apply-path description with the
  `IMigrationHost`-discovery apply path (per ADR-0024).
- Fix the CLI invocation example: `--scan-source <path>` (or
  `--no-scan="<reason>"`) and `--fleet-manifest` required-by-default (or
  `--no-fleet-manifest="<reason>"`) per ADR-0019 A2 — verify against
  `SquashVerb` help output.
- Correct `--remove-originals` semantics: dry-run by default; requires
  `--migrations-root`; `--confirm-delete` to actually delete.
- Correct the `recover from-mid-range` flag list against `RecoverVerb`
  required args (drop nonexistent `--owner`; add `--squash-version`,
  `--missing-versions`, `--connection`, `--assembly`).
- Add the generation-time (`MidRangeFleetException`) vs deploy-time
  (`MidRangeSquashException`) signpost.
- **Per ADR-0026:** add a short operator note — in v3.0 there is no
  deploy-time fleet gate; the fleet manifest is authoritative for
  generation-time safety, and a mid-range environment hitting a squash is
  refused loudly at apply time via `MidRangeSquashException` with the
  `recover from-mid-range` path. State the operator's fleet responsibility
  explicitly.
- **Test:** every changed flag/claim diff-checked against the cited source
  file:line; ASCII-only preserved.

### Task 1.2 — Reconcile CHANGELOG Aerospike contradiction
- Determine ground truth from `AerospikeRecordStore` `IntersectWithSquashedAsync`
  (override shipped per R-15).
- Fix the stale CHANGELOG "Operational notes" section to agree with the
  "Changed" section.
- Fix the matching now-incorrect "Transitivity caveat" claim in
  `squashing-migrations.md`.
- **Test:** CHANGELOG self-consistent; both squash docs agree with the code.

### Task 1.3 — Remove non-ASCII from `docs/site/supported-versions.md`
- Replace em-dashes (4 lines) with ` -- ` or `-`.
- **Test:** programmatic ASCII scan of all `docs/site/**` returns clean.

---

## Phase 2 — Release Quality: README + Aerospike Readiness Gate

**Goal:** Repo front-door advertises v3.0; the one real readiness race is
closed.

**Prerequisites:** Phase 0 complete. (Independent of Phase 1.)

**Completion criteria:** README has a correct v3.0 section; Aerospike
readiness gate implemented + tested; full validation gate green; CI 23/23.

**Testing strategy:** README cross-checked against actual package IDs +
public API. Aerospike gate: new unit/integration test proving
`InitializeAsync` waits through a transient-cluster-error window and does not
regress the healthy-cluster path.

### Task 2.1 — Top-level `README.md` v3.0 section
- Add "What's new in v3.0": migration squashing, the `hyperbee-migrations`
  CLI, the five `.Squash` provider packages + `ISquashProvider`,
  `IMigrationHost`, link to `docs/guides/upgrading-from-v2.md`.
- Cross-check every package id / interface name against `src/`.
- **Test:** every name in the new section grep-verified to exist in code;
  ASCII (top-level README is not Jekyll-constrained but stay consistent).

### Task 2.2 — Aerospike readiness gate (RISKIEST TASK)
- **This is the riskiest task in the plan.** It changes
  `AerospikeRecordStore.InitializeAsync` — a hot path for every Aerospike
  migration run. A wrong gate could hang startup or mask a real connection
  failure.
- Implementation strategy: mirror the bootstrapper shape (Couchbase/OpenSearch
  reference). Reuse the EXISTING `IsTransientClusterError` predicate already
  in `AerospikeRecordStore` (do not write a new classifier). Add a bounded
  readiness wait (poll a cheap server signal until non-transient or timeout)
  invoked from `InitializeAsync`, covering the lock-disabled path. The wait
  must be bounded by the existing cluster-ready timeout option and must
  surface a clear failure (not hang) on genuine unreachability.
- Subtasks:
  - [ ] Add the readiness wait method (bounded; reuses
        `IsTransientClusterError`); call it from `InitializeAsync` before the
        first ledger op.
  - [ ] Unit/integration test: simulated/real transient window is absorbed;
        healthy cluster path unchanged (no added latency beyond one cheap
        probe); genuine unreachable fails fast with a clear error, no hang.
  - [ ] Run full validation gate; Aerospike integration jobs green.
- **Test:** the above; plus the existing Aerospike integration suite stays
  green (no regression to the lock-enabled path).
- **Completion:** gate implemented, tested, CI Aerospike jobs green, no
  healthy-path latency regression.

---

## Phase 3 — DRY Consolidation + Dead-Code Removal

**Goal:** One source of truth for the rebalance-retry policy; definitely-dead
code gone.

**Prerequisites:** Phase 0 complete (Task 0.2 ADR informs whether
`NullSquashStrategy` deletion is in scope here or deferred). Independent of
Phases 1-2.

**Completion criteria:** Single rebalance-retry helper; the three call sites
delegate to it; definitely-dead members removed; full validation gate green;
CI 23/23.

**Testing strategy:** Existing Couchbase integration tests must still pass
(they exercise the retry path under real rebalance). Build must be clean with
no unused-symbol warnings for the removed members.

### Task 3.1 — Consolidate GSI rebalance-retry
- Single internal helper in the Couchbase provider (single source of truth for
  `maxAttempts` + delay + the `"rebalance in progress"` message filter).
- `CouchbaseRecordStore.CreateIndexWithRebalanceRetryAsync` and the inlined
  loop in `CouchbaseResourceRunner.CreateIndexAsync` both delegate to it.
- The integration-test `CouchbaseIndexRetry` references the production helper
  (add `InternalsVisibleTo` if required) instead of re-declaring the policy.
- **Test:** Couchbase squash + runner integration tests green (retry path
  still works under real rebalance); net change is a reduction with no
  behavior change.

### Task 3.2 — Remove definitely-dead code (HIGH-confidence only)
- Remove `ICouchbaseRestApiService.GetNodeStatusesAsync` + impl +
  `RestApi.GetNodeStatuses()`.
- Remove `GetClusterInfoAsync` + impl. **Keep** `RestApi.GetClusterInfo()`
  (still used by `ManagementReadyAsync`).
- Remove the no-timeout `WaitUntilBucketReadyAsync` overload (no caller; both
  call sites pass an explicit timeout). Keep the used
  `WaitUntilBucketHealthyAsync`/`WaitUntilClusterHealthyAsync` no-timeout
  siblings.
- **Test:** clean build all TFMs; full validation gate green; CI 23/23.

> Scope guard: ONLY the three HIGH-confidence removals here. `NullSquashStrategy`
> and `EnsureDeployable` are executed in Phase 4 per their Phase 0 ADRs — not
> here.

---

## Phase 4 — Gated-Decision Execution + Drift Cleanup

**Goal:** Execute the two recorded decisions; correct accidental drift.

**Prerequisites:** Phase 0 ADRs (Task 0.2, 0.3) written and unambiguous.
Phase 3 complete.

**Completion criteria:** Both ADR decisions executed exactly as recorded;
drift items fixed; full validation gate green; CI 23/23.

**Testing strategy:** If deletes — clean build + full suite. If retain — the
corrected docs match the kept code. Drift fixes verified by config/grep.

### Task 4.1 — Execute the `NullSquashStrategy` decision (per Task 0.2 ADR)
- If ADR = delete: remove the type + its contract test + fix the stale remark
  in `ISquashStrategy`.
- If ADR = retain: fix only the stale remark; add the "public extension point"
  note the ADR specifies.
- **Test:** full suite green; the `ISquashStrategy` doc now matches reality.

### Task 4.2 — Execute the `EnsureDeployable` decision (per Task 0.3 ADR)
- If ADR = cut: remove `EnsureDeployable` + `StaleFleetMemberException` +
  `UnregisteredEnvironmentException` + their tests; correct
  `SquashFleetGate`/`SquashRequest` docs that reference the failure modes.
- If ADR = roadmap: keep code; correct the class doc to "not yet wired";
  ensure the follow-up is filed where the project tracks follow-ups.
- **Test:** full suite green; docs match the code's actual wired state.

### Task 4.3 — Accidental drift cleanup
- Subtasks:
  - [ ] MongoDB & Postgres `appsettings.json`: Serilog `Override` key
        `"Couchbase"` -> `"MongoDB"` / `"Npgsql"`.
  - [ ] Couchbase runner DI extension naming -> `Add<P>Provider` /
        `Add<P>Migrations` convention (match the other 4); update the runner
        `Program.cs` call sites.
  - [ ] Stale test namespace `Hyperbee.Migrations.Tests.Squash.Cli` ->
        `Hyperbee.Migrations.Tests.Squash`.
- **Test:** runners start with corrected log filters; full unit + CI green
  (DI rename is internal — no public API change).

---

## Phase 5 — Release Prep

**Goal:** Final verification; branch ready to merge to main.

**Prerequisites:** Phases 1-4 complete.

**Completion criteria:** Full CI matrix 23/23 on a clean run; CHANGELOG
finalized for the hardening pass; plan archived; INDEX updated.

**Testing strategy:** One clean end-to-end CI run with no retried jobs
(or only the known-flake Couchbase job retried once and then green).

### Task 5.1 — Final reconciliation + CI
- Re-run the documentation cross-check (no remaining stale CLI/apply-path
  claims; ASCII clean) and a final dead-code spot check on touched files.
- CHANGELOG: add the hardening entries (doc fixes, Aerospike gate, retry
  consolidation, dead-code removal, the two recorded decisions).
- Full CI matrix green.
- **Completion:** 23/23 green; CHANGELOG accurate; ready to merge.

### Task 5.2 — Plan close-out
- Move this plan to `docs/plans/archive/2026-05-<slug>.md`; update
  `docs/plans/active/INDEX.md`; final memory update with release status.

---

## Learnings Ledger

| Date | Type | Learning |
|------|------|----------|
| 2026-05-16 | process | Plan created from the five-agent pre-ship audit. Baseline before hardening: CI 23/23 green on the retriggered run; 413 unit + 884 squash-unit local. Riskiest task: 2.2 (Aerospike `InitializeAsync` readiness gate — hot path). Two gated decisions (0.2 NullSquashStrategy, 0.3 EnsureDeployable) must be ADR-recorded before their Phase 3/4 deletes. |
| 2026-05-16 | decision | Phase 0 gated decisions resolved by user. **NullSquashStrategy: RETAIN** as public extension point (ADR-0025) — Phase 4.1 is doc-only, no delete. **EnsureDeployable: CUT** (ADR-0026). Baseline confirmed: CI 25953176574 = 23/23 on c221fd4. |
| 2026-05-16 | decision | ADR-0026 rationale re-substantiated after challenge + runtime trace. The P0 (ADR-0019 A2) silent-stranding concern is **already** addressed by the WIRED `MigrationRunner` `MidRangeSquashException` reconciliation path + `recover from-mid-range` + ADR-0021 integrity — `EnsureDeployable` is redundant unwired defense-in-depth, never connected. Cutting removes a misleading net, not protection. Industry survey: no mainstream migration tool ships a deploy-time fleet-staleness gate (all use recoverability-from-history + operator discipline). Lesson: verify whether a P0's *outcome* is already met by a different wired mechanism before treating its unwired implementation as load-bearing. |
| 2026-05-16 | style | Phase 1: doc claims must be diff-checked against source contracts, not prior docs. Ground truth pulled from `SquashVerb`/`RecoverVerb` (flag contracts), `AerospikeRecordStore:309-361` (IntersectWithSquashedAsync IS fully implemented — the CHANGELOG "follow-up" bullet was the stale side of the contradiction; "Changed/R-15" was correct). `docs/site` ASCII enforced via a glob sweep, now part of the Phase-1 done-gate. |

---

## Status Summary

| Phase | Status |
|-------|--------|
| 0 — Baseline + Gated Decisions | **Done** (2026-05-16) — baseline confirmed; ADR-0025 (retain NullSquashStrategy) + ADR-0026 (cut EnsureDeployable) written; committed |
| 1 — Documentation Ship-Blockers | **Done** (2026-05-16) — squashing-migrations.md factual fixes + ADR-0026 two-refusal-points model; CHANGELOG Aerospike contradiction resolved; docs/site ASCII-clean |
| 2 — README + Aerospike Readiness Gate | Not Started |
| 3 — DRY Consolidation + Dead-Code Removal | Not Started |
| 4 — Gated-Decision Execution + Drift Cleanup | Not Started |
| 5 — Release Prep | Not Started |

**Current task:** Phase 1 complete — awaiting user check-in before Phase 2.
**Next action:** user approves Phase 1 commit; then `/nop:implement` Phase 2
(README v3.0 section + Aerospike readiness gate — riskiest task).
**Blockers:** none.

**Riskiest task:** Task 2.2 — Aerospike readiness gate (changes
`AerospikeRecordStore.InitializeAsync`, the per-run hot path). A wrong gate
could hang startup or mask a genuine connection failure. Mitigation: reuse
the existing `IsTransientClusterError` predicate, bound the wait by the
existing cluster-ready timeout, and add a test that proves the
healthy-cluster path gains no latency and a genuinely-unreachable cluster
fails fast (no hang).
