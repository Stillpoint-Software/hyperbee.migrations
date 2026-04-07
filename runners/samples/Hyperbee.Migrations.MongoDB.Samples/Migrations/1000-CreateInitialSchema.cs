using Hyperbee.Migrations.Providers.MongoDB.Resources;

namespace Hyperbee.Migrations.MongoDB.Samples.Migrations;

[Migration( 1000 )]
public class CreateInitialSchema( MongoDBResourceRunner<CreateInitialSchema> resourceRunner ) : Migration
{
    public override async Task UpAsync( CancellationToken cancellationToken = default )
    {
        // seed initial user and product documents
        await resourceRunner.DocumentsFromAsync( [
            "sample/users",
            "sample/products"
        ], cancellationToken );
    }
}
