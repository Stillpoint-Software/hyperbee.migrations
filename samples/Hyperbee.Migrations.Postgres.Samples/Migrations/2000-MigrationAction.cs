using Hyperbee.Migrations.Providers.Postgres.Resources;

namespace Hyperbee.Migrations.Postgres.Samples.Migrations;

[Migration( 2000, "StartMethod", "StopMethod", true )]
public class MigrationAction( PostgresResourceRunner<MigrationAction> resourceRunner ) : Migration
{
    public override async Task UpAsync( CancellationToken cancellationToken = default )
    {
        // run a `resource` migration to create initial state.
        await resourceRunner.SqlFromAsync( [
            "AddModified.sql"
        ], cancellationToken );
    }

    public async Task<bool> StartMethod()
    {
        //logic here to determine when to stop;
        return await Task.FromResult( true );
    }

    public async Task<bool> StopMethod()
    {

        //add cron helper
        //logic here to determine when to stop;
        return await Task.FromResult( true );
    }
}

