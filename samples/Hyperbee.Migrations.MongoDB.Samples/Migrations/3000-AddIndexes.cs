using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Hyperbee.Migrations.MongoDB.Samples.Migrations;

[Migration( 3000 )]
public class AddIndexes : Migration
{
    private readonly IMongoClient _client;
    private readonly ILogger<AddIndexes> _logger;

    public AddIndexes( IMongoClient client, ILogger<AddIndexes> logger )
    {
        _client = client;
        _logger = logger;
    }

    public override async Task UpAsync( CancellationToken cancellationToken = default )
    {
        // code migration: use the injected IMongoClient to run operations directly

        var db = _client.GetDatabase( "administration" );

        _logger.LogInformation( "Running MongoDB code migration" );

        // ensure the collection exists
        var collections = await (await db.ListCollectionNamesAsync( cancellationToken: cancellationToken ))
            .ToListAsync( cancellationToken );

        if ( !collections.Contains( "users" ) )
            await db.CreateCollectionAsync( "users", cancellationToken: cancellationToken );

        // create indexes using the driver directly
        var usersCollection = db.GetCollection<BsonDocument>( "users" );

        await usersCollection.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending( "email" ),
                new CreateIndexOptions { Name = "idx_email", Unique = true }
            ),
            cancellationToken: cancellationToken
        );

        await usersCollection.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending( "active" ),
                new CreateIndexOptions { Name = "idx_active" }
            ),
            cancellationToken: cancellationToken
        );

        _logger.LogInformation( "MongoDB code migration completed" );
    }
}
