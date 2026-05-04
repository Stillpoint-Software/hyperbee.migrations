# Assessment: OpenSearch Provider Requirements

**Date:** 2026-05-02
**Status:** Final
**Subject:** [docs/requirements/opensearch-provider.md](../requirements/opensearch-provider.md)
**Mode:** Standard Full Assessment (Triage → 3 Discovery → Synthesis → Red-Blue → Independent Review → Red-Blue₂ → Consolidation)
**Goals:** Production-capable OpenSearch provider; zero data loss during reindex/alias swaps; no permanent lockouts; same migrations run unchanged across single-node dev, multi-node prod, and AWS Managed OpenSearch.

## Triage

| Skill | Value | Selected |
|-------|-------|----------|
| Pre-mortem | High | Yes |
| Mechanism Design | High | Yes |
| Performance Audit | High | Yes |

## Headline finding

**The Independent Review's meta-claim was validated and is the most important takeaway:** the synthesis recurringly defers to "samples and documentation" as fixes for correctness hazards on the *laziest* code path. This contradicts the mechanism design premise that consumers take the path of least resistance. R-17's existing `dynamic: strict` injection is the correct precedent — silent-default insertion enforced by the parser, not by docs. Apply that shape to **PM-3** (`op_type: create` injection on `REINDEX`), **MD-3** ($body namespace policy), **PA-2** (lock-index settings), **MD-9** (component-template-aware injection logic). The test: *can a competent author who ignores the samples still ship a correct migration?* If no, parser/runtime must enforce.

## Convergence summary

- **Red-Blue₁ balance:** ~55% Red / ~45% Blue. Balanced.
- **Independent Review:** 5 disagreements + 5 new findings + 1 meta-pattern.
- **Red-Blue₂ balance:** Red won 4 of 5 contested points; Blue conceded 4 of 5 new findings; meta-pattern validated.

## Final consolidated verdicts

### Synthesis amendments (revised after Red-Blue₂)

| Amendment | Final Verdict | Action |
|---|---|---|
| 1. ~~R-29 EnvironmentProfile enum~~ → `WithProductionDefaults()` extension | **Redesign** | Replace enum with extension method `services.AddOpenSearchMigrations(...).WithProductionDefaults()` that explicitly sets the four options (ClusterHealthThreshold=Green, WaitMode=PerMigration, RequireUnsafeJustification=true, ContextResolutionPolicy=RequireExplicit). Keep the startup-log banner invariant. No hidden coupling. |
| 2. R-03 profile-driven threshold | Keep | Production = Green via the extension; Yellow remains the SDK default for dev |
| 3. R-10 SecretMarker + log-time SecretScrubber by hash | Keep | Ship as designed |
| 4. R-12 WaitMode enum | **Keep + scope amendment (NF-3)** | Implicit wait is `PerMigration` by default in production. Implicit waits scope to the mutated index by default (e.g., `?index=users-v2`) so a permanently-yellow `.opendistro_security` doesn't stall unrelated migrations. Cluster-wide is explicit `WAIT FOR GREEN` with no `ON <idx>`. NO WAIT requires justification token |
| 5. R-15 ActiveContext + RequireExplicit policy | Keep | Resolves Open Question; Production forces RequireExplicit |
| 6. R-18 UNSAFE justification | Keep | Token requires justification string; structured WARN log; explicit syntactic enumeration in samples |
| 7. R-21 SigV4 loud-fail + endpoint-capability detection | Keep | Detects `*.amazonaws.com` / `*.aoss.amazonaws.com` and AWS-specific ISM endpoint paths |
| 8. R-05 lock validation + realtime GET on takeover | Keep | Validation enforces `LockRenewInterval < LockStaleAfter < LockMaxLifetime` AND `LockStaleAfter ≥ 2*LockRenewInterval`. Takeover uses `GET /{idx}/_doc/{id}?realtime=true` to avoid search-staleness false positives. LockTuning presets demoted to docs |
| 9. R-25 logs route through SecretScrubber | Keep | Pairs with #3 |
| 10. Trust Boundaries / startup banner | Keep | Banner shows resolved defaults including rollback enabled/disabled state |
| 11. R-27 samples expanded | Keep | Demonstrate WaitMode, UNSAFE justification, $body namespace, op_type behavior |
| 12. Decided list cleanup | Keep | Hygiene |
| 13. R-17 dynamic:strict opt-in | **Redesign** | Make injection opt-in (not default), or component-template-aware (skip injection when body has `composed_of`) — apply uniform shape with new Amendment 14 |
| **14 (new). R-08a `REINDEX SAFE` default** | **Add** | `REINDEX FROM x TO y` injects `op_type: create` by default; opt out with `REINDEX UNSAFE FROM x TO y` (with justification, per Amendment 6). Closes PM-3 at parser level. R-24c integration test asserts `op_type: create` is on the wire by default |
| **15 (new). R-15a semantic version comparison** | **Add (Must)** | `WHEN VERSION` parses to `System.Version` / SemVer; rejects unparseable inputs at parse time; integration test asserts `'2.9' < '2.10'` (lexically false but semantically true). Documented suffix-normalization for `-SNAPSHOT`, `-rc1`, AWS `OpenSearch_2.x` prefix |
| **16 (new). R-16 atomic precondition** | **Add (Must, correctness)** | `ALIAS SWAP` precondition is expressed inside the single `_aliases` POST body (e.g., the `remove` action targets `<old>` so the cluster rejects the body atomically if `<old>` is not the current target). Strike the separate precondition GET from R-16 Otherwise clause |
| **17 (new). R-06 ledger forensic fields** | **Add (Must)** | Ledger mapping includes `appliedBy` (string: machine + pid + optional `RunnerId`) and `direction` (`Up`/`Down`). Strict mapping is immutable per Forbidden list — must land before v1 |
| **18 (new). R-19 partial rollback semantics** | **Add (Must, correctness)** | When Down rollback fails mid-sequence: ledger entry marked `status: partially_rolled_back` with failed-statement index; subsequent runs refuse to retry in either direction without explicit `--force-resume`; error lists failed + already-rolled-back statements |
| **19 (new). R-28 multi-node CI as Must** | **Promote** | Multi-node Testcontainers Compose (3-node) is Must with CI automation; AWS Managed remains Should + scheduled |
| **20 (new). R-07 ledger refresh budget** | **Monitor** | Keep `?refresh=wait_for` as default; R-24c adds measured-cost test ("100-migration bootstrap completes in < N seconds"). If budget breaks, alternative is `refresh=true` for ledger writes (hot single-doc index, bounded cost) |

### Discovery findings (final consolidated)

#### Pre-mortem
| ID | Final Verdict | Action |
|----|---------------|--------|
| PM-1 heartbeat false takeover | Redesign | Amendment 8 (validation + realtime GET on takeover) |
| PM-2 SigV4 creds caching | Redesign | Amendment 7 |
| PM-3 reindex stale dst | **Redesign at parser level** | Amendment 14 (auto-inject `op_type: create`) |
| PM-4 dynamic:strict clobbers | Redesign | Amendment 13 (opt-in or component-template-aware) |
| PM-5 templating JSON-context bugs | Monitor | Add to R-24c test list |
| PM-6 AWS ISM endpoint differences | Redesign | Amendment 7 (expanded) |
| PM-7 yellow alias swap | Keep | Resolved by Amendment 2 + multi-node CI (Amendment 19) |
| PM-8 stagnant 1.8 client | Defer | Track upgrade cadence; revisit when OpenSearch 3.x ships |
| PM-9 WHEN VERSION semver | **Promoted to Must** | Amendment 15 |
| PM-10 mapping drift via hand-edit | Monitor | Operator-discipline; no design fix |
| PM-11 Testcontainers mutable pin | Redesign | Pin by sha; trivial |
| PM-12 LockMaxLifetime ceiling | Redesign | Amendment 8 (explicit cancellation contract) |

#### Mechanism Design
| ID | Final Verdict | Action |
|----|---------------|--------|
| MD-1 context source-of-truth | Keep | Amendment 5 |
| MD-2 UNSAFE single-token | Keep | Amendment 6 |
| MD-3 templating $body collision | **Re-examine** | Apply meta-pattern: parser-level namespace policy + reserved name list (not just docs) |
| MD-4 Yellow default ships | Keep | Amendment 2 + WithProductionDefaults() extension |
| MD-5 Lock TTL coupling | Keep | Amendment 8 validation |
| MD-6 SigV4 invisible | Keep | Amendment 7 |
| MD-7 implicit wait scope | Keep | Amendment 4 |
| MD-8 raw mapping JSON / no schema | Defer | Nice-to-have JSON Schema for IDE help; v1.1 |
| MD-9 dynamic:strict copy-paste | **Re-examine** | Apply meta-pattern: component-template-aware injection at parser level (Amendment 13) |
| MD-10 WHEN VERSION lazy strings | **Promoted to Must** | Amendment 15 |
| MD-11 NO WAIT shape | Keep | Amendment 4 |
| MD-12 bulk-load _refresh appears hung | Monitor | Log-line clarity fix; trivial |
| MD-13 rollback opt-in invisible | Keep | Amendment 10 startup banner |
| MD-14 IF NOT EXISTS omitted | Defer | Doc warning (this one IS appropriate for docs — author actively writes the verb) |
| MD-15 secrets in config scope | Keep | Amendment 3 |

#### Performance Audit
| ID | Final Verdict | Action |
|----|---------------|--------|
| PA-1 ledger refresh=wait_for serial | **Promoted to Monitor** | Amendment 20 (measured-cost test) |
| PA-2 lock shard contention | **Re-examine** | Apply meta-pattern: parser/runtime sets `number_of_replicas: 0` on lock index at create — not just doc |
| PA-3 implicit health-wait N+1 | Keep | Amendment 4 (PerMigration default) |
| PA-4 Tasks API INFO log flood | Redesign | Demote to DEBUG; trivial |
| PA-5 lock false-positive | Keep | Amendment 8 (realtime GET) |
| PA-6 bulk parallelism topology-blind | Defer | Topology-aware tuning is v1.1 |
| PA-7 templating no caching spec | Defer | Specify if profiling shows hot path |
| PA-8 Parlot construction cost | Defer | Per-runner caching when profiled |
| PA-9 SigV4 signing overhead | Defer | Re-evaluate if AWS users hit limit |
| PA-10 conn pool pins one node | Defer | Pairs with PM-8 client upgrade |
| PA-11 WAIT UNTIL TASK 30s ceiling | Defer | Minor |
| PA-12 bootstrap health storm | Defer | Pairs with PA-3 fix |

### New findings (from Independent Review)

| ID | Severity | Final Verdict | Action |
|----|----------|---------------|--------|
| NF-1 R-06 ledger unforensic | Medium | **Redesign** | Amendment 17 — add `appliedBy` + `direction` |
| NF-2 R-16 ALIAS SWAP TOCTOU | High | **Redesign** | Amendment 16 — atomic precondition inside `_aliases` body |
| NF-3 wait_for_status stalls on yellow indices | Medium | **Redesign** | Amendment 4 (scoped implicit wait) |
| NF-4 No WAIT FOR not red verb | Low | Defer | `WAIT FOR YELLOW` covers it; v1.1 if asked |
| NF-5 R-19 partial rollback semantics | High | **Redesign** | Amendment 18 — `partially_rolled_back` ledger state |

## Convergence Analysis

**Strong convergence (act now):**
- PM-1 + PA-5 + MD-5 + Amendment 8 — lock CAS correctness reached via three independent reasoning paths (temporal: refresh lag; performance: takeover false-positive zone; mechanism design: TTL coupling)
- MD-1 + Amendment 5 — context source-of-truth resolved by direct evidence (Open Question in artifact + lazy-path analysis)
- PM-7 + MD-4 + Amendment 2 — Yellow default unsafe in prod confirmed by both temporal failure and consumer modeling

**Weak convergence (review individually):**
- The "documentation as fix" pattern across PM-3, MD-3, PA-2, MD-9 — all four reached the same flawed conclusion via shared prior (the framework already has docs/samples, so leveraging them feels natural). IR caught it. Re-examined.
- Yellow vs Green threshold flagged independently by PM, MD — but these may share the surface observation (R-03's default is Yellow), not deep independent analysis. Convergence holds because the lazy-path failure (operator never reviews) is independently confirmable.

**Disagreements that resolved:**
- IR vs synthesis on R-29 EnvironmentProfile (resolved as `WithProductionDefaults()` extension)
- Red-Blue on PA-1 perf vs correctness (resolved as Monitor with measured budget)
- Red-Blue on R-28 multi-node CI cost (resolved by checking Testcontainers actual capability)

**Shared-prior check:** "Would a developer reading the artifact for 5 minutes notice the same thing?" Yes for MD-4 (Yellow default), Yes for MD-1 (Open Question is literally flagged), No for PM-1 (refresh-lag interaction with TTL math) — that one is genuine deep analysis. Confidence high on PM-1 / Amendment 8.

## Action plan (prioritized)

### P0 — Must land before v1 (correctness)
1. Amendment 14 — `REINDEX` injects `op_type: create` by default (PM-3)
2. Amendment 15 — `WHEN VERSION` semantic comparison (PM-9, MD-10)
3. Amendment 16 — `ALIAS SWAP` atomic precondition (NF-2)
4. Amendment 17 — Ledger forensic fields (`appliedBy`, `direction`) (NF-1)
5. Amendment 18 — Partial rollback ledger semantics (NF-5)
6. Amendment 13 — `dynamic: strict` opt-in or component-template-aware (PM-4, MD-9)
7. Amendment 8 — Lock validation + realtime GET on takeover (PM-1, PA-5, MD-5, PM-12)
8. Amendment 2 — `WithProductionDefaults()` extension; Green threshold default (MD-4, PM-7)
9. Amendment 5 — `ActiveContext` + RequireExplicit policy (MD-1)
10. Amendment 7 — SigV4 + AWS endpoint loud-fail (PM-2, PM-6, MD-6)
11. Amendment 3 + 9 — SecretMarker + log-time scrubber (MD-15)
12. Amendment 19 — Multi-node Testcontainers Compose CI as Must (PM-7, R-28)

### P1 — Land in v1 (production safety)
13. Amendment 4 — `WaitMode` enum + scoped implicit wait (MD-7, MD-11, PA-3, NF-3)
14. Amendment 6 — `UNSAFE` justification token (MD-2)
15. Amendment 10 — Startup banner (MD-13)
16. Amendment 11 — Samples (R-27 expansion)
17. Amendment 20 — Ledger refresh budget test (PA-1)
18. Re-examine MD-3, PA-2 with meta-pattern (parser enforcement, not docs)
19. PM-11 — Pin Testcontainers image by sha
20. PA-4 — Tasks API logs to DEBUG

### P2 — Defer to v1.1 (perf, ergonomics)
- PA-1, PA-6, PA-7, PA-8, PA-9, PA-10, PA-11, PA-12 (perf optimization)
- PM-8 client upgrade tracking (when OpenSearch 3.x lands)
- MD-8 JSON Schema for IDE help
- MD-12 bulk `_refresh` log-line clarity
- NF-4 `WAIT FOR not red` verb
- AWS Managed OpenSearch CI automation

### P3 — Open backlog with explicit triggers
- PM-9 long-tail semver suffixes (revisit when AWS prefix issues reported)
- PM-10 mapping drift detection (revisit if hand-edit incidents observed)
- MD-14 IF NOT EXISTS lint (revisit if ledger-wipe incidents observed)
- R-15 PRD `context` granularity beyond `RequireExplicit`/`SkipIfUnset`

## Recommendations to user

1. **Update the requirements doc** with all P0 and P1 amendments; promote ledger forensics, atomic precondition, semver, partial rollback, REINDEX safe-default, multi-node CI to Must.
2. **Replace R-29 enum proposal** with `WithProductionDefaults()` extension method — document this as a Decided item; resolve the IR's "hidden coupling" concern.
3. **Apply the meta-pattern systematically**: re-examine MD-3 (templating namespace), PA-2 (lock index settings), MD-9 (component-template injection) with parser/runtime enforcement, not docs. The test is "can a lazy path still be wrong?" — if yes, fix in code.
4. **Run `/nop:propose` next** with the updated requirements as fitness criteria. Several decisions still require evaluation across competing implementation strategies (e.g., parser-level injection vs runtime middleware for `op_type: create`; opt-in vs component-template-aware for `dynamic: strict`).

## Out of scope (confirmed during assessment)

These were explicitly evaluated and rejected from v1:
- AWS Managed OpenSearch CI automation (Should + scheduled, not Must — Amendment 19 only covers multi-node)
- Semantic detection of unsafe ops (vs syntactic enumeration) — research project deferred
- `WAIT FOR not red` verb — `WAIT FOR YELLOW` covers
- JSON Schema for `statements.json` — v1.1 IDE ergonomics
- Topology-aware bulk parallelism — v1.1 perf
- ES 7.x legacy compatibility — separate provider if demand emerges
