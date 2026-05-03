#nullable enable
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using OpenSearch.Client;
using OpenSearch.Net;

namespace Hyperbee.Migrations.Integration.Tests.Container.OpenSearch;

// R-28b — multi-node Testcontainers Compose harness.
//
// Spawns a 3-node OpenSearch cluster on a private Docker network. Each node
// has its own host-side port mapping; the test client connects through the
// first node. The cluster forms automatically once the seed hosts can resolve
// each other via the network's DNS aliases. The wait strategy on the first
// node holds until the cluster reports YELLOW (formed enough to serve writes,
// even before all replicas are assigned).
//
// Why a separate harness:
//
//   - Single-node clusters can never reach GREEN — primary shards have nowhere
//     to allocate replicas, so health hovers at YELLOW. Tests asserting
//     production behaviors that depend on GREEN (R-12 wait-for-green via
//     WithProductionDefaults, replica allocation, shard relocation during
//     ALIAS SWAP) need real multi-node.
//   - PA-2 (assessment 0002): the lock index uses `number_of_replicas: 0` so
//     concurrent-acquire under N runners isn't slowed by replica-write
//     coupling. That's only meaningfully verifiable on a cluster that WOULD
//     otherwise allocate replicas (i.e., multi-node).
//   - The R-24c production scenario suite has multi-node-only entries
//     (alias swap with active background writes, lock primary-shard
//     contention with replicas:0 verification, ledger refresh budget under
//     replica load).
//
// Lifecycle:
//
//   - Initialize() is opt-in per test class via [ClassInitialize], NOT
//     wired into the assembly-level InitializeTestContainers. Tests that
//     don't need multi-node pay zero startup cost (3 JVMs * ~512MB is
//     significant). Tests that DO need it call Initialize from their
//     own ClassInitialize and Cleanup from ClassCleanup.
//
//   - Container cleanup is automatic via WithCleanUp(true) — Testcontainers
//     stops + removes the containers when the test process exits OR when
//     each container's IDisposable is disposed.
//
// Concurrency: a single static instance per test process. Two test classes
// using multi-node simultaneously is not supported (would race on the
// statics); MSTest's default ClassInitialize ordering serializes class
// fixtures within a single assembly run, so this is fine in practice.

public class MultiNodeOpenSearchTestContainer
{
    private const string ImageTag = "opensearchproject/opensearch:2.18.0";
    private const int OpenSearchPort = 9200;
    private const string AdminPassword = "Hyperbee.Migrations.Test#2026";
    private const int NodeCount = 3;
    private const string ClusterName = "hyperbee-migrations-multinode";

    public static IOpenSearchClient Client { get; private set; } = null!;
    public static OpenSearchLowLevelClient LowLevelClient { get; private set; } = null!;
    public static INetwork Network { get; private set; } = null!;
    public static IList<IContainer> Nodes { get; private set; } = null!;
    public static Uri Endpoint { get; private set; } = null!;

    public static async Task InitializeAsync( CancellationToken cancellationToken = default )
    {
        var network = new NetworkBuilder()
            .WithName( $"opensearch-multinode-{Guid.NewGuid():N}" )
            .WithCleanUp( true )
            .Build();

        await network.CreateAsync( cancellationToken ).ConfigureAwait( false );

        // Stable DNS aliases on the network so the OpenSearch nodes can
        // resolve each other via discovery.seed_hosts. Names must match
        // what each node registers as `node.name`, otherwise the cluster
        // refuses to elect a master (initial_master_nodes is name-based).
        var nodeNames = Enumerable.Range( 1, NodeCount )
            .Select( static i => $"opensearch-node{i}" )
            .ToArray();
        var seedHosts = string.Join( ",", nodeNames );

        var nodes = new List<IContainer>( NodeCount );
        for ( var i = 0; i < NodeCount; i++ )
        {
            var nodeName = nodeNames[i];
            var builder = new ContainerBuilder()
                .WithImage( ImageTag )
                .WithNetwork( network )
                .WithNetworkAliases( nodeName )
                .WithPortBinding( OpenSearchPort, true )    // host-side port assigned
                .WithEnvironment( "cluster.name", ClusterName )
                .WithEnvironment( "node.name", nodeName )
                .WithEnvironment( "discovery.seed_hosts", seedHosts )
                .WithEnvironment( "cluster.initial_master_nodes", seedHosts )
                .WithEnvironment( "bootstrap.memory_lock", "false" )
                .WithEnvironment( "DISABLE_SECURITY_PLUGIN", "true" )
                .WithEnvironment( "DISABLE_INSTALL_DEMO_CONFIG", "true" )
                .WithEnvironment( "OPENSEARCH_INITIAL_ADMIN_PASSWORD", AdminPassword )
                // Conservative heap so 3 JVMs fit on a typical CI runner.
                .WithEnvironment( "OPENSEARCH_JAVA_OPTS", "-Xms512m -Xmx512m" )
                .WithCleanUp( true );

            // No per-node HTTP wait strategy. With cluster.initial_master_nodes
            // listing all 3 nodes, none of them will go YELLOW until ALL
            // are running and have joined — so a per-node wait_for_status
            // wait deadlocks if the strategy runs before the next node
            // starts. We rely on the harness-level WaitForFullClusterAsync
            // below to confirm the cluster has converged once all 3
            // containers are up.
            //
            // Default ContainerBuilder readiness (process alive + ports
            // bound) is enough at this layer; the harness-level health
            // check covers the actual cluster-ready signal.

            var node = builder.Build();
            await node.StartAsync( cancellationToken ).ConfigureAwait( false );
            nodes.Add( node );
        }

        Nodes = nodes;
        Network = network;

        var firstNode = nodes[0];
        var host = firstNode.Hostname;
        var port = firstNode.GetMappedPublicPort( OpenSearchPort );
        Endpoint = new UriBuilder( "http", host, port ).Uri;

        var settings = new ConnectionSettings( Endpoint )
            .DisableDirectStreaming()
            .ThrowExceptions();

        Client = new OpenSearchClient( settings );
        LowLevelClient = new OpenSearchLowLevelClient( settings );

        // Wait for all 3 nodes to join. UntilHttpRequestIsSucceeded only checked
        // node1's view of itself; we want the cluster's view to confirm
        // discovery has converged before tests begin.
        await WaitForFullClusterAsync( cancellationToken ).ConfigureAwait( false );
    }

    public static async Task DisposeAsync()
    {
        if ( Nodes is null ) return;

        foreach ( var node in Nodes )
        {
            try
            {
                await node.DisposeAsync().ConfigureAwait( false );
            }
            catch
            {
                // Best-effort teardown; Testcontainers' WithCleanUp(true) is the safety net.
            }
        }

        try
        {
            await Network.DisposeAsync().ConfigureAwait( false );
        }
        catch
        {
            // Best-effort.
        }
    }

    private static async Task WaitForFullClusterAsync( CancellationToken cancellationToken )
    {
        // Poll _cluster/health until it reports number_of_nodes == NodeCount,
        // up to a generous deadline. 3 nodes typically converge within 10–20s
        // after node3 starts; bail out at 60s with a clear error so a stuck
        // cluster surfaces as a fixture failure rather than a confusing
        // test-time symptom.
        var deadline = DateTimeOffset.UtcNow.AddSeconds( 60 );
        while ( DateTimeOffset.UtcNow < deadline )
        {
            cancellationToken.ThrowIfCancellationRequested();

            try
            {
                var resp = await LowLevelClient.DoRequestAsync<StringResponse>(
                    global::OpenSearch.Net.HttpMethod.GET, "_cluster/health", cancellationToken ).ConfigureAwait( false );

                if ( resp.Success && resp.Body!.Contains( $"\"number_of_nodes\":{NodeCount}" ) )
                    return;
            }
            catch
            {
                // Cluster transient state during election — retry.
            }

            await Task.Delay( 1000, cancellationToken ).ConfigureAwait( false );
        }

        throw new InvalidOperationException(
            $"Multi-node OpenSearch cluster did not reach number_of_nodes={NodeCount} within 60s. " +
            "Check Docker resources (3 JVMs at ~512MB each) and the test container logs." );
    }
}
