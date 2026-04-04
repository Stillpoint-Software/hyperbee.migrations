using Hyperbee.Migrations.Providers.Aerospike.Resources;

namespace Hyperbee.Migrations.Aerospike.Samples.Migrations;

[Migration( 2000 )]
public class AddSecondaryIndexes( AerospikeResourceRunner<AddSecondaryIndexes> resourceRunner ) : Migration
{
    public override async Task UpAsync( CancellationToken cancellationToken = default )
    {
        // create additional indexes for users and products
        await resourceRunner.StatementsFromAsync( [
            "statements.json"
        ], cancellationToken );

        // seed user and product data
        await resourceRunner.DocumentsFromAsync( [
            "test/users",
            "test/products"
        ], cancellationToken );
    }
}
