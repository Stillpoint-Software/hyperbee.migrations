# ADR-0014: State-Machine Façade over IBootstrapStep[] Pipeline

**Status:** Accepted
**Date:** 2026-05-02

## Context

The OpenSearch provider's bootstrapper (R-02) must orchestrate cluster readiness checks, ledger init, lock-index init, and optional warmup. Three architectures were considered during `/nop:propose`:

1. **Direct port of Couchbase state machine** — `CouchbaseBootstrapper`'s 7-state design, transliterated to OpenSearch states (REST ping → cluster health → ledger ready → lock ready → sacrificial query). Verbose but battle-tested in production.

2. **Pure pipeline (`IBootstrapStep[]`)** — bootstrapper composed of DI-registered steps; consumers add custom steps. Cleanly testable in isolation; loses the simple house-style public contract that operators expect when reading bootstrapper logs across providers.

3. **Simpler async sequence** — flat `await` calls in `InitializeAsync`. Smallest surface area but loses both testability and consumer extension points.

The forces in tension:

- **House-style consistency** — Couchbase's state-machine pattern is the precedent; operators reading bootstrap logs across providers benefit from a uniform shape.
- **Internal testability** — testing the state machine end-to-end requires a real cluster; testing individual steps in isolation against mocked clients is significantly faster.
- **Consumer extensibility** — some consumers will want to add domain-specific bootstrap behavior (e.g., custom warmup queries); a pluggable step list accommodates this without subclassing.
- **YAGNI risk** — if no consumer ever extends the bootstrapper, the pipeline pluggability is dead weight.
- **Public-contract simplicity** — exposing `IBootstrapStep[]` as the public bootstrap API forces every operator to learn the pipeline concept; exposing a state machine keeps the public surface small.

Assessment 0002 (Phase 1 Performance Audit, PA-12 + PA-3) flagged that bootstrap `_cluster/health` storms at rolling-deploy startup are a real concern; future optimization may want to parallelize independent steps. A pipeline structure makes that trivial; a state machine makes that surgery.

## Decision

We will implement the bootstrapper as a state-machine façade whose internal implementation is composed of `IBootstrapStep` instances registered in DI.

**Public contract** (`OpenSearchBootstrapper`):

```csharp
public sealed class OpenSearchBootstrapper {
    public OpenSearchBootstrapper(IEnumerable<IBootstrapStep> steps, ...);
    public Task<BootstrapResult> RunAsync(CancellationToken ct);
}

public sealed record BootstrapResult(
    BootstrapStatus Status,
    IReadOnlyList<StepResult> Steps,
    Exception? FailedAt
);
```

The result projects the per-step outcomes so operators see exactly which step failed without parsing log strings.

**Internal pipeline** — the default registration ships these steps in order:
- `RestPingStep` — verifies cluster reachability
- `ClusterHealthStep` — `_cluster/health` poll per R-03 threshold
- `EndpointCapabilityStep` — AWS endpoint loud-fail + ISM endpoint detection (R-21)
- `LedgerIndexInitStep` — R-06 strict mapping creation/verification
- `LockIndexInitStep` — R-04 lock index with `number_of_replicas: 0`
- `SacrificialQueryStep` — optional warmup (skip-able by config)

Consumers extend by registering an additional `IBootstrapStep` in DI; default ordering is preserved unless the consumer explicitly opts into reordering via a position attribute.

## Consequences

**Easier:**
- Each step is a small unit testable in isolation against a mocked `IOpenSearchClient` — unit suite (R-24) covers all steps without Docker
- The state-machine façade exposes `BootstrapResult.Steps` for log aggregation; operators see which step failed at a glance
- Consumers add custom steps by registering an additional `IBootstrapStep` — no subclassing required
- Future parallelization (PA-12 mitigation) is internal: two independent steps can declare no `DependsOn` constraint and run concurrently without changing the public API
- Documentation can teach the state machine *as the contract*; the pipeline is implementation detail

**Harder:**
- Two layers must stay coordinated; documentation must clarify that "extending the bootstrapper" means registering an `IBootstrapStep` in DI, not subclassing the façade
- The pipeline-with-position-attributes ordering scheme has edge cases (consumer registers a step with a position that conflicts with a built-in step) that need explicit policy
- Per-step error wrapping must preserve exception types so callers can pattern-match on `OpenSearchNotReadyException`, `AwsSigV4NotConfiguredException`, etc. — easy to get wrong if not designed up-front

**Constrains:**
- Future bootstrapper changes must respect that pluggable steps may declare dependencies; ordering must be deterministic and documented
- If pipeline pluggability proves YAGNI in practice, we may seal the internal pipeline (mark it `internal sealed`) without breaking the public contract — but doing so requires a superseding ADR
- The default step list is part of the contract; adding a step that runs by default is a breaking change for consumers who registered steps with explicit positions
- Custom consumer steps run with the same `BootstrapContext` and `CancellationToken`; they must handle cancellation correctly and must not throw unhandled exceptions
