# v3.0 Release Readiness Assessment

**Date:** 2026-05-12
**Branch:** `devs/bfarmer/provider-squash`
**Commits in scope:** `b5de226..206e287` (23 commits)
**Method:** `/nop:assess` — Pre-mortem + Mechanism Design + Performance Audit discovery, Red-Blue convergence, Independent Review, Red-Blue₂.
**Verdict:** **NOT READY FOR v3.0.0 TAG. Path A locked 2026-05-12.** Five release-blocking findings + 17 Redesign items (+1 post-assessment addition for OpenSearch ISM lifecycle). Tag after the blocking set is closed AND the integration suite reflects the marketing claim. Estimated 3-4 weeks of focused work.

**Path A vs Path B decision:** Path A locked. The CLI ships as a thin dispatch shell over an `ISquashProvider` extensibility contract — no hardcoded provider list in the CLI assembly. Each provider package implements the contract; CLI discovers via assembly reference-closure scan. Third-party providers consume the same surface as the 5 first-party providers. The marketing claim "all 5 providers ship" is honored at all tiers (library, CLI, integration tests).

---

## TL;DR

The strategy libraries for all 5 providers shipped clean and contract-correct (zero ADR-0019 amendments across 4 non-Postgres providers — strong evidence the abstraction is sound). However, the **CLI surface was never updated past v1 (Postgres-only)** and contradicts the v3.0 marketing claims in CHANGELOG.md, Program.cs, and README. The "all 5 providers" claim is true at the library tier and was false at the CLI tier.

Path A is locked: the CLI is being rewritten as a thin dispatch shell over an `ISquashProvider` contract that each provider package implements. CLI discovers providers via assembly reference-closure scan — no hardcoded provider list. This unifies the 5 first-party providers and any future third-party provider under the same extensibility surface. Plus 17 Redesign items + safety-default flips + the OpenSearch ISM lifecycle DSL completion (R-17) ship together.

Estimated 3-4 weeks single-developer focused work to tag.

The library-tier work is solid. The CLI tier is being rewritten properly, not patched.

---

## Triage

**Artifact type:** Architecture + Plan/Roadmap + Code/Implementation hybrid.

| Skill | Value | Rationale |
|---|---|---|
| Pre-mortem | High | Multi-year release lifetime; squash determinism depends on multiple versioned assumptions. |
| Mechanism Design | High | 10+ consumer-facing surfaces. |
| Performance Audit | Medium | Runtime additions on host boot + per-RunAsync ledger queries. |

**Selected:** all three. **Skipped:** none.

---

## Consumer enumeration (Phase 2 Synthesis check)

1. **Operators** — deploy migrations in production; highest blast radius from defaults.
2. **Library consumers** — .NET app developers wiring DI; want "just works" defaults.
3. **Future provider authors** — implementing a 6th provider.

**0 conflicting non-negotiable pairs.** Findings are bug fixes / contract gaps, not stakeholder priority conflicts. **Synthesis skipped (justified).**

---

## Findings — consolidated verdict table

### RELEASE BLOCKING (must fix before v3.0.0 tag)

| ID | Finding | File:Line | Verdict | Action |
|---|---|---|---|---|
| **RB-1** (PM-1/MD-4) | CLI hard-refuses non-Postgres providers; CHANGELOG/Program.cs/README claim "all 5 providers" | `runners/Hyperbee.Migrations.Cli/Verbs/SquashVerb.cs:49-62`<br>`runners/Hyperbee.Migrations.Cli/Program.cs:67`<br>`CHANGELOG.md:26-37` | **Redesign** | Either (a) wire CLI to all 5 providers' strategies + dispatch on `--provider` value, OR (b) downgrade CHANGELOG/Program.cs/README to call non-Postgres "preview" and keep CLI refusing. Option (b) is faster; option (a) is what the locked release rule actually requires. |
| **RB-2** (MD-15) | `recover from-mid-range` is audit-only; prints "acknowledgement valid" to stdout but persists nothing | `runners/Hyperbee.Migrations.Cli/Verbs/RecoverVerb.cs:67-86` | **Redesign** | Persist the acknowledgement to the record store so the runner can read it on next invocation, OR exit non-zero with "validation only — recovery requires direct ledger mutation." Current state misleads operators under incident pressure. |
| **RB-3** (NEW) | `FleetReadinessCheck` hardcodes `public.migrations` table — operator using non-default `SchemaName` gets silently-wrong fleet classification | `runners/Hyperbee.Migrations.Cli/Postgres/FleetReadinessCheck.cs:89-92,105` | **Redesign** | Read `SchemaName` + `TableName` from `PostgresMigrationOptions`. Currently any non-default schema returns 0 from the ledger probe → every fleet env classified as fresh install → mid-range gate becomes a no-op. |
| **RB-4** (NEW) | `PostgresEphemeralCapture` requires undocumented `ApplyToDataSourceAsync` static method on migration assembly; throws `NotSupportedException` otherwise; inline comment admits "Caller-supplied applyMigrations delegate is a TODO for v1.0" | `runners/Hyperbee.Migrations.Cli/Postgres/PostgresEphemeralCapture.cs:41-48`<br>`runners/Hyperbee.Migrations.Cli/Verbs/SquashVerb.cs:217-235` | **Redesign** | Either implement the delegate path the TODO names, OR document the precondition prominently in CLI help + operator guide + CHANGELOG. Currently operators hit an obscure exception after typing the documented command. |
| **RB-5** (PM-2 per IR) | "All 5 providers" CHANGELOG claim is unsupported by CI evidence: Couchbase squash integration tests are `[TestCategory("LocalOnly")]` and the nightly CI is documented-failing | `tests/Hyperbee.Migrations.Integration.Tests/CouchbaseSquashDeterminismTests.cs`<br>`CHANGELOG.md:26-37`<br>(memory: `project_nightly_integration_failing.md`) | **Redesign** | Either ship the Couchbase integration tests via the sibling-container model (the 3.0.1 follow-up brought forward), OR downgrade the CHANGELOG claim for Couchbase to "preview" and document why. Both Independent Review and Pre-mortem flagged this; convergence is strong. |

### REDESIGN (should fix before tag; high-confidence with cheap fixes)

| ID | Finding | Verdict | Action |
|---|---|---|---|
| **R-1** (MD-2) | `MigrationOptions.LockingEnabled = false` default — production-grade defaults must protect against the lazy path | Redesign | Default to `true`; document `false` as an explicit dev-mode opt-out. |
| **R-2** (PA-1) | Per-migration `ExistsAsync` loop after bulk `IntersectWithApplied` already ran — 500 RTTs while holding the fleet lock | Redesign | Use the existing `applied` set from line 86; remove the per-id loop at line 120. |
| **R-3** (PA-3/MD-6) | `IntersectWithSquashedAsync` DIM-default returns empty silently — fail-open on a correctness gate | Redesign | Throw `NotSupportedException` the first time a `Kind=Squash` row is encountered AND the override hasn't been provided; document the upgrade path. Fail-loud, not fail-silent. |
| **R-4** (MD-13) | `--remove-originals` regex matches unrelated files (`User_1000_Backup.cs` matches when squashing 1000) | Redesign | Default to `--dry-run`; require `--confirm-delete` to actually delete. List matched files first. |
| **R-5** (MD-10) | CLI hardcodes `.sql` extension; non-Postgres providers emit `CanonicalJson` | Redesign | Drive extension from `Generated.Kind` (`ContentKind.SqlText` → `.sql`; `CanonicalJson` → `.json`; etc.). Required as part of RB-1's CLI fix. |
| **R-6** (MD-12) | `--scan-source` is opt-in despite ADR-0019 A5 being default-deny | Redesign | Make `--scan-source <path>` required unless `--no-scan` with a reason flag is supplied. The annotation contract is the safety gate. |
| **R-7** (PM-11) | `--fleet-manifest` optional; two-phase gate degrades to zero-phase when omitted | Redesign | Make required unless `--no-fleet-manifest` with a reason is supplied. Warn loudly when omitted. |
| **R-8** (NEW) | `SquashVerb.cs:98` calls `PostgresMigrationSourceScanner.Scan` regardless of `--provider`; hidden today by RB-1 refusal | Redesign | Dispatch to the correct provider's scanner (`AerospikeMigrationSourceScanner` / `OpenSearchMigrationSourceScanner` / etc.) based on `--provider` value. Part of the RB-1 fix. |
| **R-9** (PM-9 per IR) | `RegisterBaseAliases.RemoveAll` destroys user-supplied custom registrations (test harness footgun) | Redesign | Track descriptor source so only the helper-installed legacy aliases are removed; preserve user-supplied registrations. OR document loudly. |
| **R-10** (MD-5) | `MidRangeSquashException` doesn't print the recovery token (per `RecoveryAcknowledgement.cs:60-61`, token is not a secret) | Redesign | Include the computed token in the exception message so operators have it during incident response. Trivial. |
| **R-11** (MD-3) | README quick-start uses `GetRequiredService<MigrationRunner>()` — the unsafe pattern multi-provider hosts throw on | Redesign | Single-provider quick-start can use base; add an explicit warning + "for multi-provider hosts use `{Provider}MigrationRunner` directly." Cross-link the operator guide. |
| **R-12** (MD-7) | `ArgParser` silently accepts typoed flags (`--conneciton`) and treats a flag-followed-by-flag as `value="true"` | Redesign | Whitelist known flags per verb; reject unknown with "did you mean?" suggestion. Required-flag missing-value is an error, not a silent `"true"`. |
| **R-13** (MD-8) | Fleet manifest `IgnoreUnmatchedProperties()` silently swallows YAML typos | Redesign | Remove or whitelist known keys; bail with the actual typo line/column. |
| **R-14** (MD-9) | `CouchbaseMigrationOptions.BucketName = null` default; failure is in the Couchbase SDK, not our diagnostic | Redesign | Validate required fields in `AddCouchbaseMigrations` and throw with operator-friendly message naming `opts.BucketName = "..."`. |
| **R-15** (PM-5) | Aerospike `IntersectWithSquashedAsync` doc/code divergence: CHANGELOG says DIM-empty; code at `AerospikeRecordStore.cs:309-361` implements it | Redesign | Fix CHANGELOG to match shipped code OR add coverage to confirm the implementation is correct. |
| **R-16** (PA-5) | Couchbase `IntersectWithAppliedAsync` fan-out — N parallel `ExistsAsync` per candidate; opens 500 concurrent KV ops; throttle/retry storm risk | Redesign | Replace with N1QL `USE KEYS [...]` single round trip. |
| **R-17** (POST-ASSESSMENT) | OpenSearch DSL ISM lifecycle is incomplete: `DROP POLICY <name>` and `DETACH POLICY FROM INDEX <pattern>` verbs missing. Classifier already recognizes `DROP POLICY` (`OpenSearchDataOpClassifier.cs:63`) but parser rejects it — internal inconsistency. Two consumer PRs document the gap (Billing repo asserts the policy persists post-Down because rollback can't clean up). Without `DETACH` before `DROP`, an attached policy refuses deletion at the OpenSearch API. | Redesign | Add both verbs as a pair (DETACH first, then DROP — that's the rollback order). Grammar rule, AST nodes (`DetachPolicyStatement`, `DropPolicyStatement`), dispatcher handlers using `IsmEndpointCapability` prefix detection, `OpenSearchStatementKind` enum additions, parser tests, one round-trip integration test (`CREATE → APPLY → DETACH → DROP`), CHANGELOG entry, `docs/site/opensearch.md` Statement reference update. **Estimate ~1 day** including shared test fixture. **Non-optional for v3.0** per user direction 2026-05-12: two consumer-team PRs flag the rollback gap; shipping v3.0 without ISM lifecycle completeness leaves real operator debt. |

**REDESIGN total:** 17 items. ~5-6 days of focused engineering after the cascade is unblocked.

### MONITOR (real risk; acceptable with documentation + alerting)

| ID | Finding | Verdict | Action |
|---|---|---|---|
| **M-1** (PM-3/PM-4/PM-7) | Canonicalizer ephemeral catalog is closed; server-side feature evolution can introduce new fields → silent determinism erosion | Monitor | The verifier byte-equality round IS the gate; document the failure mode (operator sees "verification failed" + diff summary). Add a release-note about server-version pinning. |
| **M-2** (PA-6) | Lock-hold duration linear in migration count; 1000-migration runs serialize the fleet for the duration | Monitor | Document the scaling ceiling. Lock heartbeat / per-migration locks would require architectural change; defer to a future release. |
| **M-3** (PA-10) | MongoDB lock has no fencing token; GC pause longer than `LockMaxLifetime` allows second runner to claim and race | Document acceptance | Existing v2 behavior; not a v3.0 regression. Add to "known limitations" in upgrade guide; recommend `LockMaxLifetime` >> any expected GC pause. |
| **M-4** (PA-4) | Aerospike `IntersectWithSquashedAsync` is full-namespace scan (no secondary index on `Replaces`) | Monitor | Real but bounded (few squashes per project). Document as scaling consideration. |
| **M-5** (PA-7) | Canonicalizer 3× memory copy + `Indented=true` for large snapshots | Monitor | Operator-initiated, not hot path. Real but bounded. Document the size ceiling. |
| **M-6** (PA-9) | `pg_dump` output buffered as managed string; large schemas can land on LOH | Monitor | Operator-initiated. Document the size limit. |

### DOCUMENT (no code change; v3.0 release notes)

| ID | Finding | Action |
|---|---|---|
| **D-1** (PM-8) | v3 → v2 downgrade is undefined | Add downgrade warning to upgrade guide. |
| **D-2** (PM-10) | `RecoveryAcknowledgement` 48-bit truncation is anti-typo, not anti-security | Surface in operator guide + RecoverVerb help. |
| **D-3** (MD-1/MD-14) | `[Migration(v, "name")]` 2-arg ambiguity binds StartMethod not Profile | Add CHANGELOG callout. Schedule Roslyn analyzer for v3.0.1 and breaking-ctor fix for v4. |
| **D-4** (MD-11) | Empty `Profiles = []` semantically means "include all" | Add CHANGELOG callout naming the semantic. |
| **D-5** (NEW) | "All 5 providers" CHANGELOG claim should reference actual CI coverage state | Add note describing which providers have unit + integration vs unit-only-with-LocalOnly-deferred. |

### DEFER (acceptable for v3.0; revisit in v3.0.1+)

| ID | Finding | When to revisit |
|---|---|---|
| **F-1** | Couchbase squash integration tests LocalOnly | 3.0.1 sibling-container test architecture |
| **F-2** (PA-8) | `IsLedgerEmptyAsync` sends all ids when only `Count==0` is needed | 3.0.1 if monitoring shows BSON size pressure |
| **F-3** (NEW) | `Assembly.LoadFrom` in CLI doesn't unload | 3.0.x if long-running CLI servers become a use case |

### ACCEPT (no action)

| ID | Finding | Why |
|---|---|---|
| **A-1** (PM-12) | "5 providers without amendment" is pseudo-independence | True observation; can't preempt without an actual 6th provider author. |

---

## Convergence Analysis

**Strong convergence (highest confidence — different reasoning paths arriving at the same finding):**

| Cluster | Discovery paths | Verdict |
|---|---|---|
| CLI gating (RB-1) | PM (assumption decay: marketing claim ≠ ship) + MD (lazy path: operator runs squash) | Strong convergence; Independent Review confirmed |
| Lock semantics (R-1 + M-2 + M-3) | MD (lazy default) + PA (concurrency hazard + lock-hold duration) | Strong convergence |
| IntersectWithSquashed DIM (R-3) | MD (lazy path skip override) + PA (silent fail-open on correctness gate) | Strong convergence |
| Determinism erosion (M-1) | PM × 3 independent failure chains (assumption decay, dependency, slow-burn) | Strong convergence |
| Optional safety controls (R-6 + R-7) | PM-11 + MD-12 | Strong convergence |

**Weak convergence (single-source but file-verifiable):** RB-2 (recover verb), RB-3+RB-4 (Independent Review new findings — re-confirmed by IR's direct codebase read).

**Disagreement (Red-Blue₂ resolution):**
- PM-9 escalated to R-9 (DI test footgun real)
- PM-2 escalated to RB-5 (claim vs evidence contradiction)
- PA-10 deescalated to M-3 (pre-existing v2 behavior, not v3 regression)
- MD-1/MD-14 stays at Defer but add D-3 release-note
- MD-11 changed Monitor → Document (active surface)
- PA-8 changed Redesign → Defer (low ROI)

---

## Recommended sequencing before tag (Path A — locked 2026-05-12)

**Path B (downgrade marketing claim) was considered and rejected.** The locked release rule "all 5 providers ship or do not ship" is honored. CLI is wired for all 5 providers via an extensibility contract (`ISquashProvider`) — no hardcoded provider list in the CLI assembly.

### Architecture decision (2026-05-12): CLI as thin dispatch shell

The CLI references **zero** provider packages directly. It dispatches via the `ISquashProvider` contract that each provider package implements. Discovery is via assembly scan of the migration project's reference closure. NuGet package presence IS the registration — no `register-provider` / `unregister-provider` commands. Third-party providers consume the same extensibility surface as the 5 first-party providers.

### Phased plan (4 weeks single-developer focused)

**Week 1 — Independent fixes that unblock CLI work:**
- RB-2 — `recover from-mid-range` persistence decision + implementation (~0.5-2 days)
- RB-3 — `FleetReadinessCheck` reads schema/table from options (~0.5 day after the CLI cascade exposes the per-provider FleetReadinessCheck shape)
- RB-4 — Document the apply-path convention as part of the new `ISquashProvider` contract (the apply path moves into the contract — dissolves as a release-blocker)
- R-1 — Default `LockingEnabled = true` (~2 hours)
- R-6 — `--scan-source` required (~0.5 day)
- R-7 — `--fleet-manifest` required or warn-loud (~0.5 day)
- R-10 — `MidRangeSquashException` prints recovery token (~15 min)
- R-11 — README quick-start fixes (~15 min)
- R-12 — ArgParser whitelist (~0.5 day)
- R-13 — Fleet manifest schema whitelist (~2 hours)
- R-14 — Couchbase `BucketName` validation (~1 hour)
- R-15 — Aerospike CHANGELOG/code consistency (~1 hour)
- R-17 — OpenSearch ISM lifecycle DSL completion (DROP POLICY + DETACH POLICY FROM INDEX). Independent of CLI cascade; ~1 day.
- R-9 — `RegisterBaseAliases` preserve user registrations (~0.5 day)
- R-2 — Remove per-migration `ExistsAsync` loop (~0.5 day including tests)
- R-3 — `IntersectWithSquashedAsync` DIM throws (~0.5 day including tests)

Week 1 total: ~6-8 days of work spread across the surface. Closes 4 release-blockers (or near-blockers) and 14 Redesigns.

**Week 2 — CLI extensibility contract + Postgres reference:**
- Day 1: Define `ISquashProvider`, `IEphemeralProvisioner`, `EphemeralFixture`, `IEphemeralFixtureRequest` in core. Build `SquashProviderRegistry.Discover` (reference-closure scan). Build `TestcontainersEphemeralProvisioner` skeleton.
- Day 2-3: Migrate existing Postgres CLI code (`runners/Hyperbee.Migrations.Cli/Postgres/`) into `PostgresSquashProvider` in the Postgres provider package. CLI csproj loses Postgres-specific references. Verb dispatcher becomes single dictionary lookup.
- Day 4-5: Aerospike `ISquashProvider` implementation + `AerospikeFixtureRequest` + Testcontainers handler. Integration test.

**Week 3 — OpenSearch + MongoDB:**
- Day 1-3: OpenSearch `ISquashProvider` + `OpenSearchFixtureRequest` + handler. Per-provider FleetReadinessCheck (RB-3 for OpenSearch). Integration test.
- Day 4-5: MongoDB `ISquashProvider` + handler + FleetReadinessCheck (RB-3 for MongoDB). Integration test.
- R-16 — Couchbase `IntersectWithAppliedAsync` rewrite to N1QL `USE KEYS` (~0.5 day, fits here).

**Week 4 — Couchbase (sibling-container) + RB-5 + verify + tag:**
- Day 1-3: Couchbase `ISquashProvider` with sibling-container provisioner variant. The Couchbase fixture request asks the provisioner for a sibling-container fixture (because host-side connection conflicts with the existing `CouchbaseRunnerTest`). Integration test. Closes RB-5 + F-1.
- Day 4: D-1 through D-5 documentation. M-1 through M-6 release notes.
- Day 5: Full multi-target test pass (net8/9/10). Plan/CHANGELOG closing. `dotnet pack` dry run. PR to main. CI green. Tag v3.0.0.

### Total

**16-20 working days** (3-4 weeks single-developer focused). +1 day for R-17 already absorbed into Week 1.

---

## Quality assessment summary

| Dimension | Rating | Evidence |
|---|---|---|
| **Library tier (Squash codegen)** | **High** | 5/5 provider contracts shipped; ADR-0019 unamended; 846 squash unit tests + 99 integration tests passing |
| **Library tier (Multi-runner DI)** | **High** | ADR-0023 unamended; 21 multi-runner tests; live two-provider integration test 2/2 |
| **Library tier (Record store + interface evolution)** | **Medium** | DIM-default fail-silent on `IntersectWithSquashed` (R-3) is a correctness gap; otherwise solid |
| **CLI tier** | **Low** | Frozen at v1 (Postgres-only) despite library tier shipping all 5 providers. Multiple "v1 TODO" land mines (RB-3, RB-4). The CLI is the gap |
| **Doc accuracy** | **Medium-Low** | Critical inconsistency: "all 5 providers ship" claim vs CLI refusal (RB-1). After fix: high |
| **Default safety** | **Medium-Low** | `LockingEnabled=false`, `--scan-source` opt-in, `--fleet-manifest` optional, `--remove-originals` lacks dry-run. Each individually correctable; collectively shows a permissive default posture inconsistent with "production-grade" framing |
| **Determinism + verification** | **High** | Verifier byte-equality round IS the gate; canonical-form ephemeral strip catalog is correct for known fields; future-server fields surface as verifier failures, not silent drift |
| **Test coverage** | **High** | 1,247 unit tests across 3 targets; 99/100 integration with 1 known pre-existing skip. 2 production bugs found + fixed during Phase 5 audit |
| **Architecture decisions** | **High** | ADR-0019 unamended across 5 providers; ADR-0023 multi-runner converged cleanly; ADR-0020/0021/0022 all Accepted |

---

## Final recommendation (locked 2026-05-12)

**Path A is the path.** "All 5 providers ship or do not ship" is honored. The CLI is rewritten as a thin dispatch shell over the new `ISquashProvider` extensibility contract. No hardcoded provider list. Third-party providers (a future Cassandra, DynamoDB, etc.) consume the same surface.

The 17-item Redesign cleanup ships with the CLI rewrite. R-17 (OpenSearch ISM lifecycle DSL completion — `DROP POLICY` + `DETACH POLICY FROM INDEX`) is non-optional per consumer-team PR demand.

Timeline: 3-4 weeks single-developer focused. Week 1 closes most of the independent Redesigns + the safety-default flips + R-17. Weeks 2-4 build out the per-provider `ISquashProvider` implementations (Postgres reference first, then Aerospike, OpenSearch, MongoDB, Couchbase-with-sibling-container) + per-provider FleetReadinessCheck + per-provider integration test.

Tag v3.0.0 after the full plan completes and all suites are green.

The library work is high-quality. The CLI tier is the work that remains. Path A makes it match.
