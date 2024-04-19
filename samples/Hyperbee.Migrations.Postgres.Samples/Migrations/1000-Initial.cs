using Hyperbee.Migrations.Providers.Postgres.Resources;

namespace Hyperbee.Migrations.Postgres.Samples.Migrations;

[Migration( 1000 )]
public class Initial(PostgresResourceRunner<Initial> resourceRunner) : Migration
{
    public override async Task UpAsync( CancellationToken cancellationToken = default )
    {
        // run a `resource` migration to create initial state.
        await resourceRunner.AllSqlFromAsync( cancellationToken );
    }
}
