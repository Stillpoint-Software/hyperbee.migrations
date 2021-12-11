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
        _logger.LogDebug( $"In Up `{nameof(MySuperAwesomeTransformation)}`" );
        return Task.CompletedTask;
    }

    public override Task Down()
    {
        _logger.LogDebug( $"In Down `{nameof(MySuperAwesomeTransformation)}`" );
        return Task.CompletedTask;
    }
}