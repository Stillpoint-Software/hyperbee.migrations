using Aerospike.Client;
using Hyperbee.Migrations.Wait;
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

    public async Task InitializeAsync( CancellationToken cancellationToken = default )
    {
        _logger.LogDebug( "Running {action}", nameof( InitializeAsync ) );

        // Aerospike namespaces are configured at the server level, not created dynamically.
        // Verify we can connect by checking if the client is connected.

        if ( !_client.Connected )
        {
            throw new MigrationException( $"Aerospike client is not connected. Verify the cluster is available and the namespace '{_options.Namespace}' is configured." );
        }

        // Readiness gate. `_client.Connected` flips true as soon as the seed
        // node answers -- before partitions are master-assigned. Without a
        // gate, the first ledger op on the lock-disabled path (ExistsAsync /
        // ReadAsync / WriteAsync) can hit a transient cluster error and
        // false-fail the run. Probe with a side-effect-free Get of a
        // non-existent sentinel key, retrying only on transient cluster
        // errors (same predicate + bound as CreateLockAsync). A non-existent
        // key returns null on a converged cluster; a still-converging cluster
        // throws INVALID_NODE / SERVER_NOT_AVAILABLE / CLUSTER_KEY_MISMATCH /
        // FORBIDDEN_OP. Genuine misconfig (e.g. INVALID_NAMESPACE) is NOT
        // transient and surfaces immediately rather than hanging.
        var probeKey = new Key( _options.Namespace, _options.MigrationSet, "__migrations_readiness_probe__" );
        AerospikeException lastTransient = null;
        try
        {
            await WaitHelper.WaitUntilAsync(
                async _ =>
                {
                    try
                    {
                        await _client.Get( null, CancellationToken.None, probeKey ).ConfigureAwait( false );
                        return true;
                    }
                    catch ( AerospikeException ex ) when ( IsTransientClusterError( ex ) )
                    {
                        lastTransient = ex;
                        return false;
                    }
                },
                timeout: TimeSpan.FromSeconds( 60 ) ).ConfigureAwait( false );
        }
        catch ( RetryTimeoutException )
        {
            _logger.LogError( lastTransient, "{action} cluster not ready after retries (last error: {result})", nameof( InitializeAsync ), lastTransient?.Result );
            throw new MigrationException(
                $"Aerospike cluster did not become ready within 60s (namespace '{_options.Namespace}'). Last transient error: {lastTransient?.Result}.",
                lastTransient );
        }
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

        // Atomic acquire. CREATE_ONLY rejects with KEY_EXISTS_ERROR if another runner
        // already holds the lock — server-enforced, not racy. FORBIDDEN_OP (22) is
        // a transient signal during cluster startup or partition rebalance ("operation
        // not allowed at this time"); retry that briefly before giving up so a runner
        // started a few seconds before the cluster is fully ready doesn't false-skip
        // its migrations.
        var policy = new WritePolicy
        {
            recordExistsAction = RecordExistsAction.CREATE_ONLY,
            expiration = expireSeconds
        };

        AerospikeException lastTransient = null;
        var acquired = false;
        try
        {
            await WaitHelper.WaitUntilAsync(
                async _ =>
                {
                    try
                    {
                        await _client.Put(
                            policy,
                            CancellationToken.None,
                            key,
                            new Bin( "Name", _options.LockName ),
                            new Bin( "LockedOn", _timeProvider.GetUtcNow().ToUnixTimeSeconds() )
                        ).ConfigureAwait( false );
                        acquired = true;
                        return true;
                    }
                    catch ( AerospikeException ex ) when ( IsTransientClusterError( ex ) )
                    {
                        lastTransient = ex;
                        return false;
                    }
                },
                timeout: TimeSpan.FromSeconds( 60 ) ).ConfigureAwait( false );
        }
        catch ( RetryTimeoutException )
        {
            _logger.LogError( lastTransient, "{action} unable to create lock after retries (last error: {result})", nameof( CreateLockAsync ), lastTransient?.Result );
            throw new MigrationLockUnavailableException( $"The lock `{_options.LockName}` is unavailable.", lastTransient );
        }
        catch ( RetryStrategyException ex ) when ( ex.InnerException is AerospikeException ae && ae.Result == ResultCode.KEY_EXISTS_ERROR )
        {
            _logger.LogWarning( "{action} Lock already exists (key exists)", nameof( CreateLockAsync ) );
            throw new MigrationLockUnavailableException( $"The lock `{_options.LockName}` is unavailable.", ae );
        }
        catch ( RetryStrategyException ex )
        {
            _logger.LogError( ex.InnerException ?? ex, "{action} unable to create lock", nameof( CreateLockAsync ) );
            throw new MigrationLockUnavailableException( $"The lock `{_options.LockName}` is unavailable.", ex.InnerException ?? ex );
        }

        if ( !acquired )
            throw new MigrationLockUnavailableException( $"The lock `{_options.LockName}` is unavailable." );

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

    // Cluster-startup churn surfaces in three transient ways:
    //
    //   22 (FORBIDDEN_OP / "Operation not allowed at this time") — the
    //      partition isn't yet master-assigned for the lock set.
    //   ResultCode.INVALID_NODE_ERROR — request routed to a node that
    //      doesn't host the partition yet (partition map convergence).
    //   ResultCode.SERVER_NOT_AVAILABLE — node is up but namespace is
    //      still loading or in a transitional state.
    //   ResultCode.CLUSTER_KEY_MISMATCH — client and server disagree on
    //      cluster generation; resolves on the next info round.
    //
    // FORBIDDEN_OP isn't a named constant in Aerospike.Client 8.x; the raw
    // value 22 is the canonical Aerospike error code documented for this
    // condition.
    private const int ForbiddenOp = 22;

    private static bool IsTransientClusterError( AerospikeException ex )
    {
        return ex.Result == ForbiddenOp
            || ex.Result == ResultCode.INVALID_NODE_ERROR
            || ex.Result == ResultCode.SERVER_NOT_AVAILABLE
            || ex.Result == ResultCode.CLUSTER_KEY_MISMATCH;
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

        // Pre-checksum-era records have no Checksum/Kind/Replaces bins; the Aerospike
        // client returns null/0/null for missing bins, which becomes Kind=Migration and
        // Checksum=null after coercion. Per ADR-0021 amendment A1.
        var checksum = record.GetString( "Checksum" );
        var kind = (MigrationRecordKind) (byte) record.GetLong( "Kind" );

        var replacesList = record.GetList( "Replaces" );
        long[] replaces;
        if ( replacesList == null || replacesList.Count == 0 )
        {
            replaces = Array.Empty<long>();
        }
        else
        {
            replaces = new long[replacesList.Count];
            for ( var i = 0; i < replacesList.Count; i++ )
                replaces[i] = Convert.ToInt64( replacesList[i] );
        }

        var migrationRecord = new MigrationRecord
        {
            Id = recordId,
            RunOn = DateTimeOffset.FromUnixTimeSeconds( executedAt ),
            Checksum = checksum,
            Kind = kind,
            Replaces = replaces
        };

        migrationRecord.EnsureLedgerIntegrity();
        return migrationRecord;
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

    public async Task<IReadOnlySet<string>> IntersectWithAppliedAsync(
        IEnumerable<string> candidateIds,
        CancellationToken cancellationToken = default )
    {
        if ( candidateIds == null )
            throw new ArgumentNullException( nameof( candidateIds ) );

        var ids = candidateIds as string[] ?? candidateIds.ToArray();
        var found = new HashSet<string>( StringComparer.Ordinal );
        if ( ids.Length == 0 )
            return found;

        // BatchGet realtime read per ADR-0019 Phase 3. Aerospike's BatchPolicy
        // defaults to strong consistency on namespaces configured for SC; the
        // result.Length == ids.Length, with null entries for missing records.
        var keys = new Key[ids.Length];
        for ( var i = 0; i < ids.Length; i++ )
            keys[i] = new Key( _options.Namespace, _options.MigrationSet, ids[i] );

        var batchPolicy = new BatchPolicy();
        var records = await _client.Get( batchPolicy, cancellationToken, keys, "Name" )
            .ConfigureAwait( false );

        for ( var i = 0; i < records.Length; i++ )
        {
            if ( records[i] != null )
                found.Add( ids[i] );
        }
        return found;
    }

    public async Task<IReadOnlySet<long>> IntersectWithSquashedAsync(
        IEnumerable<long> versions,
        CancellationToken cancellationToken = default )
    {
        if ( versions == null )
            throw new ArgumentNullException( nameof( versions ) );

        var inputs = versions as long[] ?? versions.ToArray();
        var covered = new HashSet<long>();
        if ( inputs.Length == 0 )
            return covered;

        // Transitive squash satisfaction (ADR-0019 A6) on Aerospike. The async
        // client's Query API is listener-shaped (no native CT); we bridge via
        // a TaskCompletionSource and a custom RecordSequenceListener. Filter
        // expression narrows to Kind=Squash; we then intersect each record's
        // Replaces bin with the input set client-side.
        var inputSet = new HashSet<long>( inputs );

        var statement = new Statement
        {
            Namespace = _options.Namespace,
            SetName = _options.MigrationSet,
            BinNames = new[] { "Kind", "Replaces" }
        };

        var queryPolicy = new QueryPolicy
        {
            filterExp = Exp.Build(
                Exp.EQ( Exp.IntBin( "Kind" ),
                        Exp.Val( (long) (byte) MigrationRecordKind.Squash ) ) )
        };

        var tcs = new TaskCompletionSource<bool>( TaskCreationOptions.RunContinuationsAsynchronously );
        var coveredLock = new object();
        var listener = new TransitiveScanListener( inputSet, covered, coveredLock, tcs );

        await using ( cancellationToken.Register( () => tcs.TrySetCanceled( cancellationToken ) ) )
        {
            try
            {
                _client.Query( queryPolicy, listener, statement );
            }
            catch ( AerospikeException ex )
            {
                tcs.TrySetException( ex );
            }

            await tcs.Task.ConfigureAwait( false );
        }

        return covered;
    }

    // Bridges the Aerospike async client's listener-shaped Query API to a
    // Task. OnRecord/OnSuccess/OnFailure are invoked from the client's
    // completion-port pool; we publish to a thread-safe HashSet via lock.
    private sealed class TransitiveScanListener : RecordSequenceListener
    {
        private readonly HashSet<long> _inputSet;
        private readonly HashSet<long> _covered;
        private readonly object _coveredLock;
        private readonly TaskCompletionSource<bool> _tcs;

        public TransitiveScanListener(
            HashSet<long> inputSet,
            HashSet<long> covered,
            object coveredLock,
            TaskCompletionSource<bool> tcs )
        {
            _inputSet = inputSet;
            _covered = covered;
            _coveredLock = coveredLock;
            _tcs = tcs;
        }

        public void OnRecord( Key key, Record record )
        {
            if ( record == null )
                return;
            var replacesObj = record.GetValue( "Replaces" );
            if ( replacesObj is not System.Collections.IList list )
                return;

            foreach ( var item in list )
            {
                if ( item is null ) continue;
                var v = Convert.ToInt64( item );
                if ( !_inputSet.Contains( v ) ) continue;

                lock ( _coveredLock )
                {
                    _covered.Add( v );
                }
            }
        }

        public void OnSuccess() => _tcs.TrySetResult( true );

        public void OnFailure( AerospikeException ex ) => _tcs.TrySetException( ex );
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

        var key = new Key( _options.Namespace, _options.MigrationSet, record.Id );

        // Aerospike treats a null bin value as a delete on update — fine for pre-existing
        // rows; for create-only, just omit the bin so the bin is absent on disk.
        var binList = new List<Bin>( 5 )
        {
            new( "Name", record.Id ),
            new( "ExecutedAt", record.RunOn.ToUnixTimeSeconds() ),
            new( "Kind", (long) (byte) record.Kind ),
            new( "Replaces", record.Replaces?.ToList() ?? new List<long>() )
        };
        if ( record.Checksum != null )
            binList.Add( new Bin( "Checksum", record.Checksum ) );

        var bins = binList.ToArray();

        var policy = new WritePolicy
        {
            recordExistsAction = precondition == WritePrecondition.MustNotExist
                ? RecordExistsAction.CREATE_ONLY
                : RecordExistsAction.UPDATE
        };

        try
        {
            await _client.Put( policy, cancellationToken, key, bins ).ConfigureAwait( false );
            return WriteOutcome.Created;
        }
        catch ( AerospikeException ex ) when (
            ex.Result == ResultCode.KEY_EXISTS_ERROR &&
            precondition == WritePrecondition.MustNotExist )
        {
            // Existing row — checksum-equality re-check decides benign vs conflict.
            var existing = await ReadAsync( record.Id ).ConfigureAwait( false );
            if ( existing != null && string.Equals( existing.Checksum, record.Checksum, StringComparison.Ordinal ) )
                return WriteOutcome.AlreadyExistsBenign;
            return WriteOutcome.PreconditionFailed;
        }
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
