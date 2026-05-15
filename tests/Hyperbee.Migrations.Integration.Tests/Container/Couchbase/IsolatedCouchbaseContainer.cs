using System.Net.Http.Headers;
using System.Text;
using Couchbase;
using Couchbase.Diagnostics;
using Couchbase.Management.Buckets;
using Hyperbee.Migrations.Providers.Couchbase.Services;
using Hyperbee.Migrations.Providers.Couchbase.SquashCli;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
using Testcontainers.Couchbase;
using CbNetworkResolution = Couchbase.NetworkResolution;

namespace Hyperbee.Migrations.Integration.Tests.Container.Couchbase;

/// <summary>
/// Per-test Couchbase container helper for the squash integration tests.
/// Spins an isolated Testcontainers Couchbase per test class so it does
/// not contend with the shared <see cref="CouchbaseTestContainer"/>.
/// Mirrors the production <c>CouchbaseBootstrapper</c> readiness sequence
/// plus the Java Testcontainers per-bucket service-propagation wait so
/// the SDK sees n1ql in the new bucket's cluster map before the first
/// query runs.
/// </summary>
public sealed class IsolatedCouchbaseContainer : IAsyncDisposable
{
    private readonly CouchbaseContainer _container;

    public ICluster ClusterHandle { get; private set; }
    public string ConnectionString { get; private set; }
    public int MgmtPort { get; private set; }
    /// <summary>
    /// Name of the auto-created bucket provided by the Testcontainers
    /// library wait strategy (its <c>AllServicesEnabledPredicate</c>
    /// confirms KV + N1QL + Index + Search are running on the bucket).
    /// Test code should use this name -- creating a separate bucket
    /// after the cluster handle is open races the SDK's per-bucket
    /// cluster-map refresh and produces <c>SocketNotAvailableException</c>
    /// flakes on Linux CI.
    /// </summary>
    public string BucketName { get; private set; }

    private IsolatedCouchbaseContainer( CouchbaseContainer container )
    {
        _container = container;
    }

    public static async Task<IsolatedCouchbaseContainer> StartAsync(
        CancellationToken cancellationToken = default )
    {
        // Library default callback fully provisions the cluster: services,
        // alt-addresses, default bucket, credentials. PostStartConfigure
        // adds the data-service RAM bump + GSI indexer storage mode.
        var container = new CouchbaseBuilder( "couchbase:community-7.6.2" )
            .WithCleanUp( true )
            .Build();

        await container.StartAsync( cancellationToken ).ConfigureAwait( false );

        var mgmtPort = container.GetMappedPublicPort( CouchbaseBuilder.MgmtPort );

        await CouchbaseContainerSetup.PostStartConfigureAsync( container, mgmtPort, cancellationToken: cancellationToken )
            .ConfigureAwait( false );

        // Use the bucket the library wait strategy already validated as
        // "all services enabled". Creating a separate bucket here races
        // the SDK's per-bucket cluster-map refresh and produces flaky
        // SocketNotAvailableException on Linux CI; the library's bucket
        // has been verified before container.StartAsync returns and is
        // safe to open immediately.
        var libraryBucket = container.Buckets.FirstOrDefault();
        if ( libraryBucket == null )
            throw new InvalidOperationException( "CouchbaseBuilder did not pre-create a bucket; cannot proceed." );

        var instance = new IsolatedCouchbaseContainer( container )
        {
            ConnectionString = "couchbase://" + container.GetConnectionString(),
            MgmtPort = mgmtPort,
            BucketName = libraryBucket.Name
        };

        await WaitForClusterAndBucketHealthyAsync(
            instance.ConnectionString, mgmtPort, instance.BucketName, cancellationToken )
            .ConfigureAwait( false );

        instance.ClusterHandle = await Cluster.ConnectAsync( new ClusterOptions
        {
            ConnectionString = instance.ConnectionString,
            UserName = CouchbaseBuilder.DefaultUsername,
            Password = CouchbaseBuilder.DefaultPassword,
            BootstrapHttpPort = mgmtPort,
            NetworkResolution = CbNetworkResolution.External
        } ).ConfigureAwait( false );

        await instance.ClusterHandle.WaitUntilReadyAsync(
            TimeSpan.FromMinutes( 2 ),
            new WaitUntilReadyOptions().ServiceTypes( ServiceType.KeyValue, ServiceType.Query ) )
            .ConfigureAwait( false );

        var bucket = await instance.ClusterHandle.BucketAsync( instance.BucketName ).ConfigureAwait( false );
        await bucket.WaitUntilReadyAsync( TimeSpan.FromSeconds( 30 ) ).ConfigureAwait( false );

        await WarmupN1qlAsync( instance.ClusterHandle, cancellationToken ).ConfigureAwait( false );

        return instance;
    }

    private static async Task WaitForClusterAndBucketHealthyAsync(
        string connectionString, int mgmtPort, string bucketName, CancellationToken cancellationToken )
    {
        // Build a CouchbaseRestApiService against the test container's
        // mgmt port and use the production provider's wait extensions
        // (WaitUntilClusterHealthyAsync + WaitUntilBucketReadyAsync).
        // WaitUntilBucketReadyAsync combines BucketHealthyAsync (nodes
        // status == healthy) and BucketServicesReadyAsync (terse config
        // advertises kv + n1ql + alt-addresses) -- the latter is the
        // signal that prevents the SocketNotAvailableException race on
        // BucketAsync after a fresh bucket creation.
        using var http = new HttpClient();
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String( Encoding.ASCII.GetBytes(
                $"{CouchbaseBuilder.DefaultUsername}:{CouchbaseBuilder.DefaultPassword}" ) ) );

        var options = new ClusterOptions
        {
            ConnectionString = connectionString,
            UserName = CouchbaseBuilder.DefaultUsername,
            Password = CouchbaseBuilder.DefaultPassword,
            BootstrapHttpPort = mgmtPort
        };

        var restApi = new CouchbaseRestApiService(
            http,
            new OptionsWrapper<ClusterOptions>( options ),
            NullLogger<CouchbaseRestApiService>.Instance );

        await restApi.WaitUntilClusterHealthyAsync( TimeSpan.FromMinutes( 2 ), cancellationToken ).ConfigureAwait( false );
        await restApi.WaitUntilBucketReadyAsync( bucketName, TimeSpan.FromMinutes( 2 ), cancellationToken ).ConfigureAwait( false );
    }

    private static async Task WarmupN1qlAsync( ICluster cluster, CancellationToken cancellationToken )
    {
        // Per CouchbaseBootstrapper.SystemQueryWarmupAsync: the first
        // system:* query after a fresh cluster bootstrap is unreliable.
        // Run it, retry on failure, and once it succeeds the planner
        // is hot.
        var deadline = DateTime.UtcNow + TimeSpan.FromSeconds( 30 );
        Exception last = null;
        while ( DateTime.UtcNow < deadline )
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                var result = await cluster.QueryAsync<int>(
                    "SELECT RAW count(*) FROM system:indexes WHERE is_primary" )
                    .ConfigureAwait( false );
                await foreach ( var _ in result.Rows.WithCancellation( cancellationToken ).ConfigureAwait( false ) )
                    break;
                return;
            }
            catch ( Exception ex )
            {
                last = ex;
                await Task.Delay( TimeSpan.FromSeconds( 1 ), cancellationToken ).ConfigureAwait( false );
            }
        }
        throw new InvalidOperationException( "n1ql planner did not become reachable within 30 s.", last );
    }

    public async ValueTask DisposeAsync()
    {
        if ( ClusterHandle != null )
            await ClusterHandle.DisposeAsync().ConfigureAwait( false );
        await _container.DisposeAsync().ConfigureAwait( false );
    }
}
