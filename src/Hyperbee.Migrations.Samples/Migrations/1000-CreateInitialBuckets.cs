using Couchbase.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Hyperbee.Migrations.Samples.Migrations;

[Migration(1000)] 
public class CreateInitialBuckets : Migration
{
    private readonly IClusterProvider _clusterProvider;
    private readonly ILogger _logger;

    public CreateInitialBuckets( IClusterProvider clusterProvider, ILogger<CreateInitialBuckets> logger )
    {
        _clusterProvider = clusterProvider;
        _logger = logger;
    }

    public override async Task UpAsync( CancellationToken cancellationToken = default )
    {
        // run a `resource` migration to create initial buckets, indexes, and data.
        // `resource` migrations are atypical; prefer `n1ql` migrations.

        await CouchbaseResourceHelper.CreateBucketsFromResourcesAsync(
            _clusterProvider,
            _logger,
            VersionedName(),
            "buckets.json",
            waitInterval: TimeSpan.FromSeconds( 3 ),
            maxAttempts: 10
        );

        await CouchbaseResourceHelper.CreateIndexesFromResourcesAsync( 
            _clusterProvider, 
            _logger,
            VersionedName(), 
            "cloudc/indexes.json", 
            "wagglebee/indexes.json", 
            "wagglebeecache/indexes.json" 
        );

        await CouchbaseResourceHelper.CreateDataFromResourcesAsync(
            _clusterProvider,
            _logger,
            VersionedName(),
            "cloudc/_default",
            "wagglebee/_default",
            "wagglebeecache/_default"
        );
    }
}