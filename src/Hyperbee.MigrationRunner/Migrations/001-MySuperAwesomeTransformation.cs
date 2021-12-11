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

    public override Task Up()
    {
        _logger.LogInformation( "Inside {name} `{direction}`", nameof(MySuperAwesomeTransformation), nameof(Up) );
        return Task.CompletedTask;
    }

    public override Task Down()
    {
        _logger.LogInformation( "Inside {name} `{direction}`", nameof(MySuperAwesomeTransformation), nameof(Down) );
        return Task.CompletedTask;
    }
}