using Hyperbee.Migrations.Resources;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hyperbee.Migrations.Providers.Postgres.Resources;

public class PostgresResourceRunner<TMigration>
    where TMigration : Migration
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly ILogger _logger;

    public PostgresResourceRunner(
        NpgsqlDataSource dataSource,
        ILogger<TMigration> logger )
    {
        _dataSource = dataSource;
        _logger = logger;
    }

    // sql

    public Task SqlFromAsync( string resourceName, CancellationToken cancellationToken = default )
    {
        return SqlFromAsync( new[] { resourceName }, default, cancellationToken );
    }

    public Task SqlFromAsync( string resourceName, TimeSpan? timeout, CancellationToken cancellationToken = default )
    {
        return SqlFromAsync( new[] { resourceName }, timeout, cancellationToken );
    }

    public Task SqlFromAsync( string[] resourceNames, CancellationToken cancellationToken = default )
    {
        return SqlFromAsync( resourceNames, default, cancellationToken );
    }

    public async Task SqlFromAsync( string[] resourceNames, TimeSpan? timeout, CancellationToken cancellationToken = default )
    {
        var migrationName = Migration.VersionedName<TMigration>();

        foreach ( var statement in ReadResources() )
        {
            await using var command = _dataSource.CreateCommand( statement );

            if ( timeout.HasValue )
            {
                command.CommandTimeout = timeout.Value.Seconds;
            }

            try
            {
                await command.ExecuteNonQueryAsync( cancellationToken );
            }
            catch ( Exception ex )
            {
                _logger.LogError( ex, "Error executing statement: `{statement}`", statement );
                throw;
            }
        }

        return;

        IEnumerable<string> ReadResources()
        {
            foreach ( var resourceName in resourceNames )
            {
                var resource = $"{migrationName}.{resourceName}";
                _logger.LogInformation( " - Resource: [{resource}]", resource );

                var statement = ResourceHelper.GetResource<TMigration>( resource );
                yield return statement;
            }
        }
    }

    public Task AllSqlFromAsync( CancellationToken cancellationToken = default )
    {
        return AllSqlFromAsync( timeout: null, cancellationToken );
    }

    public async Task AllSqlFromAsync( TimeSpan? timeout = null, CancellationToken cancellationToken = default )
    {
        var migrationName = Migration.VersionedName<TMigration>();

        foreach ( var statement in ReadResources() )
        {
            await using var command = _dataSource.CreateCommand( statement );

            if ( timeout.HasValue )
            {
                command.CommandTimeout = timeout.Value.Seconds;
            }

            try
            {
                await command.ExecuteNonQueryAsync( cancellationToken );
            }
            catch ( Exception ex )
            {
                _logger.LogError( ex, "Error executing statement: [{statement}]", statement );
                throw;
            }
        }

        return;

        IEnumerable<string> ReadResources()
        {
            var resourceNames = ResourceHelper.GetResourceNames<TMigration>( migrationName );

            foreach ( var resourceName in resourceNames )
            {
                _logger.LogInformation( " - Resource: [{resource}]", resourceName );

                var statement = ResourceHelper.GetResource<TMigration>( resourceName, true );
                yield return statement;
            }
        }
    }
}
