using Microsoft.Extensions.Logging;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Hyperbee.Migrations.MongoDB.Samples.Migrations;

[Migration( 3000 )]
public class SeedData : Migration
{
    private readonly IMongoClient _client;
    private readonly ILogger<SeedData> _logger;

    public SeedData( IMongoClient client, ILogger<SeedData> logger )
    {
        _client = client;
        _logger = logger;
    }

    public override async Task UpAsync( CancellationToken cancellationToken = default )
    {
        // code migration: seed additional data using the injected IMongoClient

        _logger.LogInformation( "Seeding additional data via code migration" );

        var db = _client.GetDatabase( "sample" );

        // seed a user
        var users = db.GetCollection<BsonDocument>( "users" );

        await users.InsertOneAsync( new BsonDocument
        {
            { "userId", 3 },
            { "name", "Bob Johnson" },
            { "email", "bob@example.com" },
            { "active", true },
            { "role", "user" },
            { "createdDate", "2024-06-01T09:00:00Z" }
        }, cancellationToken: cancellationToken );

        _logger.LogInformation( "Inserted user 3" );

        // seed a product
        var products = db.GetCollection<BsonDocument>( "products" );

        await products.InsertOneAsync( new BsonDocument
        {
            { "productId", 3 },
            { "name", "Doohickey" },
            { "category", "accessories" },
            { "price", 9.99 },
            { "active", true },
            { "createdDate", "2024-06-01T09:00:00Z" }
        }, cancellationToken: cancellationToken );

        _logger.LogInformation( "Inserted product 3" );

        _logger.LogInformation( "Seed data migration completed" );
    }
}
