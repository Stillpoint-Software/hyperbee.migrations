using Aerospike.Client;
using Hyperbee.Migrations.Providers.Aerospike;
using Hyperbee.Migrations.Providers.Aerospike.Extensions;
using Microsoft.Extensions.Logging;

namespace Hyperbee.Migrations.Aerospike.Samples.Migrations;

// Code migration using raw client injection and the AerospikeClientExtensions helpers.
// Use this pattern when the DSL/resource-runner style doesn't fit, but you still want
// idempotent index creation with async readiness waits and ergonomic upserts.

[Migration( 3000 )]
public class SeedData( IAerospikeClient client, IAsyncClient asyncClient, AerospikeMigrationOptions options, ILogger<SeedData> logger ) : Migration
{
    public override async Task UpAsync( CancellationToken cancellationToken = default )
    {
        var ns = options.Namespace;

        // idempotent index creation with async readiness wait (no blocking task.Wait)

        await client.CreateIndexAsync( ns, "users", "idx_users_createdDate", "createdDate", IndexType.STRING, cancellationToken: cancellationToken );

        // ergonomic upserts — no manual Key/Bin plumbing at the call site

        await asyncClient.UpsertAsync( ns, "users", "user-003", new Bin[]
        {
            new( "name", "Bob Johnson" ),
            new( "email", "bob@example.com" ),
            new( "active", 1 ),
            new( "role", "user" ),
            new( "createdDate", "2024-06-01T09:00:00Z" )
        }, cancellationToken );

        logger.LogInformation( "Inserted user-003" );

        await asyncClient.UpsertAsync( ns, "products", "prod-003", new Bin[]
        {
            new( "name", "Doohickey" ),
            new( "category", "accessories" ),
            new( "price", 9.99 ),
            new( "active", 1 ),
            new( "createdDate", "2024-06-01T09:00:00Z" )
        }, cancellationToken );

        logger.LogInformation( "Inserted prod-003" );
    }
}
