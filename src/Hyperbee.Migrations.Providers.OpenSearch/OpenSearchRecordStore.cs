#nullable enable
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Bootstrap;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Locking;
using Microsoft.Extensions.Logging;
using OpenSearch.Client;
using OpenSearch.Net;

// OpenSearch.Client and OpenSearch.Net both define types with the same simple
// names (OpType, Refresh, OpenSearchClientException). Alias the Net versions
// so call sites can drop the prior global:: disambiguation.
using OpType = OpenSearch.Net.OpType;
using Refresh = OpenSearch.Net.Refresh;
using OpenSearchClientException = OpenSearch.Net.OpenSearchClientException;

namespace Hyperbee.Migrations.Providers.OpenSearch;

// IMigrationRecordStore implementation per ADR-0003 (5-method contract).
//
// Lifecycle:
//   - InitializeAsync runs the bootstrapper pipeline (REST ping, cluster
//     health, ledger init, lock init). Per ADR-0014 the bootstrapper
//     surfaces a typed BootstrapResult; failure is converted to
//     OpenSearchNotReadyException
//   - CreateLockAsync acquires the singleton lock document via op_type=create;
//     on 409, the realtime-GET path checks staleness and CAS-overwrites if
//     the holder is past LockStaleAfter (NF-1 mitigation)
//   - Read/Write/Delete operate on the ledger index with strict mapping;
//     writes use ?refresh=wait_for so ExistsAsync after Write is reliable

internal sealed class OpenSearchRecordStore : IMigrationRecordStore
{
    private readonly IOpenSearchClient _client;
    private readonly OpenSearchBootstrapper _bootstrapper;
    private readonly OpenSearchMigrationOptions _options;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<OpenSearchRecordStore> _logger;

    public OpenSearchRecordStore(
        IOpenSearchClient client,
        OpenSearchBootstrapper bootstrapper,
        OpenSearchMigrationOptions options,
        TimeProvider timeProvider,
        ILogger<OpenSearchRecordStore> logger )
    {
        _client = client;
        _bootstrapper = bootstrapper;
        _options = options;
        _timeProvider = timeProvider;
        _logger = logger;

        ValidateLockTuning( options );
    }

    // Internal accessors used by LockHandle's renewal loop
    internal IOpenSearchClient Client => _client;
    internal OpenSearchMigrationOptions Options => _options;
    internal TimeProvider TimeProvider => _timeProvider;
    internal ILogger Logger => _logger;

    private static void ValidateLockTuning( OpenSearchMigrationOptions options )
    {
        // Per R-05: enforce LockRenewInterval < LockStaleAfter < LockMaxLifetime
        // AND LockStaleAfter >= 2 * LockRenewInterval. Violations are operator
        // errors that produce hard-to-diagnose lock thrashing under load
        // (MD-5 from assessment 0002).

        if ( options.LockRenewInterval <= TimeSpan.Zero )
            throw new OpenSearchProviderException( "LockRenewInterval must be positive." );

        if ( options.LockStaleAfter <= options.LockRenewInterval )
            throw new OpenSearchProviderException(
                $"LockStaleAfter ({options.LockStaleAfter}) must be greater than " +
                $"LockRenewInterval ({options.LockRenewInterval})." );

        if ( options.LockMaxLifetime <= options.LockStaleAfter )
            throw new OpenSearchProviderException(
                $"LockMaxLifetime ({options.LockMaxLifetime}) must be greater than " +
                $"LockStaleAfter ({options.LockStaleAfter})." );

        if ( options.LockStaleAfter < options.LockRenewInterval + options.LockRenewInterval )
            throw new OpenSearchProviderException(
                $"LockStaleAfter ({options.LockStaleAfter}) must be at least " +
                $"2 * LockRenewInterval ({options.LockRenewInterval}). The buffer " +
                $"prevents takeover races during legitimate renewal latency." );
    }

    public async Task InitializeAsync( CancellationToken cancellationToken = default )
    {
        _logger.LogDebug( "Running {action}", nameof( InitializeAsync ) );

        var result = await _bootstrapper.RunAsync( cancellationToken ).ConfigureAwait( false );

        if ( !result.IsSuccess )
        {
            var failed = result.FailedAt!;
            throw new OpenSearchNotReadyException(
                $"Bootstrap failed at step `{failed.Name}`: {failed.Detail ?? "no detail"}. " +
                $"See inner exception for the underlying cause.",
                failed.Exception ?? new InvalidOperationException( failed.Detail ?? "Unknown failure" ) );
        }
    }

    public async Task<IDisposable> CreateLockAsync()
    {
        _logger.LogDebug( "Running {action}", nameof( CreateLockAsync ) );

        var ownerId = $"{Environment.MachineName}/{Environment.ProcessId}";
        var now = _timeProvider.GetUtcNow();
        var doc = new LockDocument
        {
            Name = _options.LockName,
            Owner = ownerId,
            AcquiredAt = now,
            LastHeartbeat = now
        };

        // Acquire via op_type=create. 409 surfaces either as a non-valid response
        // (default settings) OR as OpenSearchClientException (when client has
        // ThrowExceptions enabled). Handle both paths uniformly.
        try
        {
            var indexResponse = await _client.IndexAsync( doc, idx => idx
                .Index( _options.LockIndex )
                .Id( _options.LockName )
                .OpType( OpType.Create )
                .Refresh( Refresh.WaitFor )
            ).ConfigureAwait( false );

            if ( indexResponse.IsValid )
            {
                _logger.LogInformation(
                    "Lock {lockId} acquired by {owner} (seq={seq}, term={term})",
                    _options.LockName, ownerId, indexResponse.SequenceNumber, indexResponse.PrimaryTerm );

                return new LockHandle( this, _options.LockName, indexResponse.SequenceNumber, indexResponse.PrimaryTerm );
            }

            // Non-throwing 409: lock document exists.
            if ( indexResponse.ApiCall.HttpStatusCode == 409 )
                return await TryTakeOverAsync( doc, cancellationToken: default ).ConfigureAwait( false );

            var detail = indexResponse.OriginalException?.Message
                ?? indexResponse.ServerError?.Error?.ToString()
                ?? "Unknown lock acquire failure.";

            throw new MigrationLockUnavailableException(
                $"Lock {_options.LockName} could not be acquired. {detail}",
                indexResponse.OriginalException ?? new InvalidOperationException( detail ) );
        }
        catch ( OpenSearchClientException ex ) when ( ex.Response?.HttpStatusCode == 409 )
        {
            // Throwing 409: same takeover path as the non-throwing case.
            return await TryTakeOverAsync( doc, cancellationToken: default ).ConfigureAwait( false );
        }
    }

    private async Task<IDisposable> TryTakeOverAsync( LockDocument newDoc, CancellationToken cancellationToken )
    {
        // Per NF-1 from assessment 0002: realtime: true is the default for GET _doc, but
        // we make it explicit because OpenSearch.Client's GetAsync defaults to it.
        // The point is that we read the document's actual write recency, not the
        // search-layer-visible version, which can lag by refresh_interval.
        var existing = await _client.GetAsync<LockDocument>( _options.LockName, g => g
            .Index( _options.LockIndex )
            .Realtime( true )
        , cancellationToken ).ConfigureAwait( false );

        if ( !existing.Found || existing.Source is null )
        {
            // Race: lock was released between our op_type=create and our GET. Retry once.
            _logger.LogDebug( "Lock {lockId} disappeared during takeover check; retrying acquire", _options.LockName );
            // Caller will surface as MigrationLockUnavailableException for now to keep
            // the API simple; operator can retry. Phase 6 may add bounded retry inside.
            throw new MigrationLockUnavailableException(
                $"Lock {_options.LockName} state was unstable (raced with another release). Retry." );
        }

        var heldFor = _timeProvider.GetUtcNow() - existing.Source.LastHeartbeat;

        if ( heldFor < _options.LockStaleAfter )
        {
            throw new MigrationLockUnavailableException(
                $"Lock {_options.LockName} is held by {existing.Source.Owner} " +
                $"(last heartbeat {heldFor.TotalSeconds:F1}s ago, stale-after {_options.LockStaleAfter.TotalSeconds}s)." );
        }

        _logger.LogWarning(
            "Lock {lockId} stale: holder {owner} last heartbeat {heldFor}s ago. Attempting CAS takeover.",
            _options.LockName, existing.Source.Owner, heldFor.TotalSeconds );

        // CAS overwrite via if_seq_no / if_primary_term
        try
        {
            var takeoverResponse = await _client.IndexAsync( newDoc, idx => idx
                .Index( _options.LockIndex )
                .Id( _options.LockName )
                .IfSequenceNumber( existing.SequenceNumber )
                .IfPrimaryTerm( existing.PrimaryTerm )
                .Refresh( Refresh.WaitFor )
            , cancellationToken ).ConfigureAwait( false );

            if ( takeoverResponse.IsValid )
            {
                _logger.LogInformation(
                    "Lock {lockId} taken over from {priorOwner} -> {newOwner} (seq={seq}, term={term})",
                    _options.LockName, existing.Source.Owner, newDoc.Owner,
                    takeoverResponse.SequenceNumber, takeoverResponse.PrimaryTerm );

                return new LockHandle( this, _options.LockName, takeoverResponse.SequenceNumber, takeoverResponse.PrimaryTerm );
            }

            // Non-throwing 409 / other failure
            throw new MigrationLockUnavailableException(
                $"Lock {_options.LockName} takeover failed: another runner CAS-overwrote first.",
                takeoverResponse.OriginalException ?? new InvalidOperationException( "CAS conflict during takeover" ) );
        }
        catch ( OpenSearchClientException ex ) when ( ex.Response?.HttpStatusCode == 409 )
        {
            throw new MigrationLockUnavailableException(
                $"Lock {_options.LockName} takeover failed: another runner CAS-overwrote first.", ex );
        }
    }

    /// <summary>
    /// Renews the lock heartbeat with the captured seq/term. Returns the
    /// new seq/term on success. On CAS conflict (409), throws
    /// MigrationLockUnavailableException to signal that the holder lost
    /// the lock and the caller (LockHandle's renewal loop) should signal
    /// LockExpired.
    /// </summary>
    internal async Task<(long SeqNo, long PrimaryTerm)> RenewLockAsync(
        string lockId, long seqNo, long primaryTerm, CancellationToken cancellationToken )
    {
        var existing = await _client.GetAsync<LockDocument>( lockId, g => g
            .Index( _options.LockIndex )
            .Realtime( true )
        , cancellationToken ).ConfigureAwait( false );

        if ( !existing.Found || existing.Source is null )
        {
            throw new MigrationLockUnavailableException(
                $"Lock {lockId} document was deleted during renewal. Lock is lost." );
        }

        // Verify we still own it via seq/term match
        if ( existing.SequenceNumber != seqNo || existing.PrimaryTerm != primaryTerm )
        {
            throw new MigrationLockUnavailableException(
                $"Lock {lockId} CAS mismatch on renewal " +
                $"(expected seq={seqNo} term={primaryTerm}, found seq={existing.SequenceNumber} term={existing.PrimaryTerm}). " +
                $"Lock has been taken over." );
        }

        // Update heartbeat; preserve acquiredAt and owner
        var updated = new LockDocument
        {
            Name = existing.Source.Name,
            Owner = existing.Source.Owner,
            AcquiredAt = existing.Source.AcquiredAt,
            LastHeartbeat = _timeProvider.GetUtcNow()
        };

        try
        {
            var renewResponse = await _client.IndexAsync( updated, idx => idx
                .Index( _options.LockIndex )
                .Id( lockId )
                .IfSequenceNumber( seqNo )
                .IfPrimaryTerm( primaryTerm )
            , cancellationToken ).ConfigureAwait( false );

            if ( !renewResponse.IsValid )
            {
                if ( renewResponse.ApiCall.HttpStatusCode == 409 )
                {
                    throw new MigrationLockUnavailableException(
                        $"Lock {lockId} renewal CAS conflict. Another runner has taken the lock." );
                }

                throw new OpenSearchProviderException(
                    $"Lock {lockId} renewal failed: " +
                    (renewResponse.OriginalException?.Message ?? "unknown error"),
                    renewResponse.OriginalException ?? new InvalidOperationException( "renewal failed" ) );
            }

            return (renewResponse.SequenceNumber, renewResponse.PrimaryTerm);
        }
        catch ( OpenSearchClientException ex ) when ( ex.Response?.HttpStatusCode == 409 )
        {
            throw new MigrationLockUnavailableException(
                $"Lock {lockId} renewal CAS conflict. Another runner has taken the lock.", ex );
        }
    }

    /// <summary>
    /// Best-effort lock release on disposal. CAS-guarded so we don't delete
    /// a lock document another runner has since taken over.
    /// </summary>
    internal async Task ReleaseLockAsync( string lockId, long seqNo, long primaryTerm )
    {
        try
        {
            var deleteResponse = await _client.DeleteAsync<LockDocument>( lockId, d => d
                .Index( _options.LockIndex )
                .IfSequenceNumber( seqNo )
                .IfPrimaryTerm( primaryTerm )
            ).ConfigureAwait( false );

            if ( deleteResponse.IsValid )
            {
                _logger.LogInformation( "Lock {lockId} released", lockId );
                return;
            }

            if ( deleteResponse.ApiCall.HttpStatusCode == 409 )
            {
                _logger.LogWarning(
                    "Lock {lockId} release skipped: CAS mismatch (another runner now holds the lock).", lockId );
                return;
            }

            if ( deleteResponse.ApiCall.HttpStatusCode == 404 )
            {
                _logger.LogDebug( "Lock {lockId} already gone at release time", lockId );
                return;
            }

            _logger.LogWarning(
                "Lock {lockId} release failed (status {status}); will rely on takeover/TTL.",
                lockId, deleteResponse.ApiCall.HttpStatusCode );
        }
        catch ( OpenSearchClientException ex ) when ( ex.Response?.HttpStatusCode == 409 )
        {
            _logger.LogWarning( ex,
                "Lock {lockId} release skipped: CAS mismatch (another runner now holds the lock).", lockId );
        }
        catch ( OpenSearchClientException ex ) when ( ex.Response?.HttpStatusCode == 404 )
        {
            _logger.LogDebug( "Lock {lockId} already gone at release time", lockId );
        }
    }

    // Per R-19: when a record is in `partially_rolled_back` state, subsequent
    // runs in either direction are refused unless ForceResume is set. The
    // refusal happens here in ExistsAsync — the core MigrationRunner calls
    // ExistsAsync before deciding to run a migration, so this is the natural
    // gate. The thrown OpenSearchPartialRollbackException bubbles through
    // RunAsync (which only catches MigrationLockUnavailable + OperationCanceled)
    // to the operator.
    //
    // ForceResume = true: skip the lockout check; behave as a normal Exists.
    // The operator has accepted responsibility for cluster-state reconciliation.
    public async Task<bool> ExistsAsync( string recordId )
    {
        _logger.LogDebug( "Running {action} with `{recordId}`", nameof( ExistsAsync ), recordId );

        var response = await _client.GetAsync<OpenSearchMigrationRecord>( recordId, g => g
            .Index( _options.LedgerIndex )
            .Realtime( true )
        ).ConfigureAwait( false );

        if ( !response.Found )
            return false;

        var record = response.Source;
        if ( !_options.ForceResume &&
             string.Equals( record?.Status, OpenSearchMigrationRecord.StatusPartiallyRolledBack, StringComparison.Ordinal ) )
        {
            throw new OpenSearchPartialRollbackException(
                recordId, record!.FailedStatementIndex,
                $"Migration `{recordId}` is in `partially_rolled_back` state " +
                $"(failed at rollback statement index {record.FailedStatementIndex?.ToString() ?? "<unknown>"}). " +
                $"Subsequent runs are refused per R-19. " +
                $"Inspect the cluster, reconcile state manually, then set " +
                $"`OpenSearchMigrationOptions.ForceResume = true` (or pass --force-resume on the runner) to proceed. " +
                $"Original error: {record.Error ?? "<none>"}" );
        }

        return true;
    }

    public async Task<MigrationRecord> ReadAsync( string recordId )
    {
        _logger.LogDebug( "Running {action} with `{recordId}`", nameof( ReadAsync ), recordId );

        var response = await _client.GetAsync<OpenSearchMigrationRecord>( recordId, g => g
            .Index( _options.LedgerIndex )
            .Realtime( true )
        ).ConfigureAwait( false );

        if ( !response.Found )
            return null!;

        var record = response.Source;
        record?.EnsureLedgerIntegrity();
        return record!;
    }

    public Task WriteAsync( string recordId )
    {
        // Standard contract write: a successful Up. Populate the forensic
        // fields per R-06 so the ledger captures status/direction/applied-by
        // alongside the record id. Failed-state writes go through the
        // partial-rollback path (WritePartialRollbackAsync).
        return WriteRecordAsync( BuildSucceededRecord( recordId, "Up" ) );
    }

    private OpenSearchMigrationRecord BuildSucceededRecord( string recordId, string direction )
    {
        return new OpenSearchMigrationRecord
        {
            Id = recordId,
            RunOn = _timeProvider.GetUtcNow(),
            Direction = direction,
            Status = OpenSearchMigrationRecord.StatusSucceeded,
            AppliedBy = $"{Environment.MachineName}/{Environment.ProcessId}",
            Error = null,
            FailedStatementIndex = null
        };
    }

    /// <summary>
    /// R-19: writes the migration's ledger entry as `partially_rolled_back`
    /// with the index of the failing rollback statement. Called by the
    /// resource runner when a Down sequence halts partway through. The
    /// recordId may already exist (the migration's previous Up wrote it);
    /// this overwrites that record with the partial-rollback state.
    /// </summary>
    internal async Task WritePartialRollbackAsync( string recordId, int failedStatementIndex, string error )
    {
        var record = new OpenSearchMigrationRecord
        {
            Id = recordId,
            RunOn = _timeProvider.GetUtcNow(),
            Direction = "Down",
            Status = OpenSearchMigrationRecord.StatusPartiallyRolledBack,
            AppliedBy = $"{Environment.MachineName}/{Environment.ProcessId}",
            Error = error,
            FailedStatementIndex = failedStatementIndex
        };
        await WriteRecordAsync( record ).ConfigureAwait( false );
    }

    private async Task WriteRecordAsync( OpenSearchMigrationRecord record )
    {
        _logger.LogDebug( "Running {action} with `{recordId}` status={status}",
            nameof( WriteRecordAsync ), record.Id, record.Status );

        var response = await _client.IndexAsync( record, idx => idx
            .Index( _options.LedgerIndex )
            .Id( record.Id )
            .Refresh( Refresh.WaitFor )
        ).ConfigureAwait( false );

        if ( !response.IsValid )
        {
            var detail = response.OriginalException?.Message
                ?? response.ServerError?.Error?.ToString()
                ?? "Unknown ledger write failure.";
            throw new OpenSearchProviderException(
                $"Ledger write for `{record.Id}` failed: {detail}",
                response.OriginalException ?? new InvalidOperationException( detail ) );
        }
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

        // _mget with realtime=true per ADR-0019 Phase 3. NOT _search — _search is
        // refresh-bound (eventually-consistent across the index refresh interval),
        // whereas _mget reads through the realtime translog. Reads ledger index
        // only per ADR-0018 split.
        var response = await _client.MultiGetAsync( m => m
            .Index( _options.LedgerIndex )
            .Realtime( true )
            .GetMany<OpenSearchMigrationRecord>( ids ),
            cancellationToken
        ).ConfigureAwait( false );

        if ( !response.IsValid )
        {
            var detail = response.OriginalException?.Message
                ?? response.ServerError?.Error?.ToString()
                ?? "Unknown _mget failure.";
            throw new OpenSearchProviderException(
                $"Ledger _mget failed: {detail}",
                response.OriginalException ?? new InvalidOperationException( detail ) );
        }

        foreach ( var hit in response.Hits )
        {
            if ( hit.Found )
                found.Add( hit.Id );
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

        // Transitive squash satisfaction (ADR-0019 A6). _search with a terms filter
        // on the replaces[] field narrows to candidate squash rows; the kind=1 term
        // ensures we only consider Kind=Squash. Refresh is fine for this query —
        // we only auto-mark a squash AFTER its replaced rows are durably written
        // (Phase 1 WriteAsync uses Refresh.WaitFor), so the data we need is
        // searchable by the time we get here.
        var response = await _client.SearchAsync<OpenSearchMigrationRecord>( s => s
            .Index( _options.LedgerIndex )
            .Size( 10_000 ) // squash rows are sparse; this caps fan-out per the
                            // pragmatic bound from the design doc (C9 design note)
            .Source( src => src.Includes( i => i.Field( f => f.Replaces ) ) )
            .Query( q => q.Bool( b => b.Filter(
                f => f.Term( t => t.Field( "kind" ).Value( (byte) MigrationRecordKind.Squash ) ),
                f => f.Terms( t => t.Field( f2 => f2.Replaces ).Terms( inputs.Cast<object>() ) )
            ) ) ),
            cancellationToken
        ).ConfigureAwait( false );

        if ( !response.IsValid )
        {
            var detail = response.OriginalException?.Message
                ?? response.ServerError?.Error?.ToString()
                ?? "Unknown _search failure.";
            throw new OpenSearchProviderException(
                $"Ledger transitive-satisfaction _search failed: {detail}",
                response.OriginalException ?? new InvalidOperationException( detail ) );
        }

        var inputSet = new HashSet<long>( inputs );
        foreach ( var doc in response.Documents )
        {
            if ( doc.Replaces == null ) continue;
            foreach ( var v in doc.Replaces )
            {
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

        // Lift the universal MigrationRecord to OpenSearchMigrationRecord so the
        // forensic R-06 fields are present even when callers go through the
        // base-class API (covers reconciliation auto-marks of squash rows).
        var lifted = record as OpenSearchMigrationRecord ?? new OpenSearchMigrationRecord
        {
            Id = record.Id,
            RunOn = record.RunOn,
            Checksum = record.Checksum,
            Kind = record.Kind,
            Replaces = record.Replaces,
            Direction = "Up",
            Status = OpenSearchMigrationRecord.StatusSucceeded,
            AppliedBy = $"{Environment.MachineName}/{Environment.ProcessId}"
        };

        var response = await _client.IndexAsync( lifted, idx => idx
            .Index( _options.LedgerIndex )
            .Id( lifted.Id )
            .OpType( precondition == WritePrecondition.MustNotExist ? OpType.Create : OpType.Index )
            .Refresh( Refresh.WaitFor )
        ).ConfigureAwait( false );

        if ( response.IsValid )
            return WriteOutcome.Created;

        // OpType.Create against an existing _id surfaces version_conflict_engine_exception (409).
        if ( precondition == WritePrecondition.MustNotExist
             && response.ApiCall?.HttpStatusCode == 409 )
        {
            var existing = await ReadAsync( record.Id ).ConfigureAwait( false );
            if ( existing != null && string.Equals( existing.Checksum, record.Checksum, StringComparison.Ordinal ) )
                return WriteOutcome.AlreadyExistsBenign;
            return WriteOutcome.PreconditionFailed;
        }

        var detail = response.OriginalException?.Message
            ?? response.ServerError?.Error?.ToString()
            ?? "Unknown ledger write failure.";
        throw new OpenSearchProviderException(
            $"Ledger write for `{record.Id}` failed: {detail}",
            response.OriginalException ?? new InvalidOperationException( detail ) );
    }

    public async Task DeleteAsync( string recordId )
    {
        _logger.LogDebug( "Running {action} with `{recordId}`", nameof( DeleteAsync ), recordId );

        var response = await _client.DeleteAsync<MigrationRecord>( recordId, d => d
            .Index( _options.LedgerIndex )
            .Refresh( Refresh.WaitFor )
        ).ConfigureAwait( false );

        if ( !response.IsValid && response.ApiCall.HttpStatusCode != 404 )
        {
            var detail = response.OriginalException?.Message ?? "Unknown ledger delete failure.";
            throw new OpenSearchProviderException(
                $"Ledger delete for `{recordId}` failed: {detail}",
                response.OriginalException ?? new InvalidOperationException( detail ) );
        }
    }
}
