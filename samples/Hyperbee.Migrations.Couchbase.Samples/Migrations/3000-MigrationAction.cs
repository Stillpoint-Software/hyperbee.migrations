using Hyperbee.Migrations.Providers.Couchbase.Resources;

namespace Hyperbee.Migrations.Couchbase.Samples.Migrations;

[Migration( 3000, "StartMethod", "StopMethod" )]
public class MigrationAction : Migration
{
    private readonly CouchbaseResourceRunner<MigrationAction> _resourceRunner;


    public MigrationAction( CouchbaseResourceRunner<MigrationAction> resourceRunner )
    {
        _resourceRunner = resourceRunner;
    }

    public override async Task UpAsync( CancellationToken cancellationToken = default )
    {
        await _resourceRunner.DocumentsFromAsync( new[] { "migrationbucket/_default" },
            cancellationToken
        );

    }

    public async Task<bool> StartMethod()
    {
        //logic here to determine when to stop;
        return await Task.FromResult( true );
    }

    public async Task<bool> StopMethod()
    {
        //logic here to determine when to stop;
        return await Task.FromResult( true );
    }
}
