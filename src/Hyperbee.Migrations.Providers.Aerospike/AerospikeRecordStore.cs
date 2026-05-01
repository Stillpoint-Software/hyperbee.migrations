using Aerospike.Client;
using Microsoft.Extensions.Logging;

namespace Hyperbee.Migrations.Providers.Aerospike;

internal class AerospikeRecordStore : IMigrationRecordStore
{
    private readonly IAsyncClient _client;
    private readonly AerospikeMigrationOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<AerospikeRecordStore> _logger;

    public AerospikeRecordStore(
        IAsyncClient client,
        AerospikeMigrationOptions options,
        TimeProvider timeProvider,
        ILogger<AerospikeRecordStore> logger )
    {
        _client = client;
        _options = options;
        _timeProvider = timeProvider;
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

        if ( _options.LockRenewInterval >= _options.LockExpireInterval )
        {
            throw new MigrationException(
                $"LockRenewInterval ({_options.LockRenewInterval}) must be shorter than LockExpireInterval ({_options.LockExpireInterval})." );
        }

        var key = new Key( _options.Namespace, _options.MigrationSet, _options.LockName );
        var expireSeconds = (int) _options.LockExpireInterval.TotalSeconds;

        try
        {
            // Atomic acquire. CREATE_ONLY rejects with KEY_EXISTS_ERROR if another runner
            // already holds the lock — server-enforced, not racy.

            var policy = new WritePolicy
            {
                recordExistsAction = RecordExistsAction.CREATE_ONLY,
                expiration = expireSeconds
            };

            await _client.Put(
                policy,
                CancellationToken.None,
                key,
                new Bin( "Name", _options.LockName ),
                new Bin( "LockedOn", _timeProvider.GetUtcNow().ToUnixTimeSeconds() )
            ).ConfigureAwait( false );
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

        // Start the auto-renew loop. Runs in the background and extends the lock TTL
        // until the disposable is disposed or LockMaxLifetime elapses (whichever comes first).

        var renewCts = new CancellationTokenSource();
        var renewTask = RenewLockLoopAsync( key, expireSeconds, renewCts.Token );

        return new LockHandle( this, key, renewCts, renewTask );
    }

    private async Task RenewLockLoopAsync( Key key, int expireSeconds, CancellationToken cancellationToken )
    {
        var deadline = _timeProvider.GetUtcNow() + _options.LockMaxLifetime;
        var policy = new WritePolicy { expiration = expireSeconds };

        try
        {
            while ( !cancellationToken.IsCancellationRequested )
            {
                try
                {
                    await Task.Delay( _options.LockRenewInterval, _timeProvider, cancellationToken ).ConfigureAwait( false );
                }
                catch ( OperationCanceledException )
                {
                    return;
                }

                if ( _timeProvider.GetUtcNow() >= deadline )
                {
                    _logger.LogCritical(
                        "{action} reached LockMaxLifetime ({lifetime}); renewals stopped. Migration is still running but the lock will expire and another runner may acquire it.",
                        nameof( CreateLockAsync ), _options.LockMaxLifetime );
                    return;
                }

                try
                {
                    await _client.Touch( policy, CancellationToken.None, key ).ConfigureAwait( false );
                    _logger.LogDebug( "{action} renewed lock", nameof( CreateLockAsync ) );
                }
                catch ( AerospikeException ex ) when ( ex.Result == ResultCode.KEY_NOT_FOUND_ERROR )
                {
                    _logger.LogCritical(
                        ex,
                        "{action} lock record was not found during renewal — TTL probably expired. Stopping renewal. Another runner may acquire the lock.",
                        nameof( CreateLockAsync ) );
                    return;
                }
                catch ( Exception ex )
                {
                    // Transient errors get retried on the next loop iteration. The lock TTL
                    // gives us a buffer (LockExpireInterval - LockRenewInterval) to recover.
                    _logger.LogWarning( ex, "{action} transient error renewing lock; will retry", nameof( CreateLockAsync ) );
                }
            }
        }
        catch ( Exception ex )
        {
            // Defensive: never let an unhandled exception escape a fire-and-forget task.
            _logger.LogError( ex, "{action} unexpected error in renewal loop", nameof( CreateLockAsync ) );
        }
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
            new Bin( "ExecutedAt", _timeProvider.GetUtcNow().ToUnixTimeSeconds() )
        ).ConfigureAwait( false );
    }

    private sealed class LockHandle : IDisposable
    {
        private readonly AerospikeRecordStore _store;
        private readonly Key _key;
        private readonly CancellationTokenSource _renewCts;
        private readonly Task _renewTask;
        private int _disposed;

        public LockHandle( AerospikeRecordStore store, Key key, CancellationTokenSource renewCts, Task renewTask )
        {
            _store = store;
            _key = key;
            _renewCts = renewCts;
            _renewTask = renewTask;
        }

        public void Dispose()
        {
            if ( Interlocked.CompareExchange( ref _disposed, 1, 0 ) != 0 )
                return;

            _store._logger.LogInformation( "{action} disposing lock", nameof( CreateLockAsync ) );

            try
            {
                _renewCts.Cancel();
                try { _renewTask.GetAwaiter().GetResult(); }
                catch ( OperationCanceledException ) { /* expected on cancel */ }
            }
            finally
            {
                _renewCts.Dispose();
            }

            try
            {
                _store._client.Delete( null, CancellationToken.None, _key )
                    .GetAwaiter().GetResult();
            }
            catch ( Exception ex )
            {
                _store._logger.LogCritical( ex, "{action} unable to remove lock", nameof( CreateLockAsync ) );
                throw;
            }
        }
    }
}
