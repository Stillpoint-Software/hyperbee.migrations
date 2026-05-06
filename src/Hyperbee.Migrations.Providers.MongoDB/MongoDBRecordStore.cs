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

    public async Task<MigrationRecord> ReadAsync( string recordId )
    {
        _logger.LogDebug( "Running {action} with `{recordId}`", nameof( ReadAsync ), recordId );

        var db = _client.GetDatabase( _options.DatabaseName );
        var collection = db.GetCollection<MigrationRecord>( _options.CollectionName );
        using var cursor = await collection.FindAsync( x => x.Id == recordId );
        var record = await cursor.FirstOrDefaultAsync();
        record?.EnsureLedgerIntegrity();
        return record;
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

    public async Task<IReadOnlySet<string>> LoadAppliedVersionsAsync(
        IEnumerable<string> candidateIds,
        CancellationToken cancellationToken = default )
    {
        if ( candidateIds == null )
            throw new ArgumentNullException( nameof( candidateIds ) );

        var ids = candidateIds as string[] ?? candidateIds.ToArray();
        var found = new HashSet<string>( StringComparer.Ordinal );
        if ( ids.Length == 0 )
            return found;

        var db = _client.GetDatabase( _options.DatabaseName );
        var collection = db.GetCollection<MigrationRecord>( _options.CollectionName );

        // Realtime read per ADR-0019 Phase 3. ReadConcern.Majority + ReadPreference.Primary
        // is the safe default for replica sets; MongoDB falls back to local semantics on
        // standalone deployments automatically. The find projection narrows to _id only
        // to minimize round-trip bytes.
        var filter = Builders<MigrationRecord>.Filter.In( x => x.Id, ids );
        var projection = Builders<MigrationRecord>.Projection.Include( x => x.Id );

        var settings = collection
            .WithReadConcern( ReadConcern.Majority )
            .WithReadPreference( ReadPreference.Primary );

        using var cursor = await settings
            .Find( filter )
            .Project<MigrationRecord>( projection )
            .ToCursorAsync( cancellationToken )
            .ConfigureAwait( false );

        while ( await cursor.MoveNextAsync( cancellationToken ).ConfigureAwait( false ) )
        {
            foreach ( var doc in cursor.Current )
                found.Add( doc.Id );
        }
        return found;
    }

    public async Task<IReadOnlySet<long>> LoadSatisfyingRowsAsync(
        IEnumerable<long> versions,
        CancellationToken cancellationToken = default )
    {
        if ( versions == null )
            throw new ArgumentNullException( nameof( versions ) );

        var inputs = versions as long[] ?? versions.ToArray();
        var covered = new HashSet<long>();
        if ( inputs.Length == 0 )
            return covered;

        var db = _client.GetDatabase( _options.DatabaseName );
        var collection = db.GetCollection<MigrationRecord>( _options.CollectionName )
            .WithReadConcern( ReadConcern.Majority )
            .WithReadPreference( ReadPreference.Primary );

        // Transitive squash satisfaction (ADR-0019 A6). MongoDB has no array-array
        // intersection operator; we project replaces arrays from squash rows whose
        // replaces is `$in` the input set, then intersect client-side. The candidate
        // pool is already narrow because Kind=Squash rows are sparse.
        var filter = Builders<MigrationRecord>.Filter.And(
            Builders<MigrationRecord>.Filter.Eq( x => x.Kind, MigrationRecordKind.Squash ),
            Builders<MigrationRecord>.Filter.In( "replaces", inputs.Cast<object>() ) );
        var projection = Builders<MigrationRecord>.Projection.Include( x => x.Replaces );

        using var cursor = await collection
            .Find( filter )
            .Project<MigrationRecord>( projection )
            .ToCursorAsync( cancellationToken )
            .ConfigureAwait( false );

        var inputSet = new HashSet<long>( inputs );
        while ( await cursor.MoveNextAsync( cancellationToken ).ConfigureAwait( false ) )
        {
            foreach ( var doc in cursor.Current )
            {
                if ( doc.Replaces == null ) continue;
                foreach ( var v in doc.Replaces )
                    if ( inputSet.Contains( v ) )
                        covered.Add( v );
            }
        }
        return covered;
    }

    public async Task<WriteOutcome> WriteAsync(
        MigrationRecord record,
        WritePrecondition precondition = WritePrecondition.None,
        CancellationToken cancellationToken = default )
    {
        if ( record == null )
            throw new ArgumentNullException( nameof( record ) );

        record.EnsureLedgerIntegrity();

        _logger.LogDebug( "Running {action} (record-bearing) with `{recordId}` precondition={precondition}",
            nameof( WriteAsync ), record.Id, precondition );

        var db = _client.GetDatabase( _options.DatabaseName );
        var collection = db.GetCollection<MigrationRecord>( _options.CollectionName );

        if ( precondition == WritePrecondition.MustNotExist )
        {
            try
            {
                await collection.InsertOneAsync( record, cancellationToken: cancellationToken ).ConfigureAwait( false );
                return WriteOutcome.Created;
            }
            catch ( MongoWriteException ex ) when ( ex.WriteError?.Category == ServerErrorCategory.DuplicateKey )
            {
                var existing = await ReadAsync( record.Id ).ConfigureAwait( false );
                if ( existing != null && string.Equals( existing.Checksum, record.Checksum, StringComparison.Ordinal ) )
                    return WriteOutcome.AlreadyExistsBenign;
                return WriteOutcome.PreconditionFailed;
            }
        }

        // Upsert by id
        var filter = Builders<MigrationRecord>.Filter.Eq( x => x.Id, record.Id );
        await collection.ReplaceOneAsync( filter, record,
            new ReplaceOptions { IsUpsert = true }, cancellationToken ).ConfigureAwait( false );
        return WriteOutcome.Created;
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
