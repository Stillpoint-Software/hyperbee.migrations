using Hyperbee.Migrations.Providers.MongoDB.Resources;

namespace Hyperbee.Migrations.MongoDB.Samples.Migrations;

[Migration( 1000 )]
public class Initial( MongoDBResourceRunner<Initial> resourceRunner ) : Migration
{
    public override async Task UpAsync( CancellationToken cancellationToken = default )
    {
        // run a `resource` migration to create initial state.
        await resourceRunner.DocumentsFromAsync( [
            "administration/users/user.json"
        ], cancellationToken );
    }
}
