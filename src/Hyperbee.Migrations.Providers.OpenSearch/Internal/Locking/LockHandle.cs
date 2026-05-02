#nullable enable
using Microsoft.Extensions.Logging;

namespace Hyperbee.Migrations.Providers.OpenSearch.Internal.Locking;

// Auto-renewing lock handle ported from AerospikeRecordStore.LockHandle, with
// OpenSearch-specific deltas:
//   - CAS via if_seq_no/if_primary_term (OpenSearch's optimistic-concurrency
//     primitives; tracked across renewals)
//   - Realtime GET on takeover decisions (NF-1 from assessment 0002 —
//     refresh-lag would otherwise produce false-positive takeovers)
//   - LockMaxLifetime cancellation contract (PM-12): on ceiling, signals
//     LockExpired CT and stops renewing. Surfacing the cancellation to the
//     in-flight migration is the consumer's responsibility (link
//     LockExpired to the migration CT at the call site)
//
// Disposal stops the renewal loop and best-effort deletes the lock document.

internal sealed class LockHandle : IDisposable
{
    private readonly OpenSearchRecordStore _store;
    private readonly string _lockId;
    private readonly DateTimeOffset _deadline;
    private readonly CancellationTokenSource _renewCts;
    private readonly CancellationTokenSource _expiredCts;
    private readonly Task _renewTask;

    private long _seqNo;
    private long _primaryTerm;
    private int _disposed;

    public LockHandle(
        OpenSearchRecordStore store,
        string lockId,
        long seqNo,
        long primaryTerm )
    {
        _store = store;
        _lockId = lockId;
        _seqNo = seqNo;
        _primaryTerm = primaryTerm;

        _deadline = _store.TimeProvider.GetUtcNow() + _store.Options.LockMaxLifetime;
        _renewCts = new CancellationTokenSource();
        _expiredCts = new CancellationTokenSource();
        _renewTask = RenewLockLoopAsync( _renewCts.Token );
    }

    /// <summary>
    /// Cancelled when the lock's max-lifetime ceiling is hit OR when renewal
    /// fails terminally (CAS conflict — another runner has taken the lock).
    /// Per R-05 / PM-12: the consumer should link this CT to the in-flight
    /// migration's cancellation token so statements abort cleanly when the
    /// lock is no longer held.
    /// </summary>
    public CancellationToken LockExpired => _expiredCts.Token;

    private async Task RenewLockLoopAsync( CancellationToken cancellationToken )
    {
        try
        {
            while ( !cancellationToken.IsCancellationRequested )
            {
                try
                {
                    await Task.Delay( _store.Options.LockRenewInterval, _store.TimeProvider, cancellationToken )
                        .ConfigureAwait( false );
                }
                catch ( OperationCanceledException )
                {
                    return;
                }

                if ( _store.TimeProvider.GetUtcNow() >= _deadline )
                {
                    _store.Logger.LogCritical(
                        "Lock {lockId} reached LockMaxLifetime ({lifetime}); renewals stopped. " +
                        "In-flight migration should observe LockExpired and abort. Per R-05 / PM-12, " +
                        "another runner may take over the lock after LockStaleAfter elapses.",
                        _lockId, _store.Options.LockMaxLifetime );

                    SignalExpired();
                    return;
                }

                try
                {
                    var (newSeq, newTerm) = await _store.RenewLockAsync(
                        _lockId, _seqNo, _primaryTerm, cancellationToken
                    ).ConfigureAwait( false );

                    _seqNo = newSeq;
                    _primaryTerm = newTerm;

                    _store.Logger.LogDebug(
                        "Lock {lockId} renewed (seq={seq}, term={term})",
                        _lockId, _seqNo, _primaryTerm );
                }
                catch ( MigrationLockUnavailableException )
                {
                    // CAS conflict during renewal — another runner has taken over the lock
                    _store.Logger.LogCritical(
                        "Lock {lockId} renewal failed: another runner has taken the lock. " +
                        "Cancelling in-flight migration via LockExpired.", _lockId );

                    SignalExpired();
                    return;
                }
                catch ( OperationCanceledException )
                {
                    return;
                }
                catch ( Exception ex )
                {
                    // Transient errors retry on next loop iteration. The TTL math
                    // (LockStaleAfter > 2 * LockRenewInterval) gives us a buffer.
                    _store.Logger.LogWarning( ex,
                        "Lock {lockId} transient renewal error; will retry", _lockId );
                }
            }
        }
        catch ( Exception ex )
        {
            // Defensive: never let an unhandled exception escape a fire-and-forget task.
            _store.Logger.LogError( ex, "Lock {lockId} renewal loop unexpected error", _lockId );
        }
    }

    private void SignalExpired()
    {
        try { _expiredCts.Cancel(); }
        catch ( ObjectDisposedException ) { /* race with Dispose */ }
    }

    public void Dispose()
    {
        if ( Interlocked.CompareExchange( ref _disposed, 1, 0 ) != 0 )
            return;

        _store.Logger.LogInformation( "Disposing lock {lockId}", _lockId );

        try
        {
            _renewCts.Cancel();
            try { _renewTask.GetAwaiter().GetResult(); }
            catch ( OperationCanceledException ) { /* expected */ }
            catch ( Exception ex )
            {
                _store.Logger.LogWarning( ex, "Lock {lockId} renew task threw during shutdown", _lockId );
            }
        }
        finally
        {
            _renewCts.Dispose();
            _expiredCts.Dispose();
        }

        // Best-effort delete with current CAS values. If 409 (someone else has the lock now),
        // log and move on — they'll own cleanup.
        try
        {
            _store.ReleaseLockAsync( _lockId, _seqNo, _primaryTerm )
                .GetAwaiter().GetResult();
        }
        catch ( Exception ex )
        {
            _store.Logger.LogWarning( ex,
                "Lock {lockId} release failed; lock may be cleaned up by takeover or TTL", _lockId );
        }
    }
}
