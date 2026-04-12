using Hyperbee.Migrations.Providers.MongoDB.Resources;

namespace Hyperbee.Migrations.MongoDB.Samples.Migrations;

[Migration( 2000 )]
public class AddSecondaryIndexes( MongoDBResourceRunner<AddSecondaryIndexes> resourceRunner ) : Migration
{
    public override async Task UpAsync( CancellationToken cancellationToken = default )
    {
        // create secondary indexes on users and products collections
        await resourceRunner.StatementsFromAsync( [
            "statements.json"
        ], cancellationToken );
    }
}
