using Hyperbee.Migrations.Providers.Aerospike.Resources;

namespace Hyperbee.Migrations.Aerospike.Samples.Migrations;

[Migration( 2000 )]
public class AddSecondaryIndexes( AerospikeResourceRunner<AddSecondaryIndexes> resourceRunner ) : Migration
{
    public override async Task UpAsync( CancellationToken cancellationToken = default )
    {
        // create additional indexes and seed data
        await resourceRunner.StatementsFromAsync( [
            "statements.json"
        ], cancellationToken );

        await resourceRunner.DocumentsFromAsync( [
            "test/users"
        ], cancellationToken );
    }
}
