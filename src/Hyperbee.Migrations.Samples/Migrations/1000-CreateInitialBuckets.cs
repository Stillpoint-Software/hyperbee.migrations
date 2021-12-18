using Couchbase.Extensions.DependencyInjection;
using Hyperbee.Migrations.Couchbase;
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
        // run a `resource` migration to create initial buckets and state.
        // resource migrations are atypical; prefer `n1ql` migrations.

        var clusterHelper = await _clusterProvider.GetClusterHelperAsync();

        await clusterHelper.CreateBucketsFromResourcesAsync(
            _logger,
            VersionedName(),
            "buckets.json",
            waitInterval: TimeSpan.FromSeconds( 3 ),
            maxAttempts: 10
        );

        await clusterHelper.CreateIndexesFromResourcesAsync( 
            _logger,
            VersionedName(), 
            "cloudc/indexes.json", 
            "wagglebee/indexes.json", 
            "wagglebeecache/indexes.json" 
        );

        await clusterHelper.CreateDocumentsFromResourcesAsync(
            _logger,
            VersionedName(),
            "cloudc/_default",
            "wagglebee/_default",
            "wagglebeecache/_default"
        );
    }
}