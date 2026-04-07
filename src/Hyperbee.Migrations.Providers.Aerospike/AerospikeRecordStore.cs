using Aerospike.Client;
using Microsoft.Extensions.Logging;

namespace Hyperbee.Migrations.Providers.Aerospike;

internal class AerospikeRecordStore : IMigrationRecordStore
{
    private readonly IAsyncClient _client;
    private readonly AerospikeMigrationOptions _options;
    private readonly ILogger<AerospikeRecordStore> _logger;

    public AerospikeRecordStore(
        IAsyncClient client,
        AerospikeMigrationOptions options,
        ILogger<AerospikeRecordStore> logger )
    {
        _client = client;
        _options = options;
        _logger = logger;
    }

    public Task InitializeAsync( CancellationToken cancellationToken = default )
    {
        _logger.LogDebug( "Running {action}", nameof( InitializeAsync ) );

        // Aerospike namespaces are configured at the server level, not created dynamically.
        // Verify we can connect by checking if the client is connected.

        if ( !_client.Connected )
        {
            throw new MigrationException( $"Aerospike client is not connected. Verify the cluster is available and the namespace '{_options.Namespace}' is configured." );
        }

        return Task.CompletedTask;
    }

    public async Task<IDisposable> CreateLockAsync()
    {
        _logger.LogDebug( "Running {action}", nameof( CreateLockAsync ) );

        var key = new Key( _options.Namespace, _options.MigrationSet, _options.LockName );

        try
        {
            var record = await _client.Get( null, CancellationToken.None, key ).ConfigureAwait( false );

            if ( record != null )
            {
                _logger.LogWarning( "{action} Lock already exists", nameof( CreateLockAsync ) );
                throw new MigrationLockUnavailableException( $"The lock `{_options.LockName}` is unavailable." );
            }

            var policy = new WritePolicy
            {
                recordExistsAction = RecordExistsAction.CREATE_ONLY,
                expiration = (int) _options.LockMaxLifetime.TotalSeconds
            };

            await _client.Put(
                policy,
                CancellationToken.None,
                key,
                new Bin( "Name", _options.LockName ),
                new Bin( "LockedOn", DateTimeOffset.UtcNow.ToUnixTimeSeconds() )
            ).ConfigureAwait( false );
        }
        catch ( MigrationLockUnavailableException )
        {
            throw;
        }
        catch ( AerospikeException ex ) when ( ex.Result == ResultCode.KEY_EXISTS_ERROR )
        {
            _logger.LogWarning( "{action} Lock already exists (key exists)", nameof( CreateLockAsync ) );
            throw new MigrationLockUnavailableException( $"The lock `{_options.LockName}` is unavailable.", ex );
        }
        catch ( Exception ex )
        {
            _logger.LogError( ex, "{action} unable to create lock", nameof( CreateLockAsync ) );
            throw new MigrationLockUnavailableException( $"The lock `{_options.LockName}` is unavailable.", ex );
        }

        return new Disposable( () =>
        {
            _logger.LogInformation( "{action} disposing lock", nameof( CreateLockAsync ) );

            try
            {
                _client.Delete( null, CancellationToken.None, key )
                    .GetAwaiter().GetResult();
            }
            catch ( Exception ex )
            {
                _logger.LogCritical( ex, "{action} unable to remove lock", nameof( CreateLockAsync ) );
                throw;
            }
        } );
    }

    public async Task<bool> ExistsAsync( string recordId )
    {
        _logger.LogDebug( "Running {action} with `{recordId}`", nameof( ExistsAsync ), recordId );

        var key = new Key( _options.Namespace, _options.MigrationSet, recordId );
        var record = await _client.Get( null, CancellationToken.None, key ).ConfigureAwait( false );

        _logger.LogDebug( "{action} found `{recordId}`: {exists}", nameof( ExistsAsync ), recordId, record != null );

        return record != null;
    }

    public async Task<MigrationRecord> ReadAsync( string recordId )
    {
        _logger.LogDebug( "Running {action} with `{recordId}`", nameof( ReadAsync ), recordId );

        var key = new Key( _options.Namespace, _options.MigrationSet, recordId );
        var record = await _client.Get( null, CancellationToken.None, key ).ConfigureAwait( false );

        if ( record == null )
            return null;

        var executedAt = record.GetLong( "ExecutedAt" );
        return new MigrationRecord
        {
            Id = recordId,
            RunOn = DateTimeOffset.FromUnixTimeSeconds( executedAt )
        };
    }

    public async Task DeleteAsync( string recordId )
    {
        _logger.LogDebug( "Running {action} with `{recordId}`", nameof( DeleteAsync ), recordId );

        var key = new Key( _options.Namespace, _options.MigrationSet, recordId );
        await _client.Delete( null, CancellationToken.None, key ).ConfigureAwait( false );
    }

    public async Task WriteAsync( string recordId )
    {
        _logger.LogDebug( "Running {action} with `{recordId}`", nameof( WriteAsync ), recordId );

        var key = new Key( _options.Namespace, _options.MigrationSet, recordId );

        await _client.Put(
            null,
            CancellationToken.None,
            key,
            new Bin( "Name", recordId ),
            new Bin( "ExecutedAt", DateTimeOffset.UtcNow.ToUnixTimeSeconds() )
        ).ConfigureAwait( false );
    }

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
