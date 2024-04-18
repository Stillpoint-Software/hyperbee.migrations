using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hyperbee.Migrations.Providers.Postgres;

internal class PostgresRecordStore : IMigrationRecordStore
{
    private readonly NpgsqlDataSource _dataSource;
    private readonly PostgresMigrationOptions _options;
    private readonly ILogger<PostgresRecordStore> _logger;

    public PostgresRecordStore(
        NpgsqlDataSource dataSource,
        PostgresMigrationOptions options,
        ILogger<PostgresRecordStore> logger )
    {
        _dataSource = dataSource;
        _options = options;
        _logger = logger;
    }

    public async Task InitializeAsync( CancellationToken cancellationToken = default )
    {
        // wait for system ready
        _logger.LogDebug( "Running {action}", nameof(InitializeAsync) );

        var createCommand = _dataSource.CreateCommand( CreateMigrationTable() );
        await createCommand.ExecuteNonQueryAsync( cancellationToken );
    }

    public async Task<IDisposable> CreateLockAsync()
    {
        _logger.LogDebug( "Running {action}", nameof(CreateLockAsync) );

        var getLockCommand = _dataSource.CreateCommand( GetMigrationLock() );
        var migrationLock = await getLockCommand.ExecuteScalarAsync();
        if ( migrationLock != null )
        {
            _logger.LogWarning( "{action} Lock already exists", nameof(CreateLockAsync) );
            throw new MigrationLockUnavailableException( $"The lock `{_options.LockName}` is unavailable." );
        }

        try
        {
            var command = _dataSource.CreateCommand( InsertMigrationLock() );
            await command.ExecuteNonQueryAsync();
        }
        catch ( Exception ex )
        {
            _logger.LogError( ex, "{action} unable to create database lock", nameof(CreateLockAsync) );
            throw new MigrationLockUnavailableException( $"The lock `{_options.LockName}` is unavailable.", ex );
        }

        return new Disposable( () =>
        {
            _logger.LogInformation( "{action} disposing lock", nameof(CreateLockAsync) );

            try
            {
                var command = _dataSource.CreateCommand( DeleteMigrationLock() );
                command.ExecuteNonQuery();
            }
            catch ( Exception ex )
            {
                _logger.LogCritical( ex, "{action} unable to remove database lock", nameof(CreateLockAsync) );
                throw;
            }
        } );
    }

    public async Task<bool> ExistsAsync( string recordId )
    {
        _logger.LogDebug( "Running {action} with `{recordId}`", nameof(ExistsAsync), recordId );

        var command = _dataSource.CreateCommand( GetMigrationRecord( recordId ) );
        var id = await command.ExecuteScalarAsync();

        _logger.LogDebug( "{action} found `{recordId}`", nameof(ExistsAsync), id );

        return id != null;
    }

    public async Task DeleteAsync( string recordId )
    {
        _logger.LogDebug( "Running {action} with `{recordId}`", nameof(DeleteAsync), recordId );

        var command = _dataSource.CreateCommand( DeleteMigrationRecord( recordId ) );
        await command.ExecuteNonQueryAsync();
    }

    public async Task WriteAsync( string recordId )
    {
        _logger.LogDebug( "Running {action} with `{recordId}`", nameof(WriteAsync), recordId );

        var command = _dataSource.CreateCommand( InsertMigrationRecord( recordId ) );
        await command.ExecuteNonQueryAsync();
    }

    private string MigrationTableName => $"{_options.SchemaName}.{_options.TableName}";
    private string LockTableName => $"{_options.SchemaName}.{_options.LockName}";

    private string CreateMigrationTable() =>
        $"""
         CREATE SCHEMA IF NOT EXISTS {_options.SchemaName};

         CREATE TABLE IF NOT EXISTS {MigrationTableName}
         (
             record_id  character varying(255) PRIMARY KEY,
             run_on     timestamp without time zone NOT NULL
         );

         CREATE TABLE IF NOT EXISTS {LockTableName}
         (
             id        integer PRIMARY KEY,
             locked_on timestamp without time zone NOT NULL
         );
         """;

    private string GetMigrationRecord( string recordId ) => $"SELECT record_id FROM {MigrationTableName} WHERE record_id = '{recordId}'";

    private string InsertMigrationRecord( string recordId ) => $"INSERT INTO {MigrationTableName} (record_id, run_on) VALUES ('{recordId}', NOW())";

    private string DeleteMigrationRecord( string recordId ) => $"DELETE FROM {MigrationTableName} WHERE record_id = '{recordId}'";

    private string GetMigrationLock() => $"SELECT locked_on FROM {LockTableName} LIMIT 1";

    private string InsertMigrationLock() => $"INSERT INTO {LockTableName} (id, locked_on) VALUES (1, NOW())";

    private string DeleteMigrationLock() => $"DELETE FROM {LockTableName}";


    private sealed class Disposable( Action dispose ) : IDisposable
    {
        private int _disposed;
        private Action Disposer { get; } = dispose;

        public void Dispose()
        {
            if ( Interlocked.CompareExchange( ref _disposed, 1, 0 ) == 0 )
                Disposer.Invoke();
        }
    }
}
