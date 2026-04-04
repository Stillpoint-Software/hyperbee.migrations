using Hyperbee.Migrations.Providers.Couchbase.Resources;

namespace Hyperbee.Migrations.Couchbase.Samples.Migrations;

[Migration( 2000 )]
public class AddSecondaryIndexes( CouchbaseResourceRunner<AddSecondaryIndexes> resourceRunner ) : Migration
{
    public override async Task UpAsync( CancellationToken cancellationToken = default )
    {
        // create secondary GSI indexes on users and products
        await resourceRunner.StatementsFromAsync( [
            "sample/statements.json"
        ], cancellationToken );
    }
}
