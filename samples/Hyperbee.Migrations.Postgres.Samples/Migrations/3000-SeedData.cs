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
        // code migration: use the injected NpgsqlDataSource to run SQL directly

        _logger.LogInformation( "Running Postgres code migration" );

        await using var connection = await _dataSource.OpenConnectionAsync( cancellationToken );

        // seed initial data using parameterized queries
        await using var cmd = new NpgsqlCommand(
            @"INSERT INTO administration.user (name, email, active, created_by, created_date)
              VALUES (@name, @email, @active, @created_by, @created_date)
              ON CONFLICT DO NOTHING",
            connection
        );

        cmd.Parameters.AddWithValue( "name", "Admin User" );
        cmd.Parameters.AddWithValue( "email", "admin@example.com" );
        cmd.Parameters.AddWithValue( "active", true );
        cmd.Parameters.AddWithValue( "created_by", "migration" );
        cmd.Parameters.AddWithValue( "created_date", DateTimeOffset.UtcNow );

        await cmd.ExecuteNonQueryAsync( cancellationToken );

        _logger.LogInformation( "Postgres code migration completed" );
    }
}
