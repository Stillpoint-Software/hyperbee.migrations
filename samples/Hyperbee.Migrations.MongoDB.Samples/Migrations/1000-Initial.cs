using Hyperbee.Migrations.Providers.MongoDB.Resources;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace Hyperbee.Migrations.MongoDB.Samples.Migrations;

[Migration( 1000 )]
public class Initial( IMongoClient client, MongoDBResourceRunner<Initial> resourceRunner, ILogger<Initial> logger ) : Migration
{
    public override async Task UpAsync( CancellationToken cancellationToken = default )
    {
        var db = client.GetDatabase( "administration" );
        await db.CreateCollectionAsync( "users", options: null, cancellationToken );

        // run a `resource` migration to create initial state.
        await resourceRunner.DocumentsFromAsync( [
            "administration/users/user.json"
        ], cancellationToken );
    }
}
