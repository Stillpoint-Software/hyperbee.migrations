# Design: OpenSearch Provider — Pragmatic Hybrid Architecture

**Status:** Proposed
**Date:** 2026-05-02
**Requirements:** [docs/requirements/opensearch-provider.md](../requirements/opensearch-provider.md)
**Research:** [docs/research/0001-opensearch-provider.md](../research/0001-opensearch-provider.md)
**Assessment:** [docs/research/0002-opensearch-provider-assessment.md](../research/0002-opensearch-provider-assessment.md)

## Selected Approach

**Pragmatic Hybrid.** Parser owns *intent* (AST enrichment, syntactic safety detection, grammar-level safe-default flags); runtime owns *execution* (request-body merge, observability, secret scrubbing, response handling). The bootstrapper presents a Couchbase-style state-machine *façade* over an internal `IBootstrapStep[]` pipeline — simple external contract, testable internal composition. Lock and ledger indices are always-created during `InitializeAsync` with an explicit `AssumeIndicesExist` opt-out for tightly-scoped IAM contexts.

## Fitness Evaluation Summary

| Candidate | Req. Compliance | ADR Compliance | Temporal | Interface | Scale | Design | Overall |
|-----------|----------------|----------------|----------|-----------|-------|--------|---------|
| A: Couchbase-Clone (runtime middleware only, full state machine, always-create) | ~85% | ✓ all | Medium | Medium | Medium | Moderate | Moderate |
| B: Parser-First Composition (parser-only, pipeline-only, provision-on-demand) | ~82% | ✓ all | High | Small | High | Clean | Moderate |
| **C: Pragmatic Hybrid** | **~96%** | ✓ all | High | Small | High | Clean | **Strong** |

C dominates because the requirements *force* a hybrid: R-08a (`op_type: create` injection), R-17 (component-template-aware `dynamic: strict`), and R-18 (parse-time syntactic unsafe-op detection) all require parser-level work; R-25 (structured event emission) requires runtime work. Pure runtime (A) loses parse-time error message contracts; pure parser (B) cannot observe live request/response. Hybrid is the only architecture that satisfies both classes natively.

**Note (post-Phase-0):** R-10 (Hyperbee.Templating renderer) was struck per [ADR-0016](../decisions/0016-no-file-level-templating.md) — env-variation flows through typed options, matching the other four providers. The architecture below has been amended to remove the Templating Renderer block and the SecretScrubberSink that depended on it. The hybrid argument still stands on the parse-time-detection / runtime-middleware split.

## Architecture

### Component sketch

```
┌─────────────────────────────────────────────────────────────────────────────┐
│ Application                                                                  │
│   services.AddOpenSearchMigrations(opts => { ... })                          │
│           .WithProductionDefaults()                  ← (extension method)    │
└─────────────────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│ MigrationRunner (core, ADR-0003)                                             │
│   InitializeAsync → CreateLockAsync → discover → run → journal               │
└─────────────────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│ OpenSearchRecordStore : IMigrationRecordStore                                │
│   ┌──────────────────────────────────────────────────────────────────┐       │
│   │ OpenSearchBootstrapper (state-machine façade)                    │       │
│   │   ┌──────────────────────────────────────────────────────┐       │       │
│   │   │ IBootstrapStep[] pipeline (DI-registered)            │       │       │
│   │   │  • RestPingStep                                      │       │       │
│   │   │  • ClusterHealthStep (uses R-03 threshold)           │       │       │
│   │   │  • EndpointCapabilityStep (AWS detection — R-21)     │       │       │
│   │   │  • LedgerIndexInitStep (R-06 strict mapping)         │       │       │
│   │   │  • LockIndexInitStep (number_of_replicas: 0 — R-04)  │       │       │
│   │   │  • SacrificialQueryStep (warmup)                     │       │       │
│   │   └──────────────────────────────────────────────────────┘       │       │
│   └──────────────────────────────────────────────────────────────────┘       │
│   ┌──────────────────────────────────────────────────────────────────┐       │
│   │ LockHandle : IDisposable (auto-renew per R-05)                   │       │
│   │   • CAS via if_seq_no/if_primary_term                            │       │
│   │   • Heartbeat timer (LockRenewInterval)                          │       │
│   │   • Realtime GET on takeover (NF-1, PM-1)                        │       │
│   │   • CancellationToken cancelled on LockMaxLifetime (PM-12)       │       │
│   └──────────────────────────────────────────────────────────────────┘       │
└─────────────────────────────────────────────────────────────────────────────┘
                              │
                              ▼
┌─────────────────────────────────────────────────────────────────────────────┐
│ Statement Pipeline                                                           │
│   (Per ADR-0016: no file-level templating renderer — resource files are     │
│    consumed by the Parlot parser directly. Env-variation is handled by      │
│    typed OpenSearchMigrationOptions + IConfiguration.)                       │
│                                                                              │
│   ┌──────────────────────────────────────────────────────────────────┐       │
│   │ Parlot Statement Parser (PARSE-TIME — R-08, R-09)                │       │
│   │   • Verb grammar (R-08a)                                         │       │
│   │   • Sibling $body resolution                                     │       │
│   │   • Reserved namespace policy (MD-3)                             │       │
│   │   • Syntactic unsafe-op enumeration (R-18)                       │       │
│   │   • UNSAFE("...") / NO WAIT("...") justification token check     │       │
│   │   • Semantic version comparator (R-15a)                          │       │
│   │   • AST nodes carry safe-default flags:                          │       │
│   │       - op_type:create=true (REINDEX)                            │       │
│   │       - dynamic:strict=auto (CREATE INDEX, skip on composed_of)  │       │
│   │   • MIGRATE INDEX composite (R-30) decomposed at parse time      │       │
│   │     into CREATE INDEX + REINDEX + ALIAS SWAP AST nodes           │       │
│   └──────────────────────────────────────────────────────────────────┘       │
│                              │                                                │
│                              ▼                                                │
│   ┌──────────────────────────────────────────────────────────────────┐       │
│   │ Statement Compiler (AST → IRequest)                              │       │
│   │   • Translates AST verb to OpenSearchClient request shape        │       │
│   │   • Resolves $body sibling JSON object                           │       │
│   └──────────────────────────────────────────────────────────────────┘       │
│                              │                                                │
│                              ▼                                                │
│   ┌──────────────────────────────────────────────────────────────────┐       │
│   │ Runtime Request Middleware (RUN-TIME)                            │       │
│   │   • SafeDefaultMergeMiddleware — applies AST safe-default flags  │       │
│   │     to the JSON tree (op_type, dynamic) before serialization     │       │
│   │   • ImplicitWaitMiddleware — issues scoped _cluster/health call  │       │
│   │     post-statement per WaitMode (R-12)                           │       │
│   │   • TasksApiPollMiddleware — handles wait_for_completion=false   │       │
│   │     (R-11) with progress threshold logging                       │       │
│   │   • (No SecretScrubberSink per ADR-0016 — host Serilog config    │       │
│   │     handles option-value redaction if needed)                    │       │
│   └──────────────────────────────────────────────────────────────────┘       │
│                              │                                                │
│                              ▼                                                │
│                        OpenSearchClient                                       │
└─────────────────────────────────────────────────────────────────────────────┘
```

### Key interfaces

```csharp
// Public extension surface
public static class OpenSearchMigrationsExtensions {
    public static IServiceCollection AddOpenSearchMigrations(
        this IServiceCollection services,
        Action<OpenSearchMigrationOptions> configure);

    public static IServiceCollection WithProductionDefaults(
        this IServiceCollection services); // R-29
}

// Bootstrapper (façade)
public sealed class OpenSearchBootstrapper {
    public OpenSearchBootstrapper(IEnumerable<IBootstrapStep> steps, /* ... */);
    public async Task<BootstrapResult> RunAsync(CancellationToken ct);
}

// Pluggable pipeline step
public interface IBootstrapStep {
    string Name { get; }
    Task<StepOutcome> ExecuteAsync(BootstrapContext ctx, CancellationToken ct);
}

// Lock handle
public sealed class LockHandle : IAsyncDisposable {
    public CancellationToken LockExpired { get; } // cancelled on LockMaxLifetime
    public Task RenewLoopAsync(CancellationToken ct);
}

// AST safe-default flag carriers (parser output)
internal abstract record StatementAst {
    public required string Verb { get; init; }
    public required JsonNode? Body { get; init; }
    public required IReadOnlyDictionary<string, object> SafeDefaults { get; init; }
}

// Runtime middleware contract
internal interface IStatementMiddleware {
    Task<StatementResult> InvokeAsync(StatementContext ctx, StatementDelegate next);
}
```

### Data flow (single statement, end-to-end)

1. `MigrationRunner.RunAsync` → `OpenSearchRecordStore.InitializeAsync` → `OpenSearchBootstrapper.RunAsync` → each `IBootstrapStep` executes; failure on any step aborts with typed exception
2. `MigrationRunner` discovers migration class, constructs it; calls `UpAsync`
3. Migration loads `statements.json` resource; provider passes file content directly to the Parlot parser (no templating renderer — per ADR-0016)
4. Parlot parser produces `StatementAst[]`; safe-default flags computed at parse; UNSAFE/NO WAIT justification tokens validated; unsafe-op detection runs; version comparators parsed semantically
5. For each AST node: `StatementCompiler` builds an `IRequest`; runtime middleware chain processes (`SafeDefaultMergeMiddleware` merges flags into JSON tree → `ImplicitWaitMiddleware` runs scoped health check post-execute → `TasksApiPollMiddleware` polls if applicable)
6. All logs / exceptions emit structured events; option-value redaction (if needed) is configured at the host Serilog/ILogger sink layer (per ADR-0016, not provider-specific)
7. `MigrationRunner` calls `OpenSearchRecordStore.WriteAsync(record)` — CAS write with `?refresh=wait_for` and forensic fields (`appliedBy`, `direction`)
8. `LockHandle.DisposeAsync` releases lock

### Distribution

- `src/Hyperbee.Migrations.Providers.OpenSearch/` — provider library
- `runners/Hyperbee.MigrationRunner.OpenSearch/` — standalone runner (R-26)
- `runners/samples/Hyperbee.Migrations.OpenSearch.Samples/` — verb showcase (R-27)
- `tests/Hyperbee.Migrations.Integration.Tests/OpenSearch/` — integration tests; multi-node Compose harness (R-28b is now Must)

## Key Decisions (recorded ADRs)

These decisions cross the ADR threshold (reversal would touch multiple components):

1. **[ADR-0011](../decisions/0011-hybrid-parser-runtime-injection.md): Hybrid parser+runtime injection for OpenSearch safe defaults** — parser owns intent (AST flags + parse-time enumeration), runtime owns merge (JSON tree mutation during request build). Reversal would touch every safe-default verb plus all observability hooks.
2. **[ADR-0012](../decisions/0012-with-production-defaults-extension.md): `WithProductionDefaults()` extension method instead of `EnvironmentProfile` enum** — driven by the IR's hidden-coupling concern in assessment 0002. Reversal would change the entire DI surface for the provider.
3. **[ADR-0013](../decisions/0013-always-create-indices-with-override.md): Always-create lock and ledger indices in `InitializeAsync` with explicit override** — `AssumeIndicesExist` option for tightly-scoped IAM contexts. Reversal would change the contract of `InitializeAsync` and affect lock-acquire path performance.
4. **[ADR-0014](../decisions/0014-state-machine-facade-over-pipeline.md): State-machine façade over `IBootstrapStep[]` pipeline** — public API matches Couchbase house style; internal composition is testable and replaceable. Reversal would either flatten the pipeline (breaking testability) or expose the pipeline (breaking the simple public contract).
5. **[ADR-0015](../decisions/0015-parser-offline-pure-all-io-runtime.md): Parser is offline-pure; all I/O is runtime middleware** — clarifying corollary of ADR-0011. Resolves R-30 template-lookup ambiguity. Future verbs that need cluster state must use unresolved-reference AST + runtime middleware.
6. **[ADR-0016](../decisions/0016-no-file-level-templating.md): OpenSearch provider does not use file-level templating** — strikes R-10; matches Aerospike/Couchbase/MongoDB/Postgres house style. Re-introducing templating requires a superseding ADR.

## Rejected Approaches

- **Approach A — Couchbase-Clone (runtime middleware only):** Lost on requirements compliance (~85%). Pure runtime middleware sees fully-built JSON and cannot satisfy R-08a/R-17/R-18's parse-time error contracts. Component-template detection (`composed_of` presence in AST vs JSON tree walk) is harder at runtime; UNSAFE token validation must happen at parse anyway. State machine alone (no pipeline) is verbose and harder to test in isolation than the façade-over-pipeline shape C adopts.
- **Approach B — Parser-First Composition (parser only, provision-on-demand, IBootstrapStep pipeline):** Lost on requirements compliance (~82%) and lock-init race. Pure parser cannot route logs through SecretScrubber (R-25); cannot emit structured WARN events from response paths; cannot observe Tasks API progress. Provision-on-demand for lock index introduces a race window during the very first concurrent acquire (the laziest CI matrix run becomes the worst case for race exposure). Pipeline-only public API loses the simple Couchbase-shaped contract that house-style consistency demands.

## Risks and Open Questions

### Riskiest assumption (validate early)

**The runtime middleware can correctly merge AST safe-default flags into arbitrary user-supplied JSON bodies.** Specifically: `op_type: create` injection on `_reindex` request bodies that already contain a `dest` object; `dynamic: strict` injection into `mappings.properties` when only `mappings` is present at the top level; preservation of an existing `dynamic: true` set explicitly by the author. This must be the first integration test written — it validates the parser/runtime split before any other component is built. If the merge logic is fragile, the architecture's primary advantage collapses.

### Other open questions worth surfacing

- **Pipeline parallelism within bootstrapper:** the `IBootstrapStep[]` pipeline could run independent steps (ledger + lock init) in parallel. Worth doing? If yes, step dependencies must be declared (`DependsOn` attribute or topological sort). If no, the linear sequential model is simpler. Recommend **linear in v1** unless a concrete bottleneck emerges in R-24c's measured-cost test.
- **Middleware ordering:** if a consumer adds a custom `IStatementMiddleware`, the position in the chain matters. Need a documented order convention (`Order` attribute) and a test that asserts the built-in middleware order.
- **`AssumeIndicesExist = true` validation:** when set, `InitializeAsync` skips create but does it *verify* the indices exist with the expected mapping? Recommend yes — verification is cheap; silent acceptance of missing indices is worse than the cost.
- ~~Hyperbee.Templating + SecretMarker integration~~ — REMOVED per ADR-0016. The first-contact bug class PM-5 worried about is fully eliminated by not adopting the engine.
- **State-machine façade observability:** the public `BootstrapResult` should expose per-step status for log aggregation. Recommend enumerating the steps in `BootstrapResult.Steps` so operators can see exactly which step failed without parsing log strings.

## Recommended next steps

1. **Run `/nop:adr` four times** to materialize ADRs 0011-0014 (or run `/nop:adr derive` to mine them from this spec in one pass)
2. **Run `/nop:plan`** to decompose into phased tasks. Suggest first phase = riskiest-assumption validation: parser AST + runtime middleware merge logic + tests against representative bodies (the validation listed above)
3. **Optional:** `/nop:assess` on this design before planning — the design is mid-stakes (production-capable provider but with mature precedent in Couchbase). Stakes don't justify a second Full Assessment, but a `/nop:red-blue` pass on the design could catch design-level gold-plating before plan-time
