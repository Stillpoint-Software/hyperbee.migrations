# Multi-node OpenSearch test harness

`MultiNodeOpenSearchTestContainer` spins up a 3-node OpenSearch cluster on a private Docker network for tests that exercise behaviors single-node clusters mask.

## When to use it

Pick this harness over the single-node `OpenSearchTestContainer` when the test is asserting any of:

- **GREEN-threshold semantics.** Single-node never reaches GREEN — replicas have nowhere to allocate, so health is permanently YELLOW. `WithProductionDefaults()` flips the threshold to GREEN; only multi-node exercises it.
- **Replica allocation / shard placement.** `number_of_replicas: 1+` only does anything on multi-node.
- **Shard relocation during cluster operations** (e.g., `ALIAS SWAP` under background writes — R-24c (a)).
- **PA-2 lock-index `number_of_replicas: 0` invariant.** Single-node has no replicas to allocate, so the assertion is vacuous.
- **Concurrent-acquire under N runners on a real master** (R-24c (k)).

Otherwise stick with single-node — it's faster (one container, no cluster formation wait) and uses ~⅓ the memory.

## Lifecycle

The harness is **opt-in per test class**, not wired into the assembly-level `InitializeTestContainers`. Tests that don't need multi-node pay zero startup cost.

```csharp
[TestClass]
public class MyMultiNodeTests
{
    [ClassInitialize]
    public static async Task ClassSetup( TestContext context )
    {
        await MultiNodeOpenSearchTestContainer.InitializeAsync(
            context.CancellationTokenSource.Token );
    }

    [ClassCleanup]
    public static async Task ClassTeardown()
    {
        await MultiNodeOpenSearchTestContainer.DisposeAsync();
    }

    [TestMethod]
    [TestCategory( "MultiNode" )]
    public async Task MyTest()
    {
        var client = MultiNodeOpenSearchTestContainer.Client;
        // ...
    }
}
```

## Test categories

Multi-node tests should carry `[TestCategory("MultiNode")]` so CI runners can include or exclude them as a group:

```bash
# Run only multi-node tests
dotnet test --filter "TestCategory=MultiNode"

# Run everything EXCEPT multi-node (faster CI sweep)
dotnet test --filter "TestCategory!=MultiNode"
```

## Resource cost

The cluster runs 3 OpenSearch JVMs at ~512MB heap each (1.5GB minimum, plus JVM overhead). Cluster formation typically takes 20–30s on a developer machine; a generous 60s deadline is set in `WaitForFullClusterAsync`. Tests within the class share one cluster — only the per-class fixture takes the startup hit, not each test.

## Why no per-node HTTP wait strategy

Earlier iterations of the harness set a Testcontainers wait strategy on node1 to wait for `_cluster/health?wait_for_status=yellow`. That deadlocks: with `cluster.initial_master_nodes` listing all 3 nodes, the cluster cannot form (and therefore cannot reach YELLOW) until ALL 3 are running — but Testcontainers won't return from `node1.StartAsync` until the wait strategy passes, so node2 never starts.

The harness instead skips per-node HTTP wait strategies (relying on Testcontainers' default process-alive readiness) and does a harness-level `WaitForFullClusterAsync` after all 3 containers are up. That waits for the cluster's own view to report `number_of_nodes == 3`, which is what tests actually need.

## Concurrency

The harness uses static fields, so two test classes running in parallel against `MultiNodeOpenSearchTestContainer` would race. MSTest's default class-fixture serialization within a single assembly handles this fine — the issue would only arise if tests were marked `[Parallelize]` across `MultiNode`-tagged classes. None are today; if that changes, this harness needs an instance-per-class wrapper.
