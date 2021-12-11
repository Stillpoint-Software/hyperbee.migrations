using Couchbase.Extensions.DependencyInjection;
using Hyperbee.Migrations;
using Microsoft.Extensions.Logging;

namespace Hyperbee.MigrationRunner.Migrations;

[Migration(1)] 
public class MySuperAwesomeTransformation : Migration
{
    private readonly IClusterProvider _clusterProvider;
    private readonly ILogger _logger;

    public MySuperAwesomeTransformation( IClusterProvider clusterProvider, ILogger<MySuperAwesomeTransformation> logger )
    {
        _clusterProvider = clusterProvider;
        _logger = logger;
    }

    public override Task UpAsync( CancellationToken cancellationToken = default )
    {
        _logger.LogInformation( "Inside {name} `{direction}`", nameof(MySuperAwesomeTransformation), nameof(UpAsync) );
        return Task.CompletedTask;
    }

    public override Task DownAsync( CancellationToken cancellationToken = default )
    {
        _logger.LogInformation( "Inside {name} `{direction}`", nameof(MySuperAwesomeTransformation), nameof(DownAsync) );
        return Task.CompletedTask;
    }
}