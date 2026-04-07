using Hyperbee.Migrations.Providers.Couchbase.Resources;

namespace Hyperbee.Migrations.Couchbase.Samples.Migrations;

[Migration( 1000 )]
public class CreateInitialSchema( CouchbaseResourceRunner<CreateInitialSchema> resourceRunner ) : Migration
{
    public override async Task UpAsync( CancellationToken cancellationToken = default )
    {
        // create the sample bucket and primary index
        await resourceRunner.StatementsFromAsync( [
            "statements.json",
            "sample/statements.json"
        ], cancellationToken );

        // seed initial user documents
        await resourceRunner.DocumentsFromAsync( [
            "sample/_default"
        ], cancellationToken );
    }
}
