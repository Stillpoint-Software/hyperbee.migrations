using Aerospike.Client;
using Microsoft.Extensions.Logging;

namespace Hyperbee.Migrations.Aerospike.Samples.Migrations;

[Migration( 3000 )]
public class SeedData : Migration
{
    private readonly IAsyncClient _asyncClient;
    private readonly ILogger<SeedData> _logger;

    public SeedData( IAsyncClient asyncClient, ILogger<SeedData> logger )
    {
        _asyncClient = asyncClient;
        _logger = logger;
    }

    public override async Task UpAsync( CancellationToken cancellationToken = default )
    {
        // code migration: seed additional user and product data using injected Aerospike client

        _logger.LogInformation( "Seeding additional data via code migration" );

        // seed users
        await _asyncClient.Put(
            null,
            cancellationToken,
            new Key( "test", "users", "user-003" ),
            new Bin( "name", "Bob Johnson" ),
            new Bin( "email", "bob@example.com" ),
            new Bin( "active", 1 ),
            new Bin( "role", "user" ),
            new Bin( "createdDate", "2024-06-01T09:00:00Z" )
        ).ConfigureAwait( false );

        _logger.LogInformation( "Inserted user-003" );

        // seed products
        await _asyncClient.Put(
            null,
            cancellationToken,
            new Key( "test", "products", "prod-003" ),
            new Bin( "name", "Doohickey" ),
            new Bin( "category", "accessories" ),
            new Bin( "price", 9.99 ),
            new Bin( "active", 1 ),
            new Bin( "createdDate", "2024-06-01T09:00:00Z" )
        ).ConfigureAwait( false );

        _logger.LogInformation( "Inserted prod-003" );

        _logger.LogInformation( "Seed data migration completed" );
    }
}
