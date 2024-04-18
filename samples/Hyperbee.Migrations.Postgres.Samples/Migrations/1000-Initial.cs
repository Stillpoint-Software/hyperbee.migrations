using Hyperbee.Migrations.Providers.Postgres.Resources;

namespace Hyperbee.Migrations.Postgres.Samples.Migrations;

[Migration( 1000 )]
public class Initial : Migration
{
    private readonly PostgresResourceRunner<Initial> _resourceRunner;

    public Initial( PostgresResourceRunner<Initial> resourceRunner )
    {
        _resourceRunner = resourceRunner;
    }

    public override async Task UpAsync( CancellationToken cancellationToken = default )
    {
        // run a `resource` migration to create initial state.
        await _resourceRunner.SqlFromAsync( [
                "CreateUsers.sql",
            ],
            cancellationToken
        );
    }
}
