using Couchbase.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Hyperbee.Migrations.Samples.Migrations;

[Migration(2000)] 
public class SecondaryAction : Migration
{
    private readonly IClusterProvider _clusterProvider;
    private readonly ILogger<CreateInitialBuckets> _logger;

    public SecondaryAction( IClusterProvider clusterProvider, ILogger<CreateInitialBuckets> logger )
    {
        _clusterProvider = clusterProvider;
        _logger = logger;
    }

    public override async Task UpAsync( CancellationToken cancellationToken = default )
    {
        // N1Ql migration
        await Task.CompletedTask;
    }
}