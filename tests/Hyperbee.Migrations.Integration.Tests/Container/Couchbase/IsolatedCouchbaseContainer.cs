using Couchbase;
using Couchbase.Management.Buckets;
using Testcontainers.Couchbase;

namespace Hyperbee.Migrations.Integration.Tests.Container.Couchbase;

/// <summary>
/// Per-test Couchbase container helper for the squash integration tests
/// (F-1 / RB-5 close-out). The shared <see cref="CouchbaseTestContainer"/>
/// is consumed by the runner test (<c>CouchbaseRunnerTest</c>) which
/// connects via the Docker-network alias; the squash tests previously
/// needed alt-address configuration on the shared container which broke
/// the runner test's cluster-map. Spinning an isolated container per
/// squash test class eliminates the conflict so the suite no longer needs
/// the <c>[TestCategory("LocalOnly")]</c> tag.
/// </summary>
public sealed class IsolatedCouchbaseContainer : IAsyncDisposable
{
    private readonly CouchbaseContainer _container;

    public ICluster ClusterHandle { get; private set; }
    public string ConnectionString { get; private set; }
    public int MgmtPort { get; private set; }

    private IsolatedCouchbaseContainer( CouchbaseContainer container )
    {
        _container = container;
    }

    public static async Task<IsolatedCouchbaseContainer> StartAsync(
        string bucketName,
        CancellationToken cancellationToken = default )
    {
        var container = new CouchbaseBuilder()
            .WithCleanUp( true )
            .Build();

        await container.StartAsync( cancellationToken ).ConfigureAwait( false );

        var instance = new IsolatedCouchbaseContainer( container )
        {
            ConnectionString = container.GetConnectionString() + "?network=external",
            MgmtPort = container.GetMappedPublicPort( CouchbaseBuilder.MgmtPort )
        };

        instance.ClusterHandle = await Cluster.ConnectAsync( new ClusterOptions
        {
            ConnectionString = instance.ConnectionString,
            UserName = CouchbaseBuilder.DefaultUsername,
            Password = CouchbaseBuilder.DefaultPassword
        } ).ConfigureAwait( false );

        await instance.ClusterHandle.WaitUntilReadyAsync( TimeSpan.FromMinutes( 2 ) ).ConfigureAwait( false );

        // Create the bucket the test needs (CouchbaseBuilder doesn't
        // pre-create one).
        try
        {
            await instance.ClusterHandle.Buckets.CreateBucketAsync( new BucketSettings
            {
                Name = bucketName,
                BucketType = BucketType.Couchbase,
                RamQuotaMB = 100
            } ).ConfigureAwait( false );

            // Wait for bucket to actually be ready.
            var bucketReady = false;
            for ( var i = 0; i < 60 && !bucketReady; i++ )
            {
                try
                {
                    var bucket = await instance.ClusterHandle.BucketAsync( bucketName ).ConfigureAwait( false );
                    await bucket.WaitUntilReadyAsync( TimeSpan.FromSeconds( 5 ) ).ConfigureAwait( false );
                    bucketReady = true;
                }
                catch
                {
                    await Task.Delay( TimeSpan.FromSeconds( 1 ), cancellationToken ).ConfigureAwait( false );
                }
            }
        }
        catch ( BucketExistsException )
        {
            // Already exists -- fine.
        }

        return instance;
    }

    public async ValueTask DisposeAsync()
    {
        if ( ClusterHandle != null )
            await ClusterHandle.DisposeAsync().ConfigureAwait( false );
        await _container.DisposeAsync().ConfigureAwait( false );
    }
}
