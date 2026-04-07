using Couchbase.Extensions.DependencyInjection;
using Couchbase.KeyValue;
using Microsoft.Extensions.Logging;

namespace Hyperbee.Migrations.Couchbase.Samples.Migrations;

[Migration( 3000 )]
public class SeedData : Migration
{
    private readonly IClusterProvider _clusterProvider;
    private readonly ILogger<SeedData> _logger;

    public SeedData( IClusterProvider clusterProvider, ILogger<SeedData> logger )
    {
        _clusterProvider = clusterProvider;
        _logger = logger;
    }

    public override async Task UpAsync( CancellationToken cancellationToken = default )
    {
        // code migration: seed additional data using the injected cluster provider

        _logger.LogInformation( "Seeding additional data via code migration" );

        var cluster = await _clusterProvider.GetClusterAsync().ConfigureAwait( false );
        var bucket = await cluster.BucketAsync( "sample" ).ConfigureAwait( false );
        var collection = bucket.DefaultCollection();

        // seed a user
        await collection.UpsertAsync( "user::003", new
        {
            userId = 3,
            name = "Bob Johnson",
            email = "bob@example.com",
            active = true,
            role = "user",
            createdDate = "2024-06-01T09:00:00Z"
        } ).ConfigureAwait( false );

        _logger.LogInformation( "Inserted user::003" );

        // seed a product
        await collection.UpsertAsync( "product::003", new
        {
            productId = 3,
            name = "Doohickey",
            category = "accessories",
            price = 9.99,
            active = true,
            createdDate = "2024-06-01T09:00:00Z"
        } ).ConfigureAwait( false );

        _logger.LogInformation( "Inserted product::003" );

        _logger.LogInformation( "Seed data migration completed" );
    }
}
