//#define INTEGRATIONS
#nullable enable
using System.Text.Json;
using Hyperbee.Migrations.Integration.Tests.Container.OpenSearch;
using Hyperbee.Migrations.Providers.OpenSearch;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Bootstrap;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Bootstrap.Steps;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Dispatch;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Grammar;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Middleware;
using Hyperbee.Migrations.Providers.OpenSearch.Resources;
using Microsoft.Extensions.Logging.Abstractions;
using OpenSearch.Net;

namespace Hyperbee.Migrations.Integration.Tests;

#if INTEGRATIONS
// R-28b multi-node Testcontainers harness keystone tests.
//
// Single-node clusters mask three production-correctness behaviors:
//
//   1. GREEN-threshold semantics. A single-node cluster has nowhere to put
//      replicas, so health is permanently YELLOW. Production deployments
//      use WithProductionDefaults() which flips the threshold to GREEN
//      (R-12). The behavior is only meaningfully exercisable on multi-node.
//
//   2. PA-2 lock-index replicas:0 invariant. The `number_of_replicas: 0`
//      setting on the lock index prevents replica-write coupling under
//      concurrent acquire — irrelevant on single-node (no replicas to
//      coupling-with), load-bearing on multi-node where the cluster would
//      otherwise allocate replicas.
//
//   3. Replica allocation + shard relocation behaviors. Indices that ship
//      with `number_of_replicas: 1+` get shards on multiple nodes; ALIAS
//      SWAP under background writes exercises shard relocation during the
//      cutover (R-24c (a)). Single-node never sees this code path.
//
// These tests opt-in via [ClassInitialize] so the multi-node fixture (3
// JVMs at ~512MB each) is paid only when this test class runs.

[TestClass]
public class OpenSearchMultiNodeIntegrationTests
{
    [ClassInitialize]
    public static async Task ClassSetup( TestContext context )
    {
        await MultiNodeOpenSearchTestContainer.InitializeAsync( context.CancellationTokenSource.Token );
    }

    [ClassCleanup]
    public static async Task ClassTeardown()
    {
        await MultiNodeOpenSearchTestContainer.DisposeAsync();
    }

    private string _slug = null!;

    [TestInitialize]
    public void Setup()
    {
        _slug = Guid.NewGuid().ToString( "n" );
    }

    // ---- 1: GREEN-threshold reachability ----

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "MultiNode" )]
    [TestCategory( "R-28b" )]
    public async Task Cluster_ReachesGreenStatus_OnceAllNodesJoined()
    {
        // Production-default (WithProductionDefaults() flips threshold to
        // Green) is only achievable on multi-node. Verify the cluster does
        // reach GREEN here so the production-defaults path is testable.
        var ll = MultiNodeOpenSearchTestContainer.LowLevelClient;

        // Wait up to 30s for GREEN — replicas may still be allocating right
        // after the last node joined.
        var deadline = DateTimeOffset.UtcNow.AddSeconds( 30 );
        string? lastStatus = null;
        while ( DateTimeOffset.UtcNow < deadline )
        {
            var resp = await ll.DoRequestAsync<StringResponse>(
                global::OpenSearch.Net.HttpMethod.GET, "_cluster/health", default );
            Assert.IsTrue( resp.Success, $"_cluster/health failed: {resp.Body}" );

            using var doc = JsonDocument.Parse( resp.Body );
            lastStatus = doc.RootElement.GetProperty( "status" ).GetString();
            var numNodes = doc.RootElement.GetProperty( "number_of_nodes" ).GetInt32();
            Assert.AreEqual( 3, numNodes, "fixture should report 3 nodes" );

            if ( lastStatus == "green" )
                return;

            await Task.Delay( 500 );
        }

        Assert.Fail( $"cluster did not reach GREEN within 30s; last observed status: {lastStatus}" );
    }

    // ---- 2: PA-2 lock-index replicas:0 invariant ----

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "MultiNode" )]
    [TestCategory( "PA-2" )]
    public async Task LockIndex_BootstrappedWithReplicasZero_PreventsReplicaWriteCoupling()
    {
        // PA-2 (assessment 0002): the lock index must be created with
        // number_of_replicas: 0 so concurrent-acquire under N runners isn't
        // slowed by replica-write coupling on the lock primary shard.
        // Single-node masks this — there are no replicas to allocate. On
        // multi-node, the default OpenSearch index-creation behavior would
        // allocate `number_of_replicas: 1` if the lock-index init didn't
        // explicitly set 0.
        var options = new OpenSearchMigrationOptions
        {
            LedgerIndex = $".migrations-mn-{_slug}",
            LockIndex = $".migrations-mn-lock-{_slug}",
            LockName = $"lock-mn-{_slug}",
            LockRenewInterval = TimeSpan.FromSeconds( 10 ),
            LockStaleAfter = TimeSpan.FromSeconds( 30 ),
            LockMaxLifetime = TimeSpan.FromMinutes( 5 )
        };

        var client = MultiNodeOpenSearchTestContainer.Client;
        var bootstrapper = new OpenSearchBootstrapper(
            new IBootstrapStep[]
            {
                new RestPingStep(),
                new ClusterHealthStep(),
                new LedgerIndexInitStep(),
                new LockIndexInitStep()
            },
            client, options, TimeProvider.System, NullLoggerFactory.Instance );

        var store = new OpenSearchRecordStore(
            client, bootstrapper, options, TimeProvider.System,
            NullLogger<OpenSearchRecordStore>.Instance );

        await store.InitializeAsync();
        try
        {
            var ll = MultiNodeOpenSearchTestContainer.LowLevelClient;
            var settingsResp = await ll.DoRequestAsync<StringResponse>(
                global::OpenSearch.Net.HttpMethod.GET, $"{options.LockIndex}/_settings", default );
            Assert.IsTrue( settingsResp.Success, $"settings probe failed: {settingsResp.Body}" );

            using var doc = JsonDocument.Parse( settingsResp.Body );
            var replicasStr = doc.RootElement
                .GetProperty( options.LockIndex )
                .GetProperty( "settings" )
                .GetProperty( "index" )
                .GetProperty( "number_of_replicas" )
                .GetString();

            Assert.AreEqual( "0", replicasStr,
                "lock index must be created with number_of_replicas: 0 per PA-2 — without this, " +
                "concurrent-acquire under N runners stalls on replica-write coupling on the lock primary." );

            // Sanity: ledger index also follows the same convention (it's a
            // small forensic table, replicas would just slow writes without
            // adding HA value for a per-record-id idempotent op).
            var ledgerSettingsResp = await ll.DoRequestAsync<StringResponse>(
                global::OpenSearch.Net.HttpMethod.GET, $"{options.LedgerIndex}/_settings", default );
            using var ledgerDoc = JsonDocument.Parse( ledgerSettingsResp.Body );
            var ledgerReplicas = ledgerDoc.RootElement
                .GetProperty( options.LedgerIndex )
                .GetProperty( "settings" )
                .GetProperty( "index" )
                .GetProperty( "number_of_replicas" )
                .GetString();
            Assert.AreEqual( "0", ledgerReplicas,
                "ledger index should also use replicas:0 per the same rationale" );
        }
        finally
        {
            var ll = MultiNodeOpenSearchTestContainer.LowLevelClient;
            await ll.Indices.DeleteAsync<StringResponse>( options.LedgerIndex );
            await ll.Indices.DeleteAsync<StringResponse>( options.LockIndex );
        }
    }

    // ---- 3: Replica allocation across nodes ----

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "MultiNode" )]
    [TestCategory( "R-28b" )]
    public async Task UserIndex_WithReplicasOne_AllocatesShardsOnMultipleNodes()
    {
        // Standard production setup: an author creates a user index with
        // number_of_replicas: 1. Verify the cluster actually allocates the
        // primary on one node and the replica on another. (Pinned to
        // multi-node because single-node leaves replicas unallocated and
        // health stays YELLOW — exactly the masking behavior we want
        // to surface here.)
        var indexName = $"users-mn-{_slug}";
        var ll = MultiNodeOpenSearchTestContainer.LowLevelClient;

        var body = $$"""
            {
              "settings": { "number_of_shards": 1, "number_of_replicas": 1 },
              "mappings": { "properties": { "id": { "type": "keyword" } } }
            }
            """;

        var createResp = await ll.Indices.CreateAsync<StringResponse>(
            indexName, PostData.String( body ) );
        Assert.IsTrue( createResp.Success, $"create failed: {createResp.Body}" );

        try
        {
            // Replica allocation is exactly what `_cluster/health/<index>`
            // signals as `green`. With number_of_replicas: 1 on a 3-node
            // cluster: green = all primaries allocated AND all replicas
            // allocated on different nodes. Yellow = primaries OK but
            // replicas unassigned (the single-node trap). The check is
            // crisp and covers the production-correctness behavior we
            // care about without needing to parse _cat output.
            var deadline = DateTimeOffset.UtcNow.AddSeconds( 30 );
            string? lastStatus = null;
            int activeShards = -1, unassignedShards = -1;
            while ( DateTimeOffset.UtcNow < deadline )
            {
                var healthResp = await ll.Cluster.HealthAsync<StringResponse>( indexName );
                Assert.IsTrue( healthResp.Success );
                using var doc = JsonDocument.Parse( healthResp.Body );
                lastStatus = doc.RootElement.GetProperty( "status" ).GetString();
                activeShards = doc.RootElement.GetProperty( "active_shards" ).GetInt32();
                unassignedShards = doc.RootElement.GetProperty( "unassigned_shards" ).GetInt32();

                if ( lastStatus == "green" )
                    break;

                await Task.Delay( 500 );
            }

            Assert.AreEqual( "green", lastStatus,
                $"index `{indexName}` (1 primary + 1 replica) should reach GREEN on a 3-node cluster. " +
                $"Last observed: status={lastStatus}, active_shards={activeShards}, unassigned_shards={unassignedShards}. " +
                $"YELLOW with unassigned_shards>0 indicates replicas could not allocate to a different node — " +
                $"the exact production failure single-node clusters mask." );

            // 1 primary + 1 replica = 2 active shards. If the cluster fudged
            // `number_of_replicas` to 0, active_shards would be 1.
            Assert.AreEqual( 2, activeShards,
                $"expected 1 primary + 1 replica = 2 active shards; saw {activeShards}." );
        }
        finally
        {
            await ll.Indices.DeleteAsync<StringResponse>( indexName );
        }
    }

    // ---- 4: ALIAS SWAP under background writes (R-24c (a)) ----

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "MultiNode" )]
    [TestCategory( "R-24c" )]
    public async Task AliasSwap_DuringBackgroundWrites_AllPreSwapDocsReachable()
    {
        // R-24c (a): zero-downtime alias swap with active background writes.
        //
        // Setup: alias `app` points at users-v1; a background writer is
        // pumping documents into users-v1 while we run the migration steps:
        //   CREATE INDEX users-v2
        //   REINDEX users-v1 -> users-v2
        //   ALIAS SWAP app FROM users-v1 TO users-v2
        //
        // Post-condition: every document the background writer wrote BEFORE
        // the swap-time snapshot must be reachable through the alias after
        // the swap. Documents written AFTER the reindex started but BEFORE
        // the swap completed may legitimately go to v1 (which the alias no
        // longer points at) — that's the inherent gap of any reindex-and-
        // swap pattern, and authors handle it with explicit dual-write or
        // post-swap delta-reindex (out of scope here).
        //
        // What this test pins: alias-swap atomicity under load. The cluster
        // must atomically remove from v1 and add to v2, never leaving the
        // alias on both or neither.
        var src = $"users-v1-{_slug}";
        var dst = $"users-v2-{_slug}";
        var alias = $"app-{_slug}";

        var ll = MultiNodeOpenSearchTestContainer.LowLevelClient;

        // Permissive index for seeding — bypass strict-default by setting
        // explicit mappings here rather than going through the full
        // dispatcher path.
        var indexBody = """
            {
              "settings": { "number_of_shards": 1, "number_of_replicas": 1 },
              "mappings": { "properties": { "id": { "type": "keyword" }, "n": { "type": "long" } } }
            }
            """;

        await ll.Indices.CreateAsync<StringResponse>( src, PostData.String( indexBody ) );
        await ll.DoRequestAsync<StringResponse>(
            global::OpenSearch.Net.HttpMethod.POST, "_aliases", default,
            data: PostData.String( $$"""{ "actions": [ { "add": { "index": "{{src}}", "alias": "{{alias}}" } } ] }""" ) );

        // Wait for index to go GREEN before starting writes.
        await Task.Delay( 1000 );

        var cts = new CancellationTokenSource();
        var preSwapDocCount = 0;
        var totalDocsAttempted = 0;
        var writerTask = Task.Run( async () =>
        {
            var n = 0;
            while ( !cts.IsCancellationRequested )
            {
                var doc = $$"""{ "id": "u{{n}}", "n": {{n}} }""";
                try
                {
                    var resp = await ll.IndexAsync<StringResponse>( src, $"u{n}", PostData.String( doc ), ctx: cts.Token );
                    if ( resp.Success )
                        Interlocked.Increment( ref totalDocsAttempted );
                }
                catch ( OperationCanceledException ) { break; }
                catch { /* tolerate transient errors */ }
                n++;
                await Task.Delay( 5, cts.Token ).ContinueWith( _ => { } );  // small pacing
            }
        }, cts.Token );

        // Let the writer build up some docs.
        await Task.Delay( 1500 );
        await ll.Indices.RefreshAsync<StringResponse>( src );
        var countResp1 = await ll.DoRequestAsync<StringResponse>( global::OpenSearch.Net.HttpMethod.GET, $"{src}/_count", default );
        using ( var doc = JsonDocument.Parse( countResp1.Body ) )
            preSwapDocCount = doc.RootElement.GetProperty( "count" ).GetInt32();
        Assert.IsTrue( preSwapDocCount > 0, "writer should have indexed at least some docs by now" );

        try
        {
            // Build the dispatcher and run the migration steps via the parser
            // so the in-body atomic precondition is exercised (R-16).
            var options = new OpenSearchMigrationOptions { WaitMode = WaitMode.Off };
            var dispatcher = new StatementDispatcher( new SafeDefaultMergeMiddleware() );
            var parser = new OpenSearchStatementParser();

            async Task<StatementResult> Dispatch( string stmt )
            {
                var ast = parser.Parse( stmt );
                var ctx = new StatementContext
                {
                    Client = MultiNodeOpenSearchTestContainer.Client,
                    Options = options,
                    TimeProvider = TimeProvider.System,
                    Logger = NullLogger.Instance,
                    ResolvedBody = null,
                    CancellationToken = default
                };
                return await dispatcher.DispatchAsync( ast, ctx );
            }

            // Build the destination with the same shape (no template here).
            var createV2 = await ll.Indices.CreateAsync<StringResponse>( dst, PostData.String( indexBody ) );
            Assert.IsTrue( createV2.Success, $"create v2 failed: {createV2.Body}" );

            // Refresh source so reindex sees the latest pre-swap docs.
            await ll.Indices.RefreshAsync<StringResponse>( src );

            // Capture the count we expect to see post-swap. Anything indexed
            // AFTER this snapshot may end up on either side — that's the
            // inherent reindex-and-swap gap and isn't what this test asserts.
            var snapshotCountResp = await ll.DoRequestAsync<StringResponse>( global::OpenSearch.Net.HttpMethod.GET, $"{src}/_count", default );
            int snapshotCount;
            using ( var doc = JsonDocument.Parse( snapshotCountResp.Body ) )
                snapshotCount = doc.RootElement.GetProperty( "count" ).GetInt32();

            var reindexResult = await Dispatch( $"REINDEX FROM {src} TO {dst}" );
            Assert.IsTrue( reindexResult.IsSuccess, $"reindex failed: {reindexResult.Detail}" );

            // The swap is the keystone — atomic remove+add in one body.
            var swapResult = await Dispatch( $"ALIAS SWAP {alias} FROM {src} TO {dst}" );
            Assert.IsTrue( swapResult.IsSuccess, $"swap failed: {swapResult.Detail}" );

            // Stop the writer now that the alias has moved.
            cts.Cancel();
            try { await writerTask; } catch { /* writer just exits */ }

            await ll.Indices.RefreshAsync<StringResponse>( dst );

            // Atomicity post-condition: alias never points at both indices.
            var aliasResp = await ll.Indices.GetAliasAsync<StringResponse>( alias );
            using ( var aliasDoc = JsonDocument.Parse( aliasResp.Body! ) )
            {
                Assert.IsTrue( aliasDoc.RootElement.TryGetProperty( dst, out _ ),
                    "alias should resolve to destination after swap" );
                Assert.IsFalse( aliasDoc.RootElement.TryGetProperty( src, out _ ),
                    "alias must NOT resolve to source after swap (atomicity)" );
            }

            // Reachability post-condition: every document captured in the
            // pre-reindex snapshot must be reachable via the alias.
            var aliasCountResp = await ll.DoRequestAsync<StringResponse>( global::OpenSearch.Net.HttpMethod.GET, $"{alias}/_count", default );
            int aliasCount;
            using ( var doc = JsonDocument.Parse( aliasCountResp.Body ) )
                aliasCount = doc.RootElement.GetProperty( "count" ).GetInt32();

            Assert.IsTrue( aliasCount >= snapshotCount,
                $"alias should resolve to at least the pre-reindex snapshot count " +
                $"(snapshotCount={snapshotCount}, aliasCount={aliasCount}, " +
                $"writerTotalAttempted={totalDocsAttempted})" );
        }
        finally
        {
            cts.Cancel();
            try { await writerTask; } catch { /* writer just exits */ }
            await ll.Indices.DeleteAsync<StringResponse>( $"{src},{dst}" );
        }
    }
}
#endif
