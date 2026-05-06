# Assessment: Migration Squashing Design (additive model — historical)

**Date:** 2026-05-05
**Status:** Historical — assessed the additive model; supersedes by destructive-model reframe later 2026-05-05
**Author:** Brenton Farmer (with assessment sub-agents)
**Subject:** [docs/design/migration-squashing.md](../design/migration-squashing.md) (additive-model framing; current canonical design is destructive)
**Pipeline:** /nop:assess — Full mode (Triage → Discovery → Red-Blue → Independent Review → Red-Blue₂ → Consolidation)

> **NOTE 2026-05-05.** This assessment evaluated the **additive squash model**
> (originals stay during deprecation window). Several findings remain valid
> against the current destructive model (e.g., IR-N2 `Replaces` immutability,
> P0-3 CAS requirements, P0-4 bulk-load realtime obligation). However, two
> findings are reframed:
> - **PM-1/MD-10 cluster (premature original deletion)** — in the destructive
>   model, originals are *intentionally* removed at squash creation. The
>   safety net is the fleet readiness check, not the R-08 retention rule.
>   This finding is no longer applicable in its original form.
> - **R-15 `--prune` audit-aware tool** — promoted from Phase 4 to v1
>   mandatory; it IS the fleet readiness check.
>
> See [ADR-0019](../decisions/0019-migration-squash-replaces-graph.md) for the
> authoritative current design. This document is retained as the historical
> record of the multi-phase /nop:assess against the additive design.

## Executive Summary

The design's center of gravity — universal Replaces-graph scaffolding (Layer 1) crossed with a deferred per-provider strategy plugin contract (Layer 2) — survives the assessment intact. No verdict reaches Delete. The center holds.

But the surrounding **author foot-guns, validation gaps, and integrity claims overstate what the design as written actually delivers**. The Independent Review caught two material problems the in-session Red-Blue missed: (1) ADR-0021's cryptographic-backing rationale is largely vacuous on the most common case (upgrade-then-rollup against pre-checksum-era ledger rows), and (2) the design has no spec for `Replaces`-set mutation between releases — a silent correctness bug, not a hygiene issue. Three of five contested points from Independent Review flipped Red-Blue₁'s verdicts entirely.

**Net findings:** 26 total. **12 Redesign** (specific actions), **2 Defer**, **3 Monitor**, **9 Keep**. P0 / P1 / P2 priority assignments at the end.

The most consequential redesigns are: IR-N2 (`Replaces` immutability + include in checksum scope) and IR-N1 (honest re-framing of ADR-0021's integrity claims). Both are correctness/clarity issues, not hygiene. CP-3's reversal preserves the user's "no new attribute" directive that the original Red-Blue₁ verdict had quietly violated.

---

## Phase 0 — Triage

**Artifact type:** Architecture / Design (with downstream Plan implications, three associated ADRs in Proposed status)

| Skill | Value | Rationale |
|-------|-------|-----------|
| Pre-mortem | High | Forward-looking infrastructure with strong temporal dimension. Design depends on social discipline (R-08 originals-stay; R-15 audit-aware prune). 12-month horizon. |
| Mechanism Design | High | Multiple consumer-facing surfaces: `[Migration(Replaces=…)]`, `IRollupStrategy`, `RollupHints`, `--accept-unverified`, `--prune` flags, `IChecksumStrategy<TMigration>`. Misuse paths central. |
| Performance Audit | Medium | Doc artifact, but executable behavior with real scale dimensions: checksum on every write, partial-catch-up reconciliation, fusion-rule application, round-trip verification time. Worth surfacing. |

**Selected:** PM, MD, PA (all three, parallel dispatch). **Skipped:** none.

---

## Phase 1 — Discovery (parallel, independent)

23 findings produced across three skills. Severity distribution: 5 Critical, 13 High, 5 Medium, 1 Low. Findings labeled PM-N / MD-N / PA-N for traceability. Full per-skill detail captured in Red-Blue₁ inputs (below); summary table in Phase 4.

### Convergence among discovery skills (shared-prior check applied)

**Strong convergence (different analytical frames, different reasoning paths):**
- **PM-1 + MD-10 + PA-3:** strict-subset partial-catch-up risk surfaces from temporal discipline-decay (PM), consumer laziness modeling (MD), and concurrency analysis (PA) — three genuinely different angles converging on the same correctness hazard.
- **PM-3 + PA-6:** OpenSearch concurrent auto-mark race surfaces from cascading-ADR-0018 dependency analysis (PM) and CAS-race concurrency modeling (PA).
- **PM-4 + MD-2:** unmarked data-op silent loss surfaces from temporal data-correctness drift (PM) and lazy-author path modeling (MD).

**Weak convergence (shared surface — would notice in 5 minutes of code reading):**
- **MD-3 + MD-4 + MD-5:** Replaces validation gaps (non-existent versions, self-reference, duplicates) all surface from "what if author types junk." Treated as one finding cluster, not three independent confirmations.

---

## Phase 2 — Synthesis (skipped with justification)

**Consumers enumerated** (8): rollup author, regular-migration author w/ data ops, provider strategy implementer, provider record-store implementer, CI/prod operator, `--prune` operator, hyperbee maintainer, runtime consumer.

**Conflicting non-negotiables check:** one possibly-conflicting pair surfaced (rollup-author brevity vs. operator data-op safety, raised by PM-4 + MD-2). Other consumer pairs show only mild tensions resolved by the additive contract.

**Decision:** Skip Synthesis — single tension within Red-Blue's resolution scope. Documented here as the evidence trail for the skip decision.

---

## Phase 3 — Red-Blue₁ Convergence

23 findings clustered by theme (reconciliation correctness, mechanism-design foot-guns, capability fork, performance). Subtraction Scan (Round 0) flagged `MigrationRecordKind.Baseline` and `IRollupStrategy.Unsupported` for delete consideration; the Defer/Keep verdicts in Round 3 absorbed these.

**Outcome:** **Zero contested points after 3 rounds.** All 23 findings reached convergence. Verdict distribution from Red-Blue₁:
- Redesign: 11
- Defer: 2
- Monitor: 3
- Keep: 7

**Notable resolutions in RB₁:**
- The strict-subset partial-catch-up correctness hazard (PM-1/MD-10) consolidated to: load-time validation that every `Replaces` value resolves to a discovered descriptor or existing ledger row.
- The `[RollupAuthored]` attribute proposal (Item 6) emerged as a compromise — Independent Review later rejected this as a violation of the user's "no new attribute" directive.

---

## Phase 3.5 — Independent Review

Fresh sub-agent received only the artifact files and the consolidated findings table — no Red-Blue rationale, no discovery-finding details.

**Outcome:**
- **16 verdicts: agree** (often with refinements that don't change the verdict)
- **5 verdicts: substantive disagreement** — Items 2, 4, 6, 8, 20
- **3 new findings** the assessment missed entirely — IR-N1, IR-N2, IR-N3

The Independent Review's substantive disagreements held up under Red-Blue₂ scrutiny in 3 of 5 cases (Red wins on CP-1, CP-3, CP-4). The remaining 2 resolved to Synthesis (CP-2, CP-5). All 3 new findings were confirmed real and assigned Redesign verdicts.

---

## Phase 3.75 — Red-Blue₂ Resolution

| ID | Title | Resolution | Effect |
|----|-------|------------|--------|
| **CP-1** | Item 2 — Warn-on-referenced-without-Replaces | **Red wins** | Item 2 reverts from Redesign to **Keep**. The original assessment misnamed the trigger condition; the proposed warning would fire on every legitimate original. No useful framework-level signal exists; defer rollup-naming-convention heuristic to Phase 2. |
| **CP-2** | Item 4 — `AcceptUnverifiedUntil` shape | **Synthesis** | Replace `AcceptUnverifiedUntil` (date) with `AcceptUnverifiedVersions` (version allowlist). Optional `AcceptUnverifiedReviewBy` date emits warning on expiry but does not refuse. Avoids "midnight production cliff"; per-version explicit acknowledgement. |
| **CP-3** | Item 6 — `[RollupAuthored]` attribute | **Red wins** | Item 6 reverts from Redesign to **Keep with documentation only**. The proposed `[RollupAuthored]` attribute violates the user's explicit "no new attribute" directive cited in ADR-0019. Non-empty `Replaces` is the sufficient discriminator; v1 ships documentation; heuristic data-op detection deferred to Phase 2 alongside generators. |
| **CP-4** | Item 8 — `--accept-rollup-up-only` rename | **Red wins** | Rename target changes from `--lose-down-across-rollup` to `--source-has-down-overrides` (or equivalent). The flag governs *generator behavior* (allowing composition of source migrations whose `DownAsync` overrides are non-trivial), not whether Down is lost — the loss is intrinsic. The proposed Red-Blue₁ rename was misleading. |
| **CP-5** | Item 20 — `IRollupStrategy` Experimental marking | **Synthesis (lean Red)** | Mark `IRollupStrategy`, `RollupGenerationResult`, `RollupGenerationOptions` with `[Experimental("HBM-ROLLUP-STRATEGY-001")]` in v1. Add ADR-0019 sentence: contract is provisional until first provider strategy ships. Default `Unsupported(...)` registrations remain so consumers don't take a dependency. |
| **IR-N1** | Auto-mark integrity check logical hole | **Redesign** | ADR-0021 advertises cryptographic backing but on first deployment after upgrade, every replaced row is pre-checksum-era — auto-mark always falls back to allowlist. Edit ADR-0021 Consequences: "best-effort going forward, not retroactive." Add Phase-2 deferred `--seal-history` one-shot remediation. Update design's risks-and-open-questions: pre-checksum-era is the dominant initial-rollout case. |
| **IR-N2** | No spec for `Replaces` drift between releases | **Redesign (correctness fix)** | v1.1 mutating a rollup's `Replaces` set silently skips newly-added versions because auto-mark looks up by Id, not contents. **This is a correctness bug, not hygiene.** (a) Add `Replaces` immutability rule to ADR-0019: re-squash via *new* rollup, not mutation. (b) Update ADR-0021 default checksum: for rollup-kind migrations, hash `(sorted Replaces array) ‖ resource bytes`. (c) Runner refuses to proceed if a discovered rollup's checksum disagrees with its ledger row's checksum. |
| **IR-N3** | `RollupHints.ReplayOnFreshOnly` fictitious for hand-authored v1 | **Redesign (light)** | The mode is a generator concept; hand-authored `UpAsync` has no signal whether running fresh-install vs. partial-catch-up. Fix: add `MigrationContext.IsFreshInstall` to runtime context surface. Cheap (the runner already knows from auto-mark/fresh-install/partial branching); unblocks hand-authored use of all three modes. Update ADR-0019 Decision 6 accordingly. |

**Red-Blue₂ summary:** 3 Red wins / 2 Synthesis / 0 Blue wins on contested points. All 3 new findings → Redesign. The IR's analysis genuinely caught problems the in-session Red-Blue missed; the assessment is materially better for the second pass.

---

## Phase 4 — Consolidated Findings

### Convergence quality per finding

Strong-convergence findings (act on these with high confidence): **PM-1/MD-10/PA-3 cluster** (strict-subset partial-catch-up), **PM-3/PA-6 cluster** (OpenSearch auto-mark race), **PM-4/MD-2 cluster** (unmarked data-op silent loss). All three reflect different analytical frames arriving at the same conclusion via genuinely different reasoning paths.

Weak-convergence findings (review individually): **MD-3/MD-4/MD-5 cluster** (Replaces validation gaps) — surface observations from a single laziness-modeling lens; treated as a single Redesign action.

Independent-Review-only findings (not detected by discovery skills): **IR-N1, IR-N2, IR-N3.** All three are real and consequential — the IR's value is precisely catching what the in-session pass missed.

### Final verdict table

| # | Finding | Source | Severity | Verdict | Action |
|---|---------|--------|----------|---------|--------|
| 1 | Originals deleted prematurely → strict-subset partial-catch-up references missing originals | PM-1 + MD-10 + PA-3 | Critical | **Redesign** | Load-time validation: every `Replaces` value must resolve to a discovered descriptor in the assembly. (Reconciliation-time tolerance for "in ledger but not in assembly" is a *separate* check; do not conflate per IR refinement.) `--prune` adds: emit warning if any environment ledger lacks the rollup row before archiving. |
| 2 | No syntactic distinction rollup vs regular | MD-1 | Critical | **Keep** | Original RB₁ verdict (warn on referenced-without-Replaces) was reversed in RB₂ as misframed. No useful framework-level signal exists; document the convention; rely on review. |
| 3 | Replaces validation gaps (non-existent / self / duplicates) | MD-3, MD-4, MD-5 | High | **Redesign** | ADR-0019: (a) `Replaces` cannot include self-version → load-time error; (b) duplicates normalized to a set with warning; (c) `Replaces` referencing non-existent version → load-time error per item 1. |
| 4 | Pre-checksum-era null tolerance becomes permanent | PM-2 + MD-7 | High | **Redesign** | `MigrationOptions.AcceptUnverifiedVersions` (`long[]` or range list) — explicit per-version allowlist (revised per CP-2 from the original date-based proposal). Optional `AcceptUnverifiedReviewBy` date logs warning on expiry but does not refuse. CLI: `--accept-unverified-version <v>[,<v>...]`. |
| 5 | OpenSearch concurrent auto-mark race | PM-3 + PA-6 | High | **Redesign** | ADR-0021: auto-mark write MUST use `if_seq_no`/`if_primary_term` CAS (or provider-equivalent). Bare `OpType=Index` rejected. Spec must address both "first writer wins" and "second writer detects benign concurrency as no-op success" — not just "rejected." |
| 6 | Unmarked data ops in hand-authored rollups silently lose on fresh installs | PM-4 + MD-2 | Critical | **Keep** | Original RB₁ verdict (introduce `[RollupAuthored]` attribute) was reversed in RB₂ for violating the user's "no new attribute" directive. v1: documentation only; keep the partial-catch-up warning ADR-0019 already promises. Heuristic data-op detector deferred to Phase 2 alongside generators. Trigger for that detector: `Replaces` non-empty (no new marker). |
| 7 | Provider implementer forgets to register `IRollupStrategy` → `ServiceNotFound` | MD-6 | High | **Redesign** | Core registers `NullRollupStrategy : IRollupStrategy` returning `Unsupported("no strategy registered for {provider}")` by default. Providers override; CLI never throws `ServiceNotFound`. |
| 8 | `--accept-rollup-up-only` flag name | MD-8 | Medium | **Redesign** | Rename to `--source-has-down-overrides` (revised per CP-4 from RB₁'s `--lose-down-across-rollup`, which was misleading — the flag governs generator behavior, not whether Down is lost). |
| 9 | `--accept-stranding` global vs per-environment | MD-9 | High | **Redesign** | `--accept-stranding=<env-name>[,<env-name>...]` requires explicit named environments; bare flag rejected. |
| 10 | Provider Checksum column nullability | MD-11 | Medium | **Keep** | ADR-0021 already specifies nullable. Action: provider integration tests verify nullability via insert-null+read-back. |
| 11 | Pseudocode shows `IEnumerable.All` over async lambdas | PA-1 | High | **Redesign** | Update design pseudocode: `var applied = await store.LoadAppliedVersionsAsync(); var allReplacesApplied = migration.Replaces.All(applied.Contains);` Single bulk read; in-memory HashSet; no per-version RTT. |
| 12 | Checksum recomputation per reconciliation pass | PA-2 | Medium | **Keep** | Clarify in ADR-0021 Consequences: "Checksum is computed once at WriteAsync; subsequent reads use the stored value, never recompute." |
| 13 | Strict-subset lock contention with concurrent runners | PA-3 | High | **Monitor** | No design change. CI signal: integration test for 10-runner concurrent strict-subset catch-up; track failure rate over time. |
| 14 | Single-threaded reflection discovery | PA-4 | Low | **Keep** | Marginal; no action. |
| 15 | R-13 round-trip verification container-startup cost | PA-5 | Medium | **Redesign** | Move R-13 from v1 scaffolding to **Phase 2 explicitly**; v1 excludes round-trip verification but provides minimum smoke-test floor: rollup applies cleanly on a fresh container. (Smoke floor added per IR refinement.) |
| 16 | Provider capability fork (Postgres > MongoDB > OpenSearch > others) | PM-5 | Medium | **Monitor** | No design change. ADR-0019 Consequences: "Generator availability is per-provider and demand-driven; user-facing hand-authored experience is identical across providers; generator parity is not guaranteed." |
| 17 | Originals decay over time | PM-6 | Medium | **Monitor** | Documented in design's riskiest-assumption section. Add ADR-0019 Consequences guidance: authors mark data-bearing originals `ReplayOnFreshOnly`. |
| 18 | `RollupHints` 3-mode taxonomy | Decision 6 | n/a | **Keep** | API surface in v1; enforcement deferred to Phase 2 generators. Hand-authored authors use `MigrationContext.IsFreshInstall` (per IR-N3) to honour `ReplayOnFreshOnly`. |
| 19 | `MigrationRecordKind.Baseline` | Subtraction Scan | n/a | **Defer** | Defer `Kind = Baseline` to Phase 4 (Open Q 4 unresolved). Keep `Migration | Rollup` in v1. |
| 20 | `IRollupStrategy` contract in v1 | Subtraction Scan | n/a | **Keep with Experimental marker** | Mark with `[Experimental("HBM-ROLLUP-STRATEGY-001")]`. Default registrations remain `Unsupported(...)`. ADR-0019 sentence: contract is provisional until first provider strategy ships. (Per CP-5 synthesis.) |
| 21 | R-16 directory hash | R-16 | n/a | **Defer** | Already deferred; no change. |
| 22 | Strict-subset partial-catch-up overall | PM-6 + Risks section | n/a | **Keep with items 1+17** | Validation per item 1; risk note per item 17; prototype committed via design's Recommended Next Steps item 4. |
| 23 | OpenSearch AST fusion privilege | Decision 2 | n/a | **Keep** | Decision 2 de-privileges; correct. |
| **IR-N1** | Auto-mark integrity check is vacuous on first rollout | IR | High | **Redesign** | Edit ADR-0021 Consequences: "Cryptographic backing applies only to migrations applied *after* this ADR ships. Pre-checksum-era rows are the dominant initial-rollout case; auto-mark for those falls back to `Id` lookup + `AcceptUnverifiedVersions` opt-in. Best-effort going forward, not retroactive." Add deferred Phase-2 ticket: `--seal-history` one-shot remediation tool. |
| **IR-N2** | `Replaces`-set mutation between releases | IR | **High (correctness)** | **Redesign** | (a) ADR-0019 new section: `Replaces` immutability rule. Re-squash via *new* rollup, not mutation. (b) ADR-0021 default checksum scope for rollup-kind migrations: `SHA-256(sorted_Replaces ‖ resource_bytes)`. (c) Runner discovery: refuse to proceed if computed rollup checksum disagrees with stored ledger checksum; surface diagnostic naming both. |
| **IR-N3** | `ReplayOnFreshOnly` fictitious for hand-authored v1 | IR | Medium | **Redesign** | Add `MigrationContext.IsFreshInstall` boolean to runtime context surface. Runner already knows from auto-mark/fresh-install/partial branching; cheap to expose. Update ADR-0019 Decision 6 to reference `IsFreshInstall`. |

### Distribution

| Verdict | Count | Items |
|---------|-------|-------|
| **Redesign** | 12 | 1, 3, 4, 5, 7, 8, 9, 11, 15, IR-N1, IR-N2, IR-N3 |
| **Defer** | 2 | 19, 21 |
| **Monitor** | 3 | 13, 16, 17 |
| **Keep** | 9 | 2, 6, 10, 12, 14, 18, 20 (with Experimental note), 22, 23 |
| **Delete** | 0 | (Subtraction Scan candidates absorbed into Defer/Keep) |

---

## Priority-Tagged Amendments

### P0 — Block ship of design until addressed

These are correctness or material-integrity issues. The design as written has known holes the team must plug before plan/implement.

- **P0-1 (IR-N2):** `Replaces` immutability rule + checksum-scope change. Without this, v1.1 mutating a rollup's `Replaces` silently corrupts environments. Three concrete edits required (ADR-0019 immutability section; ADR-0021 default checksum hashes `sorted_Replaces ‖ resource_bytes` for rollup-kind migrations; runner refuses on checksum mismatch).
- **P0-2 (Item 1, PM-1/MD-10/PA-3):** Load-time validation that every `Replaces` value resolves to a discovered descriptor in the assembly. Strongest convergence finding in the assessment (3 different analytical paths). Without this, premature original deletion or typo silently corrupts partial-catch-up environments.
- **P0-3 (Item 5, PM-3/PA-6):** OpenSearch auto-mark write must use CAS (`if_seq_no`/`if_primary_term`). Bare `OpType=Index` admits the concurrent-runner race. ADR-0021 must specify CAS for the auto-mark path.
- **P0-4 (Item 11, PA-1):** Update the design pseudocode from per-version `ExistsAsync` (sequential awaits) to bulk `LoadAppliedVersionsAsync` + in-memory HashSet check. The pseudocode as shipped will be copied by implementers and produces 500s of network wait on OpenSearch with 10K migrations.

### P1 — Material amendments before plan/implement

Important quality and clarity issues; ship-blockers for v1 even if not strict correctness bugs.

- **P1-1 (IR-N1):** Honest re-framing of ADR-0021's integrity claims. The "cryptographic backing" rationale overstates what auto-mark delivers in the upgrade-then-rollup case. Edit ADR-0021 Consequences; mark the gap explicitly; add deferred `--seal-history` ticket.
- **P1-2 (Item 4, PM-2/MD-7):** Replace `AcceptUnverifiedUntil` (date) with `AcceptUnverifiedVersions` (allowlist). Avoids midnight-cliff production failure; per-version explicit acknowledgement.
- **P1-3 (Item 3, MD-3/4/5):** Load-time validation for `Replaces` shape (no self-reference, normalized duplicates, all entries resolvable per P0-2).
- **P1-4 (Item 7, MD-6):** Core registers `NullRollupStrategy` default. Removes `ServiceNotFound` on missing provider strategy registration.
- **P1-5 (Item 9, MD-9):** Per-environment scoped `--accept-stranding` flag.
- **P1-6 (Item 15, PA-5):** Move R-13 round-trip verification from v1 to Phase 2. v1 keeps a smoke-test floor: rollup applies cleanly on a fresh container.
- **P1-7 (IR-N3):** `MigrationContext.IsFreshInstall` boolean. Required for `ReplayOnFreshOnly` to be more than fiction in hand-authored v1.

### P2 — Quality improvements

Useful but not blocking.

- **P2-1 (Item 8, CP-4):** Rename to `--source-has-down-overrides`.
- **P2-2 (Item 12, PA-2):** Clarify in ADR-0021: checksum computed once at write; never recomputed on read.
- **P2-3 (Item 20, CP-5):** Mark `IRollupStrategy` and friends `[Experimental]`.
- **P2-4 (Item 13, PA-3):** CI integration test for 10-runner concurrent strict-subset catch-up; track failure rate.
- **P2-5 (Item 16, PM-5):** ADR-0019 Consequences honest framing of generator-availability per-provider asymmetry.
- **P2-6 (Item 17, PM-6):** ADR-0019 Consequences guidance on `ReplayOnFreshOnly` for data-bearing originals.
- **P2-7 (Item 10, MD-11):** Provider integration test that verifies Checksum-column nullability via insert-null+read-back.

---

## Summary of Required Edits

| Artifact | Required edits |
|----------|----------------|
| **docs/decisions/0019-...** | (a) Add `Replaces` immutability rule (IR-N2). (b) Add load-time validation rules (Items 1, 3). (c) Add Consequences: capability-parity disclaimer (Item 16); `ReplayOnFreshOnly` guidance (Item 17); `MigrationContext.IsFreshInstall` reference (IR-N3). (d) Add: `IRollupStrategy` provisional / Experimental note (Item 20). |
| **docs/decisions/0020-...** | Update flag name from `--accept-rollup-up-only` to `--source-has-down-overrides` (Item 8). |
| **docs/decisions/0021-...** | (a) Default checksum scope for rollup-kind migrations: `SHA-256(sorted_Replaces ‖ resource_bytes)` (IR-N2). (b) Auto-mark CAS specification (Item 5). (c) `AcceptUnverifiedVersions` allowlist instead of date (Item 4). (d) Honest re-framing: best-effort going forward, not retroactive (IR-N1). (e) Clarify computed-once-at-write (Item 12). |
| **docs/design/migration-rollups.md** | (a) Update reconciliation pseudocode to bulk `LoadAppliedVersionsAsync` (Item 11). (b) Move R-13 round-trip verification to Phase 2; add v1 smoke-test floor (Item 15). (c) Update Decision 6 to reference `MigrationContext.IsFreshInstall` (IR-N3). (d) Update Risks-and-Open-Questions: pre-checksum-era is dominant initial-rollout case (IR-N1). (e) Remove the `[RollupAuthored]` proposal that briefly appeared during RB₁ — was reversed in RB₂. |
| **docs/requirements/migration-rollups.md** | R-01 default checksum scope updated to include `Replaces` for rollup-kind migrations. R-13 marked Phase 2. R-06 references `MigrationContext.IsFreshInstall` for the `ReplayOnFreshOnly` mode in v1 hand-authored case. |

---

## Recommended Next Steps

1. **Apply P0 amendments** to ADRs 0019, 0020, 0021 and the design doc. Re-cycle status from Proposed → Proposed (revised).
2. **Apply P1 amendments** in the same pass — they're cheap and avoid revisiting the docs.
3. **`/nop:plan`** for Phase 1 with the cross-provider lens already baked in. Plan tasks must demonstrate per-provider participation per the design's Constraint section, and each P0 amendment becomes a vertical slice with tests.
4. **Defer P2 amendments** to plan-time refinement — most are documentation or test additions that the plan will naturally surface.

The design with these amendments is materially safer than the original. The universal scaffolding center holds; the redesigns cluster around author foot-guns, validation gaps, and integrity-claim calibration — all addressable in the ADRs and design doc without disturbing the core mechanism.

---

## Methodology Notes

- **Discovery independence verified.** Three sub-agents dispatched in parallel; each received only the artifact, goals, constraints, and its analytical frame. Convergence findings confirmed with shared-prior check (downgraded MD-3/4/5 cluster as weak convergence).
- **Synthesis skipped with explicit consumer enumeration as evidence trail.** Eight consumers; one possibly-conflicting pair; within Red-Blue's resolution scope.
- **Independent Review's value confirmed.** 3 of 5 contested points flipped Red-Blue₁'s verdicts in Red-Blue₂; all 3 new findings real and actionable. The IR's `[RollupAuthored]` rejection (CP-3) caught a quiet violation of the user's explicit directive that the in-session pass had absorbed without flagging.
- **Strongest-convergence finding:** strict-subset partial-catch-up risk (PM-1 + MD-10 + PA-3) — three genuinely independent analytical paths converge on the same correctness hazard. Highest confidence verdict in the assessment.
- **Most consequential single finding:** IR-N2 (`Replaces`-set mutation correctness bug). Caught by Independent Review only; would have shipped silently if assessment had stopped at Red-Blue₁.

---

## References

- Design under review: [docs/design/migration-rollups.md](../design/migration-rollups.md)
- Requirements: [docs/requirements/migration-rollups.md](../requirements/migration-rollups.md)
- Research basis: [docs/research/0005-migration-rollups.md](0005-migration-rollups.md)
- ADRs: [0019](../decisions/0019-migration-rollup-replaces-graph.md), [0020](../decisions/0020-rollups-are-up-only.md), [0021](../decisions/0021-migration-record-checksum.md)
- Comparable assessment: [0002-opensearch-provider-assessment.md](0002-opensearch-provider-assessment.md), [0003-opensearch-plan-assessment.md](0003-opensearch-plan-assessment.md)
