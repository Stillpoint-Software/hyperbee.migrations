using Couchbase.Extensions.DependencyInjection;
using Couchbase.Query;
using Microsoft.Extensions.Logging;

namespace Hyperbee.Migrations.Couchbase.Samples.Migrations;

[Migration( 2000 )]
public class SecondaryAction : Migration
{
    private readonly IClusterProvider _clusterProvider;
    private readonly ILogger<SecondaryAction> _logger;

    public SecondaryAction( IClusterProvider clusterProvider, ILogger<SecondaryAction> logger )
    {
        _clusterProvider = clusterProvider;
        _logger = logger;
    }

    public override async Task UpAsync( CancellationToken cancellationToken = default )
    {
        // code migration: use the injected cluster provider to run N1QL directly

        var cluster = await _clusterProvider.GetClusterAsync().ConfigureAwait( false );
        var options = new QueryOptions().CancellationToken( cancellationToken );

        _logger.LogInformation( "Running N1QL code migration" );

        // create a secondary index using N1QL
        await cluster.QueryAsync<dynamic>(
            "CREATE INDEX idx_migrationbucket_createdTimestamp ON `migrationbucket`(`createdTimestamp`) WITH { \"defer_build\": true }",
            options
        ).ConfigureAwait( false );

        // build deferred indexes
        await cluster.QueryAsync<dynamic>(
            "BUILD INDEX ON `migrationbucket`(idx_migrationbucket_createdTimestamp)",
            options
        ).ConfigureAwait( false );

        _logger.LogInformation( "N1QL code migration completed" );
    }
}
