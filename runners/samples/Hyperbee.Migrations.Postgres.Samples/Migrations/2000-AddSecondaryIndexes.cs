using Hyperbee.Migrations.Providers.Postgres.Resources;

namespace Hyperbee.Migrations.Postgres.Samples.Migrations;

[Migration( 2000 )]
public class AddSecondaryIndexes( PostgresResourceRunner<AddSecondaryIndexes> resourceRunner ) : Migration
{
    public override async Task UpAsync( CancellationToken cancellationToken = default )
    {
        // create secondary indexes on users and products tables
        await resourceRunner.AllSqlFromAsync( cancellationToken );
    }
}
