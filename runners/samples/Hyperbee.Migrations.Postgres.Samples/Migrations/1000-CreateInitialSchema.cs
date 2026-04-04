using Hyperbee.Migrations.Providers.Postgres.Resources;

namespace Hyperbee.Migrations.Postgres.Samples.Migrations;

[Migration( 1000 )]
public class CreateInitialSchema( PostgresResourceRunner<CreateInitialSchema> resourceRunner ) : Migration
{
    public override async Task UpAsync( CancellationToken cancellationToken = default )
    {
        // create the sample schema, users table, and products table
        await resourceRunner.AllSqlFromAsync( cancellationToken );
    }
}
