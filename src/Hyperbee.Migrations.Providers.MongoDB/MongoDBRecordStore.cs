using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace Hyperbee.Migrations.Providers.MongoDB;

internal class MongoDBRecordStore : IMigrationRecordStore
{
    private readonly IMongoClient _client;
    private readonly MongoDBMigrationOptions _options;
    private readonly ILogger<MongoDBRecordStore> _logger;

    public MongoDBRecordStore(
        IMongoClient client,
        MongoDBMigrationOptions options,
        ILogger<MongoDBRecordStore> logger )
    {
        _client = client;
        _options = options;
        _logger = logger;
    }

    public Task InitializeAsync( CancellationToken cancellationToken = default )
    {
        // wait for system ready
        _logger.LogDebug( "Running {action}", nameof( InitializeAsync ) );

        var db = _client.GetDatabase( _options.DatabaseName );
        db.GetCollection<MigrationRecord>( _options.CollectionName );

        return Task.CompletedTask;
    }

    public async Task<IDisposable> CreateLockAsync()
    {
        _logger.LogDebug( "Running {action}", nameof( CreateLockAsync ) );

        var db = _client.GetDatabase( _options.DatabaseName );
        var collection = db.GetCollection<MigrationLock>( _options.CollectionName );
        using var cursor = await collection.FindAsync( x => x.Id == 1 );
        var migrationLock = await cursor.FirstOrDefaultAsync();

        if ( migrationLock != null )
        {
            _logger.LogWarning( "{action} Lock already exists", nameof( CreateLockAsync ) );

            if ( migrationLock.ReleaseOn < DateTime.UtcNow )
            {
                _logger.LogInformation( "{action} Lock expired on {releaseOn}", nameof( CreateLockAsync ), migrationLock.ReleaseOn );
                collection.FindOneAndDelete( x => x.Id == 1 );
            }
            else
            {
                throw new MigrationLockUnavailableException( $"The lock `{_options.LockName}` is unavailable." );
            }
        }

        try
        {
            await collection.InsertOneAsync( new MigrationLock( 1, DateTimeOffset.UtcNow, DateTimeOffset.UtcNow + _options.LockMaxLifetime ) );
        }
        catch ( Exception ex )
        {
            _logger.LogError( ex, "{action} unable to create database lock", nameof( CreateLockAsync ) );
            throw new MigrationLockUnavailableException( $"The lock `{_options.LockName}` is unavailable.", ex );
        }

        return new Disposable( () =>
        {
            _logger.LogInformation( "{action} disposing lock", nameof( CreateLockAsync ) );

            try
            {
                collection.FindOneAndDelete( x => x.Id == 1 );
            }
            catch ( Exception ex )
            {
                _logger.LogCritical( ex, "{action} unable to remove database lock", nameof( CreateLockAsync ) );
                throw;
            }
        } );
    }

    public async Task<bool> ExistsAsync( string recordId )
    {
        _logger.LogDebug( "Running {action} with `{recordId}`", nameof( ExistsAsync ), recordId );

        var db = _client.GetDatabase( _options.DatabaseName );
        var collection = db.GetCollection<MigrationRecord>( _options.CollectionName );
        using var cursor = await collection.FindAsync( x => x.Id == recordId );
        var record = await cursor.FirstOrDefaultAsync();

        _logger.LogDebug( "{action} found `{recordId}`", nameof( ExistsAsync ), record?.Id );

        return record != null;
    }

    public async Task DeleteAsync( string recordId )
    {
        _logger.LogDebug( "Running {action} with `{recordId}`", nameof( DeleteAsync ), recordId );

        var db = _client.GetDatabase( _options.DatabaseName );
        var collection = db.GetCollection<MigrationRecord>( _options.CollectionName );
        await collection.FindOneAndDeleteAsync( x => x.Id == recordId );
    }

    public async Task WriteAsync( string recordId )
    {
        _logger.LogDebug( "Running {action} with `{recordId}`", nameof( WriteAsync ), recordId );

        var db = _client.GetDatabase( _options.DatabaseName );
        var collection = db.GetCollection<MigrationRecord>( _options.CollectionName );
        await collection.InsertOneAsync( new MigrationRecord
        {
            Id = recordId,
            RunOn = DateTimeOffset.UtcNow
        } );
    }

    private record MigrationLock( int Id, DateTimeOffset LockedOn, DateTimeOffset ReleaseOn );

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
