# ADR-0031: Test Tiers and What Gates CI

**Status:** Accepted
**Date:** 2026-08-26
**Amends:** ADR-0010 (Dual-Tier Testing Strategy)
**Related ADRs:** ADR-0029 (Ledger Wire Contract is Library-Owned)

## Context

ADR-0010 declared two tiers: MSTest unit tests, and Testcontainers integration tests against real provider containers. The declared strategy and the running system have since diverged completely.

On 2026-05-17, twelve days before the 3.1.0 release, commit `7ff3808` tagged every container-spinning class `[TestCategory("LocalOnly")]`. The stated reasons were sound: the heavy suite does not gate the NuGet publish (the release pipeline runs unit tests only), and the recurring infrastructure flakes all lived there — Couchbase GSI indexer initial-rebalance variance, and Azure Front Door blocking `mcr.microsoft.com` when an 18-cell matrix fired roughly 36 simultaneous base-image pulls.

But the CI job already filtered *negatively*: `TestCategory!=LocalOnly`. Once everything carried the tag, the filter matched nothing, and `dotnet test` exits non-zero on a no-match. Rather than reconcile the filter with the categories, the whole job was gated behind a new repo variable:

```yaml
if: ${{ vars.RUN_HEAVY_INTEGRATION == 'true' }}
```

Three things followed, none of them intended:

- **The variable was never created.** The repository has exactly one Actions variable, `SOLUTION_NAME`. The condition is false by absence.
- **The documented way back does not work.** Both the workflow comment and the commit message say re-enabling needs "no code change". It does: the job it re-enables still carries `TestCategory!=LocalOnly`, so setting the variable to `true` un-skips a matrix whose every cell matches zero tests and fails on the no-match — reproducing precisely the failure the gate was added to avoid. Measured, not inferred.
- **A negative filter cannot fail loudly.** `TestCategory!=LocalOnly` is indistinguishable, at read time, from a filter that is working. It reports the same way whether the suite is passing, empty, or entirely untagged.

The result was zero integration coverage in CI with no working path back, and it is not hypothetical: both defects recorded in ADR-0029 — the OpenSearch `_mget` index inference that broke every run, and the MongoDB squash filter that silently matched nothing — shipped through that gap.

There is also a tier ADR-0010 never named. Neither of those defects needs a container to catch: both are visible from the real client and real serializer over a faked transport, in milliseconds. That is where they should have been caught, and it is the cheapest tier in the suite.

## Decision

Three tiers, with explicit membership rules and positive selection in CI.

### Tier 1 — Unit

No I/O of any kind. Runs on net8.0, net9.0, net10.0 on every PR. `Hyperbee.Migrations.Tests`, `Hyperbee.Migrations.Squash.Tests`, `Hyperbee.Migrations.Cli.Tests`.

### Tier 2 — Wire

Real provider client, real serializer, real request construction; only the transport is faked (`InMemoryConnection` for OpenSearch; rendered filters and serializer output for MongoDB and Couchbase). No network, no container. Lives in the unit projects and runs on all three target frameworks with Tier 1.

This is where request shape, field naming, and type inference are asserted — the ADR-0029 defect class. Per ADR-0029 Rule 3 every provider carries wire tests for its ledger operations.

### Tier 3 — Integration

Real containers. Split by cost, and every class carries **exactly one** of:

**`[TestCategory("Gating")]`** — runs on every PR. To qualify, a class must:

1. use only the shared provider container from the `InitializeTestContainers` assembly fixture;
2. build no Docker image (no `*MigrationContainer.BuildMigrationImageAsync`, no CLI binary spawn) — that is what pulls from MCR;
3. not be multi-node;
4. complete its provider's whole cell in about a minute; and
5. have been observed green before it is promoted.

**`[TestCategory("LocalOnly")]`** — everything else. Runs on demand via `heavy_integration_tests.yml`, for any provider and target framework.

**`[TestCategory("Flaky")]`** — excluded from automation entirely; runs only locally or on manual dispatch. Reserved for tests that assert on a race and can fail with no defect present. Three exist today: the `Should_Fail_WhenMigrationHasLock` test in the Aerospike, MongoDB and Couchbase runner suites, which starts concurrent runner containers and requires one to observe lock contention. On a fast host they all finish first. Aerospike and MongoDB were measured failing this way; the Postgres equivalent had already been commented out by an earlier author for the same reason. This is recorded as debt, not coverage — the fix is to hold the lock deterministically instead of racing containers.

Every integration class was measured against these criteria rather than assumed. The result:

| Cell | Gating tests | Local duration |
| --- | --- | --- |
| opensearch | 88 | 1m02s |
| mongodb | 10 | 1m04s |
| postgres | 9 | 32s |
| aerospike | 6 | 29s |
| multi-provider | 2 | 6s |
| **total** | **115** | cells run in parallel |

Excluded, with the reason measured rather than inherited:

- **5 runner classes + `CliBinaryEndToEndTests`** (10 tests) build Docker images. Criterion 2.
- **2 multi-node classes** (6 tests) need three OpenSearch JVMs. Criterion 3.
- **3 Couchbase squash classes** (6 tests) pass, but take **5–6.5 minutes** — five to six times every other cell combined — because `IsolatedCouchbaseContainer` waits out the GSI indexer's initial rebalance, for which the code allows up to 12 minutes. Criterion 4. Note this is the *only* exclusion on speed, and that Couchbase has no cheap alternative: every one of its integration classes either builds an image or uses the isolated container.

The Gating matrix runs one target framework. These tests assert behavior against a real server, which does not vary by TFM, and Tiers 1 and 2 already cover all three.

### Three triggers, one purpose each

| Trigger | Runs | Wall clock | Failure means |
| --- | --- | --- | --- |
| Pull request | Tiers 1 + 2, all TFMs; Gating tier, net10.0 | ~2 min | Do not merge |
| Manual dispatch | The `LocalOnly` suite minus `Flaky`, any provider/TFM | ~10 min | Investigate; possibly a re-run |

A push-to-`main` trigger was built for the heavy suite and then removed before shipping. The reasoning for it stands -- "run them locally" is a convention and conventions do not catch regressions -- but measuring the suite before automating it showed it is not dependably green (see Consequences). Automating a suite that is not dependably green produces a signal people learn to ignore, and an ignored signal is worse than an absent one, which is the same argument this ADR makes for keeping the PR gate small. The trigger is three commented lines in `heavy_integration_tests.yml`, to be enabled once the intermittency is fixed.

### CI selects positively, and proves the selection is non-empty

The gating job filters `TestCategory=Gating`, never `TestCategory!=...`. A guard step runs `--list-tests` with the same filter before any container starts and fails with a named error if it matches nothing:

```
::error::Gating filter matched zero tests for opensearch: (FullyQualifiedName~OpenSearch)&TestCategory=Gating
```

Losing coverage now costs a red build, not silence. Deleting the last gating test for a provider is a decision someone has to make explicitly.

### The heavy suite gets a trigger that works

`RUN_HEAVY_INTEGRATION` is removed. `heavy_integration_tests.yml` runs the `LocalOnly` suite on every push to `main` and on demand, selecting positively on `TestCategory=LocalOnly` so it runs exactly what the PR gate does not. It keeps the MCR warm step, since it does build images.

## Consequences

**Positive:**
- Integration coverage gates PRs again — 115 of the repository's 136 integration tests, across five cells, all verified green, in about two minutes.
- Every one of the repository's 136 integration tests is accounted for: 115 on every PR, 12 on every merge to `main`, 6 multi-node on manual dispatch, and 3 quarantined as `Flaky` with the reason recorded. Nothing is silently skipped.
- No Docker image builds in the PR path, so the MCR/AFD failure class is removed there rather than mitigated.
- The zero-match trap cannot silently recur.

**Negative:**
- **The heavy suite is still manual, and it is not dependably green.** Measured before automating it, rather than discovered afterward on `main`:
  - `AerospikeRunnerTest.Should_Succeed_WhenRunningUpTwice` failed in a batch run (52s) and passed standalone (12s) minutes later. Intermittent; cause not isolated, plausibly container-teardown timing from the preceding provider.
  - `Should_Fail_WhenMigrationHasLock` in the Aerospike, MongoDB and Couchbase runner suites asserts on a race: it starts concurrent runner containers and requires one to observe lock contention, which does not happen on a fast host. Now quarantined as `Flaky`. The Postgres equivalent had already been commented out by an earlier author -- the same conclusion, reached silently.
  
  So the position is honest but unfinished: the trigger exists and works, the suite does not yet deserve it. Fixing the two issues -- deterministic lock acquisition instead of a container race, and isolating the Aerospike intermittency -- is what unlocks post-merge automation.
- **A Couchbase regression against a live cluster is caught one merge late, not pre-merge.** This is the single real coverage difference the split creates. What is still caught pre-merge for Couchbase: everything in Tiers 1 and 2, including `CouchbaseLedgerWireTests`, which pins the N1QL field names against the ledger serializer's actual output — the ADR-0029 defect class. What is not: squash determinism and verification against a real cluster. Closing it pre-merge means paying 5–6.5 minutes on every PR, or making `IsolatedCouchbaseContainer`'s startup cheaper; the second is the real fix if this ever bites.
- **The `LocalOnly` suite is only as good as someone remembering to dispatch it.** That is the weakness the post-merge trigger was meant to remove, and it remains open until the suite is stable enough to automate. Image pulls and Couchbase warmup mean even a fixed suite will go red for infrastructure reasons sometimes, which is survivable on a manual or post-merge run and would not be pre-merge.
- One TFM in the integration tier means a genuinely TFM-specific driver behavior against a live server would be missed. Judged remote, and Tiers 1 and 2 cover all three.

**Neutral:**
- ADR-0010's two-tier framing is amended, not superseded: unit and Testcontainers integration both remain. This names the wire tier it omitted and splits integration by cost.
- The release pipeline is unchanged — it still runs unit tests only. Gating tests gate the PR, not the publish.

## Alternatives Considered

- **Set `RUN_HEAVY_INTEGRATION=true`** — rejected; it does not work. Verified: `(FullyQualifiedName~OpenSearch)&TestCategory!=LocalOnly&TestCategory!=MultiNode` matches zero tests today.
- **Fix the filter and re-enable the whole suite** — rejected. It re-imports every reason the suite was disabled: image builds, the MCR burst, Couchbase GSI variance, 18 cells on a 4-CPU runner. Trading silence for flake is not an improvement; a flaky gate gets ignored and then bypassed.
- **Leave it off and rely on local runs** — rejected. That was the status quo, and it cost two shipped defects in one release. "Run it locally" is a convention, and conventions do not gate merges.
- **Keep the negative filter but add the guard step** — rejected as half a fix. The guard would catch an empty run, but `TestCategory!=LocalOnly` still cannot express *which* tests are meant to gate, so any new class silently joins the PR path with whatever cost it carries.
- **Delete the integration project from CI entirely and be honest about it** — rejected. The defects that shipped were exactly the kind only a real server catches; the answer is a small trustworthy tier, not none.

## References

- [`run_tests.yml`](../../.github/workflows/run_tests.yml) — gating tier and guard step
- [`heavy_integration_tests.yml`](../../.github/workflows/heavy_integration_tests.yml) — manual heavy suite
- [`multi_node_tests.yml`](../../.github/workflows/multi_node_tests.yml) — multi-node, manual
- ADR-0010 (amended), ADR-0029 (Rule 3 — the wire tier)
