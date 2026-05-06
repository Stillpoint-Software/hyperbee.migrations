# Assessment: Migration Squashing — Destructive Model + Implementation Examples

**Date:** 2026-05-05
**Status:** Final
**Author:** Brenton Farmer (with assessment sub-agents)
**Subject:** [docs/design/migration-squashing-consensus-destructive.md](../design/migration-squashing-consensus-destructive.md) (Status: Ratified) + 5 per-provider implementation examples (~340KB of concrete code) + ADRs 0019 / 0020 / 0021
**Pipeline:** /nop:assess — Full mode (Triage → PM/MD/PA Discovery → Synthesis-skipped → Red-Blue₁ → Independent Review → Red-Blue₂ → Consolidation)
**Predecessor:** [Assessment 0006](0006-migration-squashing-assessment.md) — assessed the additive model; superseded by the destructive reframe

## Executive Summary

The destructive-model squash design and its 5 implementation examples are sound architecturally — every Critical and High finding maps to a specific addressable amendment, no verdict reaches "Delete the entire feature," and the consensus's 11 universal items (C1–C11) survive intact. But the assessment surfaces **17 Redesigns + 3 Deletes** that are non-trivial: the design as ratified ships several escape-hatch flags whose blast radius is mismatched with their friction surface (`--skip-verify`, `--accept-stranding=name1,name2`, `--force-squash-from-mid-range`), and four genuine specification gaps (re-squash transitivity, generation idempotency, `Kind`/`Replaces` consistency, container-leak-on-failure) that the Independent Review caught but in-session Red-Blue₁ missed.

**Most consequential changes:**

1. **Delete `--skip-verify` outright.** Open issue #1's own conclusion ("never for production-bound") becomes the contract. Address verification cost via PA-1/PA-2 redesign (parallel A+B capture, snapshot A caching).
2. **v1 ships Postgres only.** OpenSearch and Couchbase High-canonicalization-risk providers ship in v1.2 after the canonicalizer pipeline matures empirically against Postgres production corpora. Per IR-CP-4.
3. **Mandatory `[DataMigration]` annotation** when classifier heuristic detects suspected DML (CP-2). Converts silent false-negatives (data loss) into loud false-positives (annotation friction).
4. **Add C12 (generation determinism), C13 (container lifecycle), and `Kind`/`Replaces` consistency rule** to the consensus document. All three are gaps the in-session Red-Blue₁ pass missed.
5. **Drop the rename-detection refusal** (CP-3); make it opt-in warn only. Edit-distance heuristics produce too many false positives to be a hard gate; the verification round + mandatory `[DataMigration]` carry the data-loss-prevention weight.

**Net findings:** 34 total (30 from PM/MD/PA Discovery + 4 from Independent Review). **3 Delete / 18 Redesign / 5 Monitor / 2 Defer / 2 Keep.** P0 / P1 / P2 priorities at the end.

The Independent Review earned its place this round more than usual: 4 of 5 contested points flipped Red-Blue₁'s verdicts in Red-Blue₂; all 4 new findings were confirmed real and all received Redesign verdicts. The IR also caught a sequencing problem (Postgres-only-v1) the in-session pass had absorbed without flagging.

---

## Phase 0 — Triage

**Artifact set:** Architecture + Implementation-grounded. The consensus design (~330 lines) is conceptual; the 5 per-provider examples (~340KB of concrete C# + sample I/O) ground it.

| Skill | Value | Rationale |
|-------|-------|-----------|
| Pre-mortem | High | 18-month horizon explicit; multiple decay vectors (override proliferation, fleet manifest staleness, classifier accumulation, version-pinning across pg_dump/Mongo/OpenSearch upgrades) |
| Mechanism Design | High | 23 interface points: `[Migration(Replaces=…)]`, `ISquashStrategy` plugin, `IDataOpClassifier`, `--squash-overrides`, `--accept-stranding`, `MidRangeSquashException`, squash CLI |
| Performance Audit | High | Explicit cost dimensions per provider; verification round multiplied 3x by container pipeline; canonicalization throughput; classifier scan time on large code bases |

**Selected:** PM, MD, PA (all High). **Skipped:** none.

---

## Phase 1 — Discovery (parallel, independent)

30 findings: 9 Pre-mortem (PM-1 through PM-9), 12 Mechanism Design (MD-1 through MD-12), 9 Performance Audit (PA-1 through PA-9). Severity distribution: 7 Critical, 13 High, 9 Medium, 1 Low.

### Strong convergence (different analytical frames, different reasoning paths)

- **Verification round economics drive escape-flag adoption:** PM-1 (slow-burn cultural drift to `--skip-verify`) + PA-2 (10 iterations × 204s = 34min for OS-3node) + MD-3+MD-10 (mid-range force-flag friction). Three frames arrive at "verification cost is the load-bearing safety hazard."
- **Fleet manifest fragility:** PM-2 (manifest staleness over 18 months) + MD-2 (single source of truth fails open at consumer-design level). Same hazard from temporal and consumer lenses.
- **Classifier silent-loss:** PM-4 (classifier LOC accumulates patches over 18 months) + MD-4 (heuristic depends on receiver-name match) + PA-4 (Roslyn cost dominates large ranges). Three lenses on the same surface.
- **Mid-range recovery hell:** PM-2 (operator hits `MidRangeSquashException` after manifest staleness) + MD-3 (02:00 prod operator inherits unmakeable decision) + MD-10 (`--force-squash-from-mid-range` friction = `--verbose`). Three findings on one hazard chain.
- **Override sediment:** PM-7 (junk drawer over 18 months) + MD-7 (cargo-cultable to invisible failure). Same finding from temporal and consumer-design lenses.

### Weak convergence (shared surface, downgrade confidence)

- PA-3, PA-7 cluster: snapshot canonicalization memory pressure. Surface observation in pg_dump and JsonNode patterns; treat as one Performance concern.

---

## Phase 2 — Synthesis (skipped with justification)

Eight consumers enumerated across the IR/MD analysis (squash author, squash CLI operator, fresh-env operator, mature-env auto-mark operator, mid-range-exception operator, force-flag user, fleet manifest maintainer, new-provider implementer, post-hoc auditor). Conflicting non-negotiables check: zero pairs. The destructive model has a single primary persona (the squash operator) with secondary roles (PR reviewer, prod operator at deploy time) who inherit decisions but don't have conflicting priorities — they have **inherited conflicts** which is a different problem (covered by MD-3/MD-10 Mechanism Design findings, not Synthesis).

The earlier multi-advocate Round 1+1b already exhausted stakeholder-perspective analysis; Synthesis would add no signal. Skipped.

---

## Phase 3 — Red-Blue₁ Convergence

### Subtraction Scan (Round 0) flagged for delete consideration

- `--skip-verify` (matches PM-1)
- `SquashGenerationResult.Unsupported("hand-author")` (matches MD-8)
- pg_dump-version-pinned image bundling (matches PM-3 — Redesigned, not Deleted)

### Outcome: 4 contested points after 3 rounds

Verdict distribution from Red-Blue₁:
- Delete: 3 (PM-1 `--skip-verify`, MD-8 `Unsupported`, MD-9 subsumed by PM-3)
- Redesign: 14
- Defer: 2
- Monitor: 5
- Keep: 5

**Contested for IR:** C-1 (`--force-squash-from-mid-range` existence), C-2 (override expiry default of 90 days), C-3 (rename-detection edit-distance threshold), C-4 (v1 provider scope: Postgres-only vs multiple).

---

## Phase 3.5 — Independent Review

Fresh sub-agent received only the artifact files + the consolidated findings table — no Red-Blue rationale, no discovery-finding details.

**Outcome:**
- 18 verdicts: agree
- 5 verdicts: substantive disagreement — MD-3/10 (token theater), MD-4 (Monitor → Redesign), MD-5 (rename detection should be opt-in warn), MD-6 (Keep → Redesign with diff summary), MD-7/PM-7 (char counts are theater)
- 4 new findings the assessment missed: IR-N1 (re-squash transitivity), IR-N2 (generation idempotency), IR-N3 (`Kind`/`Replaces` consistency), IR-N4 (container leak on verification failure)
- 4 contested-point positions: IR-CP-1 (delete force flag), IR-CP-2 (30-day expiry not 90), IR-CP-3 (rename detection opt-in warn only), IR-CP-4 (Postgres-only v1)

The IR's substantive disagreements held up under Red-Blue₂ scrutiny in 4 of 5 cases (Red wins on CP-2, CP-3, CP-4, CP-5). The remaining 1 resolved to Synthesis (CP-1). All 4 new findings confirmed real and assigned Redesign verdicts.

---

## Phase 3.75 — Red-Blue₂ Resolution

| ID | Title | Resolution | Effect |
|----|-------|------------|--------|
| **CP-1** | Mid-range force-flag mechanism | **Synthesis** | Keep token gate but make it deterministic per `(env-name, squash-version, missing-versions-set)` so runbooks remain reproducible. Pair with structured `--reason` per CP-5. |
| **CP-2** | `[DataMigration]` heuristic verdict | **Red wins** | Verdict changes from Monitor → **Redesign**. Mandatory annotation: heuristic flags suspected DML, classifier *refuses* unless author has annotated with `[DataMigration]` or `[StructuralOnly]`. Converts silent false-negatives to loud false-positives. |
| **CP-3** | Field rename detection | **Red wins** | Default off; opt-in via `enable-rename-heuristic: true` per range; even when enabled, *warn only* (never refuse). Edit-distance ≤3 produces too many false positives (`name`→`age` is distance 3). Verification + mandatory `[DataMigration]` carry data-loss-prevention weight. |
| **CP-4** | Squash artifact PR review | **Red wins** | Verdict changes from Keep → **Redesign**. Add third artifact `Squash_M.summary.md` (statement count by category, table list, dropped-objects list) embedded in PR body. C2 verification proves bytes match; doesn't prove intent. |
| **CP-5** | Override `reason:` field shape | **Red wins** | Replace `≥20 chars` requirement with structured fields: `ticket-id` (regex-validated), `owner` (git-author-validated), `reason` (free text). CI lint enforces ticket-id resolves over HTTP if `tracker-url` configured. |
| **IR-N1** | Re-squash transitivity not specified | **Redesign** | `Replaces` recorded as authored. Runner walks transitively: a row with `Kind == Squash` and `version ∈ row.Replaces` satisfies the replacement obligation for any version in its replaced range. Pseudocode amended in ADR-0019. |
| **IR-N2** | Generation idempotency not addressed | **Redesign** | Add **C12: Generation determinism gate** to consensus. CI test: run `squash --range R` twice in fresh containers; assert byte-equal artifacts AND summary. Eliminates timestamps, GUIDs, container UUIDs, dictionary iteration order from canonicalization. |
| **IR-N3** | `Kind`/`Replaces` consistency not enforced | **Redesign** | Amend ADR-0019 reconciliation: row qualifies as "satisfying" only if `(Kind=Migration AND Id=version)` OR `(Kind=Squash AND version ∈ Replaces AND Replaces non-empty)`. Provider record stores enforce on write. |
| **IR-N4** | Verification ephemeral container leak | **Redesign** | Add **C13: Verification container lifecycle**. Success → tear down. Failure → tear down by default; retain only with `--keep-failed-container`; debug summary always written to `./squash-debug/<timestamp>/`. `try/finally` to handle Ctrl-C cleanly. |
| **IR-CP-1** | `--force-squash-from-mid-range` existence | **Synthesis** | Move out of `squash` subcommand into separate `dotnet hyperbee-migrations recover from-mid-range` subcommand. Token gate (CP-1) + structured reason (CP-5) + post-action mandatory data-integrity-verification. Backup-restore remains documented primary recovery; flag is "last resort, DBA-supervised, post-incident only." |
| **IR-CP-2** | Override expiry default | **Red wins** | Default `expires` = **30 days** (not 90); 90-day hard cap. CI warns at 7 days remaining; refuses past expiry. Forces sprint-cadence re-justification. |
| **IR-CP-3** | Rename detection threshold | (Covered in CP-3 above; Red wins) | n/a |
| **IR-CP-4** | v1 provider scope | **Red wins** | **v1 = Postgres only.** v1.1 (~3 months later) = Aerospike (Low risk) + MongoDB (Medium-High, with metrics from Postgres informing canonicalization spec). v1.2 = Couchbase + OpenSearch (both High canonicalization risk, benefit from cross-provider canonicalization-spec maturity). ADR-0019 amended. |

**Red-Blue₂ summary:** 4 Red wins / 1 Synthesis / 0 Blue wins on contested points. All 4 new findings → Redesign. The IR's analysis genuinely caught problems in-session Red-Blue₁ missed.

---

## Phase 4 — Consolidated Findings

### Convergence quality per finding

| Convergence | Findings | Confidence |
|---|---|---|
| **Strong** (different frames, different reasoning paths) | Verification economics (PM-1+PA-2+MD-3/10), fleet manifest fragility (PM-2+MD-2), classifier silent-loss (PM-4+MD-4+PA-4), mid-range recovery (PM-2+MD-3+MD-10), override sediment (PM-7+MD-7) | High |
| **Weak** (shared surface) | Snapshot canonicalization memory (PA-3, PA-7) | Medium — single Performance concern |
| **IR-only** (caught what assessment missed) | IR-N1 re-squash transitivity, IR-N2 generation idempotency, IR-N3 Kind/Replaces consistency, IR-N4 container leak | High — IR's value precisely in catching gaps |
| **Disagreement→Resolved** | 5 contested points went to Red-Blue₂; 4 Red wins, 1 Synthesis | High — adversarial pressure produced final positions |

### Final verdict table

| # | Finding | Verdict | Action |
|---|---------|---------|--------|
| **PM-1** | `--skip-verify` normalization risk | **Delete** | Remove `--skip-verify` from v1 CLI surface entirely. Address verification cost via PA-1/PA-2 redesign. Open issue #1's own conclusion shipped as contract. |
| **PM-2/MD-2** | Fleet manifest fail-open | **Redesign** | Two-phase gate: fleet.yml is *input*; squash artifact records `expected-fleet-versions: {env: minVersion}` + `max-staleness-window`; runner refuses on env not recorded OR recorded env's actual version is below recorded minimum AND hasn't moved within staleness window. |
| **MD-3/MD-10** | Mid-range force-flag mechanism | **Redesign** (CP-1 synthesis) | Move to separate `recover from-mid-range` subcommand (per IR-CP-1). Use deterministic token = `SHA-256(env-name ‖ squash-version ‖ missing-versions)[:12]`. `--accept-data-corruption-risk=<token>` + `--ticket-id=<>` + `--reason=<>=20 chars>`. Backup-restore remains documented primary path. |
| **PA-1/PA-2** | Verification cost compounds | **Redesign** | (a) Snapshot A cached by `hash(provider, residual-head-versions, canonicalizer-version, topology-signature, image-version)` — IR added topology+image to key. (b) A and B captured in parallel containers via `Task.WhenAll`. (c) Reuse Container A as Container C: keep residual-head state, apply squash, snapshot. Target: OS 3-node 204s → ~70s. |
| **PM-6** | Non-determinism in carry-forward | **Redesign** | Classifier scans for whitelist of approved deterministic call sites; denylist of common non-deterministic ones (`DateTime.Now/UtcNow`, `DateTimeOffset.Now/UtcNow`, `Guid.NewGuid()`, `Random` sans seed, `Environment.MachineName/UserName`, `Stopwatch.GetTimestamp()`, `Process.Id`, `Activity.Current?.TraceId`, `IPGlobalProperties.GetHostName()`, `Assembly.GetExecutingAssembly().Location`). Refuse unless `accept-non-deterministic-data-ops=true` with explicit override-record-list. |
| **MD-7/PM-7** | Override block junk-drawer | **Redesign** (CP-5 Red wins) | Replace `≥20 chars` with structured fields: `ticket-id` (regex-validated, default `^[A-Z]+-\d+$`), `owner` (git-author-validated against last-90-days commit log), `reason` (free text). CI lint validates `ticket-id` resolves if `tracker-url` configured. |
| **PM-3** | pg_dump version-pinned image rot | **Redesign** | Don't bundle N pg_dump versions. Spin server-version-matched ephemeral container; run `pg_dump` *inside* via `docker exec`. Document Docker-in-Docker prerequisite explicitly. |
| **MD-1** | `--accept-stranding` wildcard-equivalent | **Redesign** | Each named env requires `--reason-stranding=name=<>=20 chars>` arg; CLI refuses without one reason per name. Audit trail records reasons. |
| **MD-4** | `[DataMigration]` heuristic silent-drop | **Redesign** (CP-2 Red wins) | Mandatory annotation: classifier returns `RequiresAnnotation=true` whenever heuristic detects possible DML on a migration lacking `[DataMigration]`. Squash refuses with diagnostic. Author adds `[DataMigration]` (acknowledge carry-forward) or `[StructuralOnly]` (assert heuristic wrong, suppresses, logged). |
| **MD-5** | `@rename` annotation foot-gun | **Redesign** (CP-3 Red wins) | `enable-rename-heuristic: false` by default per range. When enabled, classifier emits *Warning* (never *Refusal*) naming suspect pairs. Override via `@rename(from, to)` annotation. Verification round + mandatory `[DataMigration]` carry data-loss-prevention weight. |
| **MD-6** | Squash artifact PR review unreviewable | **Redesign** (CP-4 Red wins) | Add third artifact `Squash_M.summary.md` containing statement count by category, table-list, sequence-list, dropped-object-list, data-ops-source-list. PR template requires this artifact pasted into description. C2 verification proves bytes match; summary proves intent. |
| **MD-8** | `Unsupported` "hand-author" unrealistic | **Delete** | Remove `Unsupported` from `SquashGenerationResult` variants. Provider without a strategy doesn't get squash CLI verb at all (clean refusal at registration time, not result variant promising viable manual path). |
| **MD-11** | Provider implementer 5-components-in-lockstep | **Redesign** | `ISquashStrategy` registration takes one composite descriptor with all 5 components; `NotImplementedException` from any fails registration validation, not silent runtime failure. |
| **MD-12** | `LoadAppliedVersionsAsync` realtime documented not enforced | **Defer** | Multi-node CI fixture per provider; runtime probing deferred to Phase 2. |
| **PA-3** | Snapshot canonicalization O(N²) memory | **Monitor** | Add memory metric to verifier; escalate if observed >1GB on real corpus. |
| **PA-4** | Classifier Roslyn cost at 500+ migrations | **Redesign** | Specify single-compilation-per-assembly in `ISquashStrategy` contract; per-migration semantic-model construction expressly forbidden. |
| **PA-5** | Async-build barrier 3x cost | **Keep** (contingent on PA-2) | Mitigation comes from PA-2 caching; barrier cost paid once per residual-head set. If PA-2 slips, escalate to Redesign. |
| **PA-6** | Fleet readiness sequential | **Redesign** | `Parallel.ForEachAsync` over env probes with bounded concurrency (8). Pre-validate manifest before any container spin. |
| **PA-7** | pg_dump post-processing memory | **Defer** | Streaming refactor non-trivial; current memory acceptable for typical Postgres dumps <100MB. Defer to first OOM report. |
| **PA-8/PA-9** | Artifact size + USL ceiling | **Monitor** | Add metrics; revisit if dev-team CI hosts hit OOM in practice. |
| **PM-4** | Classifier per-provider edge-case accumulation | **Monitor** | Add classifier LOC + cyclomatic-complexity metric to per-PR provider CI. Trip threshold at 1500 LOC. |
| **PM-5** | Forensic reconstruction post-canon-regression | **Keep** + amendment | C5 round-trip CI is prevention; canonicalizer-version pinning in artifact header. **IR-added:** retain old canonicalizer-versions as separate frozen packages so old artifacts remain runnable. |
| **PM-8** | Topology axis drift | **Redesign** | `ITopologySignature` artifacts carry `signature-schema-version`. Provider ships migration logic when adding axes (e.g., "old signatures lacking `replication_role` are treated as `primary` for back-compat"). Versioning is structural, not by-convention. |
| **PM-9** | Canonicalization false-positive fatigue | **Monitor** | Track verifier-refusal rate per provider; escalate if false-positive ratio >20% by month 6. |
| **MD-9** | Operator-machine vs CI pg_dump version drift | **Delete** (subsumed) | Resolved by PM-3 redesign — pg_dump runs inside container, never on operator machine. |
| **IR-N1** | Re-squash transitivity not specified | **Redesign** | `Replaces` recorded as authored. Reconciliation walks transitively: row qualifies if `(Kind=Migration AND Id=version)` OR `(Kind=Squash AND version ∈ row.Replaces)`. ADR-0019 pseudocode amended. |
| **IR-N2** | Generation idempotency not addressed | **Redesign** | Add **C12 Generation determinism gate** to consensus. CI test per provider: two squash generations produce byte-equal artifacts. Eliminate timestamps, GUIDs, container UUIDs, dictionary iteration order. |
| **IR-N3** | `Kind`/`Replaces` consistency not enforced | **Redesign** | Provider record stores enforce on write that `Kind=Squash ⟺ Replaces non-empty`; mismatch raises `MigrationLedgerIntegrityException`. Runner refuses to load a ledger with inconsistent rows. |
| **IR-N4** | Container leak on verification failure | **Redesign** | Add **C13 Verification container lifecycle** to consensus. Success → tear down. Failure → tear down by default; retain only with `--keep-failed-container`. Always emit debug summary to `./squash-debug/<timestamp>/`. `try/finally` for Ctrl-C cleanliness. |

### Distribution

| Verdict | Count | Items |
|---------|-------|-------|
| **Delete** | 3 | PM-1, MD-8, MD-9 |
| **Redesign** | 18 | PM-2/MD-2, MD-3/10, PA-1/2, PM-6, MD-7/PM-7, PM-3, MD-1, MD-4, MD-5, MD-6, MD-11, PA-4, PA-6, PM-8, IR-N1, IR-N2, IR-N3, IR-N4 |
| **Defer** | 2 | MD-12, PA-7 |
| **Monitor** | 5 | PA-3, PA-8, PA-9, PM-4, PM-9 |
| **Keep** | 2 | PA-5 (contingent), PM-5 (with amendment) |

### Ship-order amendment

Per IR-CP-4 (Red wins): v1 = Postgres only; v1.1 (~3 months later) = Aerospike + MongoDB; v1.2 = Couchbase + OpenSearch. Other providers ship `NullSquashStrategy` with `Unsupported(...)` returns until their phase. ADR-0019 amended; design doc Decision 2 reframed.

---

## Priority-Tagged Amendments

### P0 — Block ship of v1 until addressed

These are correctness/integrity/scope issues that cannot be deferred:

- **P0-1 (PM-1):** Delete `--skip-verify` from v1 CLI entirely. Remove from both squash generation and runtime.
- **P0-2 (PM-2/MD-2):** Two-phase fleet readiness gate. Squash artifact records `expected-fleet-versions` + `max-staleness-window`; runner enforces at deploy time.
- **P0-3 (MD-3/MD-10):** Move force-flag to separate `recover from-mid-range` subcommand; deterministic-but-stable token gate; mandatory ticket-id + reason.
- **P0-4 (PA-1/PA-2):** Snapshot A caching + parallel A/B capture. Without this, verification cost economics drive operator pressure for skip-verify and the entire integrity story collapses.
- **P0-5 (MD-4):** Mandatory `[DataMigration]` annotation. Classifier refuses on heuristic-detected DML without explicit author marker.
- **P0-6 (IR-N1):** Re-squash transitivity rule in ADR-0019 reconciliation pseudocode.
- **P0-7 (IR-N2):** C12 Generation determinism gate added to consensus + CI test per provider.
- **P0-8 (IR-N3):** `Kind`/`Replaces` consistency enforcement at write + `MigrationLedgerIntegrityException` on read.
- **P0-9 (IR-CP-4):** v1 ships **Postgres only**. ADR-0019 amended with sequencing.

### P1 — Material before plan/implement

- **P1-1 (PM-6):** Whitelist-approach non-determinism scan in classifier; expanded ban list (DateTime, Guid, Random, Environment.*, Stopwatch, Process.Id, etc.).
- **P1-2 (MD-7/PM-7):** Structured override fields (ticket-id + owner + reason); CI lint validates ticket-id resolves.
- **P1-3 (PM-3):** pg_dump runs inside server-version-matched ephemeral container via `docker exec`; document Docker-in-Docker prerequisite.
- **P1-4 (MD-1):** Per-env `--reason-stranding` arg; ≥20 chars; logged in audit trail.
- **P1-5 (MD-5):** Rename detection opt-in only; warn never refuse; verification + `[DataMigration]` carry data-loss prevention.
- **P1-6 (MD-6):** Third artifact `Squash_M.summary.md` (statement counts, table list, dropped-objects); PR template requires.
- **P1-7 (MD-11):** Composite-descriptor registration for `ISquashStrategy`; NotImplementedException fails registration validation.
- **P1-8 (PM-8):** `ITopologySignature` schema versioning + provider migration-forward logic.
- **P1-9 (IR-N4):** C13 Verification container lifecycle in consensus.
- **P1-10 (IR-CP-2):** Override expiry default = 30 days; 90-day hard cap; warn at 7 days.

### P2 — Quality

- **P2-1 (PA-4):** Single-compilation-per-assembly enforced in classifier contract.
- **P2-2 (PA-6):** Parallel fleet env probes via `Parallel.ForEachAsync(maxParallelism: 8)`.
- **P2-3 (PM-5 amendment):** Retain old canonicalizer-versions as frozen packages.
- **P2-4 (Monitor items):** Metrics for canonicalization memory, classifier LOC/cyclomatic complexity, verifier-refusal rate, artifact size, concurrent-squash count.
- **P2-5 (Defer items):** MD-12 multi-node CI fixture; PA-7 streaming refactor (revisit on first OOM report).

---

## Required Artifact Edits (deltas beyond original Round 1b consensus)

| Artifact | Required edits |
|----------|----------------|
| `docs/design/migration-squashing-consensus-destructive.md` | (a) Add C12 Generation determinism gate. (b) Add C13 Verification container lifecycle. (c) Reframe Decision 2: v1 = Postgres only; v1.1 = Aerospike+MongoDB; v1.2 = Couchbase+OpenSearch. (d) Override block schema: structured fields + 30-day default expiry. (e) Document `recover from-mid-range` subcommand separately. |
| `docs/decisions/0019-...` | (a) Re-squash transitivity rule in reconciliation pseudocode (per IR-N1). (b) `Kind`/`Replaces` consistency rule (per IR-N3). (c) Mandatory `[DataMigration]` annotation contract (per CP-2). (d) Rename detection opt-in (per CP-3). (e) Sequencing amendment (Postgres v1, others v1.1/v1.2). (f) `expected-fleet-versions` + `max-staleness-window` artifact header fields. (g) `--skip-verify` removed entirely. |
| `docs/decisions/0021-...` | (a) `Kind=Squash ⟺ Replaces non-empty` write-time enforcement. (b) `MigrationLedgerIntegrityException` on inconsistent rows. (c) Old canonicalizer-version retention requirement. |
| `IMigrationRecordStore` contract | New: `WriteAsync` enforces `Kind`/`Replaces` consistency. New: `LoadAppliedVersionsAsync` returns rows whose `Kind=Squash` and `version ∈ Replaces` as satisfying. |
| `IDataOpClassifier` | New: returns `RequiresAnnotation=true` (mandatory `[DataMigration]` per CP-2). New: non-determinism ban-list scan (per PM-6). New: single-compilation-per-assembly contract (per PA-4). |
| Squash CLI | Delete `--skip-verify`. Per-env `--reason-stranding=name=<>`. New: `recover from-mid-range` subcommand. New: `Squash_M.summary.md` artifact emission. |

---

## Recommended Next Steps

1. **Apply P0 + P1 amendments** to consensus + ADRs in a single revision pass. Re-cycle Status: Ratified → Ratified (revised). This is the **final hardening pass** before plan/implement.
2. **Skip Round 1c re-ratification** unless one of the P0 items is contested. The advocates have already endorsed the destructive model's structure; the changes are surgical and address gaps every advocate flagged in their Round 1a/1b "remaining concerns" sections.
3. **`/nop:plan`** for Phase 1 = Postgres-only v1. Decompose: attribute extension + `IDataOpClassifier` + `ITopologySignature` + Postgres `PgDumpSnapshotStrategy` + Postgres `ISquashVerifier` + framework-level reconciliation logic + CLI verb. Each slice independently shippable.
4. **Phase 2 (v1.1) trigger:** Postgres v1 ships and metrics from PM-9 (verifier-refusal rate), PM-4 (classifier LOC), PA-3 (canonicalization memory) all under thresholds for ≥1 release cycle.

The destructive model is materially safer with the P0 + P1 amendments than the additive model that preceded it. The center holds — every Critical/High finding is addressable; no architectural gut-rebuild required.

---

## Methodology Notes

- **Discovery independence verified.** Three sub-agents dispatched in parallel; each received only the artifact set + analytical frame.
- **Synthesis skipped with explicit consumer-conflict check.** No conflicting non-negotiables surfaced.
- **Independent Review's value confirmed.** 4 of 5 contested points flipped Red-Blue₁'s verdicts; all 4 new findings real and consequential. The IR caught a sequencing problem (Postgres-only v1) the in-session pass had absorbed without flagging.
- **Strongest convergence finding cluster:** Verification economics (PM-1 + PA-2 + MD-3/10) — three different analytical paths converge on "verification cost is the load-bearing safety hazard." Highest-confidence verdict in the assessment.
- **Most consequential single finding:** IR-CP-4 (Postgres-only v1). Caught by Independent Review only; would have shipped as multi-provider had assessment stopped at Red-Blue₁.

---

## References

- Consensus design: [docs/design/migration-squashing-consensus-destructive.md](../design/migration-squashing-consensus-destructive.md)
- ADRs: [0019](../decisions/0019-migration-squash-replaces-graph.md), [0020](../decisions/0020-squashes-are-up-only.md), [0021](../decisions/0021-migration-record-checksum.md)
- Implementation examples: [Postgres](../design/migration-squashing-example-postgres.md), [Aerospike](../design/migration-squashing-example-aerospike.md), [OpenSearch](../design/migration-squashing-example-opensearch.md), [MongoDB](../design/migration-squashing-example-mongodb.md), [Couchbase](../design/migration-squashing-couchbase-example.md)
- Predecessor assessment: [0006-migration-squashing-assessment.md](0006-migration-squashing-assessment.md) (additive model)
- EF Core consultant reference: [ef-core-squash-reference.md](ef-core-squash-reference.md)
- Multi-advocate prior consensus: [docs/design/migration-squashing-consensus.md](../design/migration-squashing-consensus.md) (additive, superseded)
