# Assessment: OpenSearch Provider Implementation Plan

**Date:** 2026-05-02
**Status:** Final
**Subject:** [docs/plans/active/opensearch-provider.md](../plans/active/opensearch-provider.md)
**Mode:** Standard Full Assessment (Triage → 3 Discovery → Synthesis-skipped → Red-Blue → Independent Review → Red-Blue₂ → Consolidation)
**Goals:** Production-capable OpenSearch provider; same migrations across single-node dev, multi-node prod (CI-automated), AWS Managed (scheduled validation); zero data loss; no permanent lockouts.

## Triage

| Skill | Value | Selected |
|-------|-------|----------|
| Pre-mortem | High | Yes |
| Mechanism Design | High | Yes |
| Performance Audit (project-scale) | High | Yes |

## Headline finding

**The plan is structurally sound but needs four targeted amendments before Phase 1 starts.** The risk-first phasing concept survives intact; the cuts are about scoping (Phase 2 is hidden mega-phase, Compose scaffold rots, ADR audit deferred too late) not about reorganizing the architecture. The single highest-ROI mitigation is converting the Style Reference's "non-empty" test into "≥10 file:line citations across ≥4 patterns" — this one change closes a class of cascade risks across all subsequent phases.

The IR identified a critical buried architectural commitment in Phase 5 task 5.3 — *"parse-time `GET /_index_template/<id>` lookup"* — that contradicts ADR-0011's intent that parsers be offline-pure. Resolution: move template-body resolution to runtime, amend ADR-0011 to state "parser is offline-pure; all I/O is runtime middleware."

## Convergence summary

- **Red-Blue₁:** 47% Red / 53% Blue. Balanced.
- **Independent Review:** 5 disagreements + 6 new findings + 3 meta-patterns
- **Red-Blue₂ after IR:** **Red 4 wins / Blue 0 wins / Synthesis 3.** All 6 new findings acknowledged actionable.

## Final consolidated verdicts

### Plan amendments — Must land before Phase 1 starts

| # | Amendment | Source | Severity |
|---|---|---|---|
| **A1** | Split Phase 2 into 2a (DI + ledger + bootstrapper skeleton) and 2b (lock state machine + R-24b suite) | PM-2, PA-1, MD-2 | **High** |
| **A2** | Delete Task 0.6 (multi-node Compose scaffold); rebuild as Phase 4 prereq subtask | PA-3 + Round 2 win | **High** |
| **A3** | Move Task 7.7 (multi-node CI integration) into Phase 4 prereq window — Phase 4 cannot meet its own R-24c-(a) criterion otherwise | PA-7, PM-3, IR | **Critical** (ordering bug) |
| **A4** | Promote Task 0.3 (codebase audit + Style Reference) to Task 0.1 — current Task 0.1 ("Mirror Aerospike runner exactly") cannot run before audit completes | PA-12, IR | **High** |
| **A5** | Add Phase 1.5 gate between Phases 1 and 2 — spike must validate at least one body resolved via live template lookup OR validate the parser/runtime boundary that NF-2 will redraw | PM-1, MD-1, PA-2 | **High** |
| **A6** | Move Hyperbee.Templating spike to Phase 0 — first-contact bugs cascade if left to Phase 6 | PM-4 + design line 201 | **High** |
| **A7** | Style Reference test strategy → "must contain ≥10 file:line citations across ≥4 patterns (lock, bootstrapper, grammar, DI registration)" | MD-4, MD-10 | **High (highest single-mitigation ROI)** |
| **A8** | Phase 1 kill-criterion verbatim: *"merge logic cannot deterministically produce expected JSON without ambiguity for any of the 5 documented edge cases"* | MD-11 + IR Contested 2 (Red wins) | **High** |
| **A9** | Move parse-time template-body resolution to **runtime**; amend ADR-0011 to state "parser is offline-pure; all I/O is runtime middleware" | NF-2 (IR) | **High** (architectural) |
| **A10** | Add Phase 1 fallback paragraph: if spike fails, Approach A (Couchbase-Clone, runtime middleware only) becomes the documented fallback architecture; AST types + grammar (Tasks 1.1-1.2) are reusable | NF-3 (IR) | **High** |
| **A11** | Phase 0 deliverable: enumerated R-24c a-o test table (the suite is referenced 4 times but never enumerated) | NF-4 (IR) | **High** |

### Plan amendments — Should land

| # | Amendment | Source |
|---|---|---|
| **B1** | Per-phase ADR-touched checklist in Definition of Done; shrink Task 7.11 to final regression cross-check, not first-time audit | MD-12, NF-5 |
| **B2** | R-24c forward-reference table (test → phase → covered combinations) | MD-6, NF-4 |
| **B3** | Pair tests with implementation per task; req/ADR cross-reference per task | MD-2 |
| **B4** | Mark each completion criterion `[CI]` or `[judgment]` | MD-9 |
| **B5** | Phase 1 explicit "Spike Iteration 2" subtask — spikes rarely converge first try | PA-2 |
| **B6** | Phase 6 internal ordering: Templating spike (Phase 0 already) → core state-sharing (PerMigration, partial rollback) → consumer surface (banner, samples). One mid-phase checkpoint commit between core and surface. **Not split into 6a/6b/6c.** | IR Contested 1 (Synthesis) |
| **B7** | AWS validation Phase 7 Completion Criteria line: "AWS validation status documented in README with date of last successful run, OR an 'AWS unverified for this release' notice with reason." | IR Contested 3 (Red wins) |
| **B8** | Plan-vs-code authoritative rule: explicit statement | MD-14 |
| **B9** | Weekly main rebase policy stated explicitly | MD-13, PM-5 |
| **B10** | Reflect-step entry template (no checkbox; just template) | MD-15 |
| **B11** | Phase end DoD: append Learnings, update Status Summary, tag snapshot — single line restatement of plan intent | MD-8 (compressed) |
| **B12** | Phase 5 Task 5.3: move template lookup to runtime per A9 | NF-2 |
| **B13** | Task 3.9: cite reserved names from R-09 (`$body`, `$query`, `$script`, `env`, `config`, `runtime`, `secrets`) | NF-1 |
| **B14** | Task 0.4: declare OpenSearch version-support contract (minimum supported, pinned digest, AWS Managed caveat) | NF-6 |
| **B15** | Phase 1 add explicit context object for "tracked indices" — Phase 6's PerMigration dirty-index tracker extends it later | PM-11 |
| **B16** | Sample authoring incremental in Phases 3-5 (one sample per verb as the verb is built) — tag "do-not-cut under deadline" | PM-12 |
| **B17** | Project-level 18-22 week estimate (single buffer; no per-phase 20% buffers) | PA-8 |
| **B18** | Phase 1.5 gate documentation includes family-of-shapes paragraph (folded artifact, not standalone) | MD-1 (folded) |

### Cuts (verdicts the assessment proposed but Red-Blue rejected)

| Cut | Rationale |
|---|---|
| Pre-commit hook for plan updates | Hook ceremony rots; replaced by B11 phase-end DoD |
| Per-phase Style Reference refresh | Folded into B1 ADR-touched checklist |
| Intra-phase tagging policy | Defer — phase + weekly rebase is enough granularity |
| Review SLA | Defer — bus factor 1; resurface when second engineer joins |
| Harness-validation test | Tasks 0.5 (smoke) and 1.4 (wire-level) jointly cover the gap; intermediate test is redundant (IR Contested 4, Red wins) |
| "parallelizable: yes/no" line per phase | Bus factor 1 makes this speculative ceremony (IR Contested 5, Red wins) |
| 20% per-phase buffer | Per-phase buffers compound to Parkinson's Law; project-level buffer instead |
| Splitting Phase 6 into 6a/6b/6c | After moving Templating to Phase 0 (A6), Phase 6 shrinks; remaining tasks loosely coupled — internal ordering + one checkpoint commit suffice (IR Contested 1, synthesis) |

### Discovery findings — final consolidated

#### Pre-mortem
| ID | Final Verdict | Action |
|----|---------------|--------|
| PM-1 heartbeat false takeover spike under-scope | Redesign | A5 (Phase 1.5 gate) + A8 (kill criterion) |
| PM-2 Phase 2 packs 12 tasks | Redesign | A1 split |
| PM-3 Compose scaffold bit-rots | Redesign | A2 delete + A3 move CI work earlier |
| PM-4 Phase 6 nine cross-cutting features | Redesign | A6 (Templating to Phase 0) + B6 (internal ordering) |
| PM-5 Long-lived branch + Style Reference stale | Keep | B9 weekly rebase |
| PM-6 AWS runbook never run | Keep | B7 release checklist |
| PM-7 living-doc under deadline | Monitor | B11 phase-end DoD; no hook |
| PM-8 hello-world only checks cluster health | Cut | Tasks 0.5 + 1.4 cover (IR Contested 4) |
| PM-9 ADR-0011 ages | Keep | B12 + ADR amendment per A9 |
| PM-10 IAM-scoped AWS Managed | Monitor | B7 release checklist surfaces this |
| PM-11 Phase 3/6 shared dirty-index state | Keep | B15 explicit context object |
| PM-12 samples treated as docs | Keep | B16 incremental sample authoring |

#### Mechanism Design
| ID | Final Verdict | Action |
|----|---------------|--------|
| MD-1 family-of-shapes | Keep (folded) | B18 paragraph in Phase 1.5 gate spec |
| MD-2 task lists missing test pairing | Keep | B3 |
| MD-3 Phase 6 ordering arbitrary | Keep | B6 internal ordering |
| MD-4 Style Reference subjective | Keep | A7 (highest ROI) |
| MD-5 ADR-0002 not cited in Phase 3 | Keep | B13 covers reserved names; ADR-0002 cite to be added Task 3.1 |
| MD-6 R-24c tests scattered | Keep | B2 forward-reference table |
| MD-7 intra-phase tagging | Defer | — |
| MD-8 living-doc enforcement | Keep (criterion only) | B11 |
| MD-9 subjective vs objective criteria | Keep | B4 |
| MD-10 audit quality | Keep (subsumed) | A7 |
| MD-11 kill-criterion soft phrasing | Keep | A8 (Red's verbatim wording) |
| MD-12 ADR drift end-audit only | Keep | B1 |
| MD-13 no rebase strategy | Keep | B9 |
| MD-14 plan-vs-code authoritative | Keep | B8 |
| MD-15 ITRV Reflect not actionable | Keep (template only) | B10 |

#### Performance Audit (project-scale)
| ID | Final Verdict | Action |
|----|---------------|--------|
| PA-1 Phase 2 12 tasks | Redesign | A1 split |
| PA-2 No spike re-spin budget | Keep | B5 explicit Iteration 2 subtask |
| PA-3 Phase 0 Compose harness rots | Redesign | A2 delete |
| PA-4 Phase 6 9 sub-tasks | Synthesis | B6 ordering, not split |
| PA-5 Phase 5/6 prereq | Keep | B12 covers (move template lookup runtime) |
| PA-6 bus factor 1 | Monitor | — |
| PA-7 Phase 7 hidden critical path | Redesign | A3 |
| PA-8 zero slack budget | Keep | B17 project-level buffer |
| PA-9 no review SLA | Defer | — |
| PA-10 ADR audit at end | Keep (subsumed) | B1 |
| PA-11 Compose hardening before 4.6 | Keep | Subtask of A2's Phase 4 prereq |
| PA-12 Task 0.3 buried | Redesign | A4 |

### Independent Review new findings — final consolidated

| ID | Severity | Verdict | Action |
|----|----------|---------|--------|
| NF-1 R-09 reserved namespace policy | Medium | Acknowledge | B13 — list exists in requirements; just cite it |
| NF-2 parse-time template lookup | High | Redesign | A9 — move to runtime; amend ADR-0011 |
| NF-3 No Phase 1 fallback strategy | High | Redesign | A10 — Approach A as documented fallback |
| NF-4 R-24c "15 tests" never enumerated | High | Redesign | A11 — Phase 0 produces a-o table |
| NF-5 ADR audit Phase 7 too late | Medium | Redesign | B1 — per-phase DoD |
| NF-6 No version matrix | Medium | Acknowledge | B14 — declare in Task 0.4 |

## Convergence Analysis

**Strong convergence (act now):**
- Phase 2 packs too much — flagged independently by PM (cascading failure mode), MD (test bundling), PA (calendar weeks). Three reasoning paths, same finding. Strong.
- Compose scaffold rots — PM (bit-rot from neglect) + PA (throwaway scaffolding) reach the same conclusion. Strong.
- Phase 7 hidden critical path — PA flagged scheduling, PM flagged ordering coincidence with Phase 4 R-24c-(a) requirement. Strong.

**Weak convergence (review individually):**
- Phase 6 grab-bag — three audits flagged but the convergence may be shared-prior (the same draft was problematic for the same reason, not three independent failure modes). IR's pushback (don't split; reorder) shows this convergence was less robust than it seemed.
- Style Reference subjective — MD-4 + MD-10 are the same finding photographed twice.

**Disagreement that resolved:**
- IR Contested 1 (Phase 6 split): three lenses said split, IR pushed back, resolution was reorder-not-split. The convergence was real but the prescription was over-engineered.
- IR Contested 4 (harness-validation test): Blue advocated; Red showed the gap doesn't exist between Tasks 0.5 and 1.4. Cut.

**Shared-prior check:** "Would a developer reading the plan for 5 minutes notice the same thing?" Yes for MD-4 (trivially-passable test), Yes for PA-1 (12 tasks visible at a glance), No for NF-2 (parse-time GET requires careful reading of plan line 354 + design line 158-167 cross-reference). Confidence high on NF-2 — genuine deep finding.

## Action plan (prioritized)

### P0 — Must land before Phase 1 starts
1. **A1** Split Phase 2 into 2a/2b
2. **A2** Delete Task 0.6 (Compose scaffold); rebuild in Phase 4 prereq
3. **A3** Move Task 7.7 multi-node CI work to Phase 4 prereq window
4. **A4** Promote Task 0.3 to Task 0.1
5. **A5** Add Phase 1.5 gate (template lookup boundary validation)
6. **A6** Move Hyperbee.Templating spike to Phase 0
7. **A7** Style Reference objective criteria (≥10 citations / ≥4 patterns)
8. **A8** Phase 1 kill-criterion verbatim wording
9. **A9** Move parse-time template lookup to runtime; amend ADR-0011
10. **A10** Phase 1 fallback paragraph (Approach A as fallback)
11. **A11** Phase 0 deliverable: enumerated R-24c a-o table

### P1 — Land in v1 (during execution)
12. **B1-B18** as listed above

### P2 — Defer to v1.1
- AWS Managed CI automation (existing Open Question)
- Multi-node performance optimization (PA-class deferrals)
- JSON Schema for `statements.json` (MD-8 IDE help)

## Recommendations

1. **Apply all 11 P0 amendments to the plan now** — they're all editing-not-rewriting; ~30 minutes. The plan is otherwise sound.
2. **Amend ADR-0011** to state "parser is offline-pure; all I/O is runtime middleware" — this resolves NF-2 and prevents the Phase 5 architectural surprise.
3. **Project estimate: 18-22 weeks calendar for one experienced engineer at full focus.** Plan timeline must reflect this; do not under-estimate to user (Brenton).
4. **Recommended order before kicking off `/nop:implement`:**
   - Apply A1-A11 plan amendments
   - Amend ADR-0011 per A9
   - Re-read the plan top-to-bottom checking nothing else cascaded
   - Tag `opensearch/plan-frozen` snapshot
   - Run Phase 0 (Task 0.1 = audit; deliverables include R-24c a-o table)
   - Run Phase 1 spike with the new gate language
5. **No second `/nop:assess` recommended.** This assessment was thorough; the IR's Red-strong outcome shows the plan was modestly gold-plated but had real architectural finds (NF-2, NF-3) that are now addressed. Further assessment without intervening implementation work would surface diminishing returns.

## Out of scope (confirmed during assessment)

- Per-task PR strategy (per-phase PRs are right for solo-maintainer; per-task is ceremony)
- Splitting Phase 0 into 0a (mechanical) / 0b (research) — bounded enough as one phase
- Changing the 8-phase count itself — the count is appropriate for production library scope; the issue is *task distribution*, not phase count
