using Hyperbee.Migrations.Providers.MongoDB.Resources;

namespace Hyperbee.Migrations.MongoDB.Samples.Migrations;

[Migration( 2000, "StartMethod", "StopMethod", true )]
public class MigrationAction( MongoDBResourceRunner<MigrationAction> resourceRunner ) : Migration
{
    public override async Task UpAsync( CancellationToken cancellationToken = default )
    {
        // run a `resource` migration to create initial state.
        await resourceRunner.DocumentsFromAsync( [
            "administration/adduser.json"
        ], cancellationToken );
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
