using Hyperbee.Migrations.Providers.Aerospike.Resources;

namespace Hyperbee.Migrations.Aerospike.Samples.Migrations;

[Migration( 1000 )]
public class CreateInitialSets( AerospikeResourceRunner<CreateInitialSets> resourceRunner ) : Migration
{
    public override async Task UpAsync( CancellationToken cancellationToken = default )
    {
        // create initial indexes (WAIT ensures indexes are built before proceeding)
        await resourceRunner.StatementsFromAsync( [
            "statements.json"
        ], cancellationToken );

        // seed initial user data
        await resourceRunner.DocumentsFromAsync( [
            "test/users"
        ], cancellationToken );
    }
}
