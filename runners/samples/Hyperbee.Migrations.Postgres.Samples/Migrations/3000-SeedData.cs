using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hyperbee.Migrations.Postgres.Samples.Migrations;

[Migration( 3000 )]
public class SeedData : Migration
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger<SeedData> _logger;

    public SeedData( NpgsqlDataSource dataSource, ILogger<SeedData> logger )
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    public override async Task UpAsync( CancellationToken cancellationToken = default )
    {
        // code migration: seed data using the injected NpgsqlDataSource

        _logger.LogInformation( "Seeding data via code migration" );

        await using var connection = await _dataSource.OpenConnectionAsync( cancellationToken );

        // seed users
        await using ( var cmd = new NpgsqlCommand(
            @"INSERT INTO sample.users (name, email, active, role, created_date)
              VALUES (@name, @email, @active, @role, @created_date)
              ON CONFLICT DO NOTHING",
            connection ) )
        {
            cmd.Parameters.AddWithValue( "name", "Admin User" );
            cmd.Parameters.AddWithValue( "email", "admin@example.com" );
            cmd.Parameters.AddWithValue( "active", true );
            cmd.Parameters.AddWithValue( "role", "admin" );
            cmd.Parameters.AddWithValue( "created_date", DateTimeOffset.Parse( "2024-01-01T00:00:00Z" ) );
            await cmd.ExecuteNonQueryAsync( cancellationToken );
        }

        await using ( var cmd = new NpgsqlCommand(
            @"INSERT INTO sample.users (name, email, active, role, created_date)
              VALUES (@name, @email, @active, @role, @created_date)
              ON CONFLICT DO NOTHING",
            connection ) )
        {
            cmd.Parameters.AddWithValue( "name", "John Doe" );
            cmd.Parameters.AddWithValue( "email", "john@example.com" );
            cmd.Parameters.AddWithValue( "active", true );
            cmd.Parameters.AddWithValue( "role", "user" );
            cmd.Parameters.AddWithValue( "created_date", DateTimeOffset.Parse( "2024-01-15T10:30:00Z" ) );
            await cmd.ExecuteNonQueryAsync( cancellationToken );
        }

        _logger.LogInformation( "Seeded users" );

        // seed products
        await using ( var cmd = new NpgsqlCommand(
            @"INSERT INTO sample.products (name, category, price, active, created_date)
              VALUES (@name, @category, @price, @active, @created_date)
              ON CONFLICT DO NOTHING",
            connection ) )
        {
            cmd.Parameters.AddWithValue( "name", "Widget" );
            cmd.Parameters.AddWithValue( "category", "hardware" );
            cmd.Parameters.AddWithValue( "price", 29.99m );
            cmd.Parameters.AddWithValue( "active", true );
            cmd.Parameters.AddWithValue( "created_date", DateTimeOffset.Parse( "2024-03-01T08:00:00Z" ) );
            await cmd.ExecuteNonQueryAsync( cancellationToken );
        }

        await using ( var cmd = new NpgsqlCommand(
            @"INSERT INTO sample.products (name, category, price, active, created_date)
              VALUES (@name, @category, @price, @active, @created_date)
              ON CONFLICT DO NOTHING",
            connection ) )
        {
            cmd.Parameters.AddWithValue( "name", "Gadget" );
            cmd.Parameters.AddWithValue( "category", "electronics" );
            cmd.Parameters.AddWithValue( "price", 49.99m );
            cmd.Parameters.AddWithValue( "active", true );
            cmd.Parameters.AddWithValue( "created_date", DateTimeOffset.Parse( "2024-03-15T12:00:00Z" ) );
            await cmd.ExecuteNonQueryAsync( cancellationToken );
        }

        _logger.LogInformation( "Seeded products" );

        _logger.LogInformation( "Seed data migration completed" );
    }
}
