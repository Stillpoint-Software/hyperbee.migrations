using Hyperbee.Migrations.Helper;
using Hyperbee.Migrations.Providers.Postgres.Resources;

namespace Hyperbee.Migrations.Postgres.Samples.Migrations;

[Migration( 2000, "StartMethod", "StopMethod", true )]
public class MigrationAction( PostgresResourceRunner<MigrationAction> resourceRunner ) : Migration
{
    private int _count = 0;
    public override async Task UpAsync( CancellationToken cancellationToken = default )
    {
        // run a `resource` migration to create initial state.
        await resourceRunner.SqlFromAsync( [
            "AddModified.sql"
        ], cancellationToken );
    }

    public async Task<bool> StartMethod()
    {
        _count++;
        var helper = new MigrationCronHelper();
        var results = await helper.CronDelayAsync( "* * * * *" );
        return results;
    }

    public Task<bool> StopMethod()
    {
        if ( _count > 2 )
        {
            return Task.FromResult( true );
        }
        return Task.FromResult( false );
    }
}

