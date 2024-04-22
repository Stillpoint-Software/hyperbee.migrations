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
        await Task.CompletedTask;

        return null;
    }

    public async Task<bool> ExistsAsync( string recordId )
    {
        _logger.LogDebug( "Running {action} with `{recordId}`", nameof( ExistsAsync ), recordId );

        var db = _client.GetDatabase( _options.DatabaseName );
        var collection = db.GetCollection<MigrationRecord>( _options.CollectionName );
        var cursor = await collection.FindAsync( x => x.Id == recordId );

        return cursor.Current != null;
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
}
