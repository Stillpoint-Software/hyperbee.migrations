using System;
using System.Collections.Generic;
using System.Threading;
using System.Threading.Tasks;

namespace Hyperbee.Migrations;

public interface IMigrationRecordStore
{
    Task InitializeAsync( CancellationToken cancellationToken = default );
    Task<IDisposable> CreateLockAsync();

    Task<bool> ExistsAsync( string recordId );
    Task<MigrationRecord> ReadAsync( string recordId );
    Task DeleteAsync( string recordId );
    Task WriteAsync( string recordId );

    /// <summary>
    /// Writes a record carrying checksum + kind + replaces metadata, optionally
    /// with a precondition. Shipped providers override this method; custom
    /// implementations may continue to use the legacy <see cref="WriteAsync(string)"/>
    /// overload — the default implementation here delegates to it and ignores
    /// the precondition + metadata. The default behavior preserves v2 semantics
    /// for unmigrated record stores; squash reconciliation requires the override.
    /// </summary>
    Task<WriteOutcome> WriteAsync(
        MigrationRecord record,
        WritePrecondition precondition = WritePrecondition.None,
        CancellationToken cancellationToken = default )
    {
        if ( record == null )
            throw new ArgumentNullException( nameof( record ) );

        record.EnsureLedgerIntegrity();
        // DIM default: legacy stores see only the recordId; checksum and kind are dropped.
        return WriteAsyncFallback( this, record );

        static async Task<WriteOutcome> WriteAsyncFallback( IMigrationRecordStore self, MigrationRecord r )
        {
            await self.WriteAsync( r.Id ).ConfigureAwait( false );
            return WriteOutcome.Created;
        }
    }

    /// <summary>
    /// Bulk realtime read — returns the subset of <paramref name="candidateIds"/>
    /// that already exist in the ledger. Shipped providers implement this with
    /// a single round-trip (BatchGet / MultiGet / mget / SELECT ... WHERE id = ANY).
    /// The default falls back to a per-id <see cref="ExistsAsync(string)"/> loop.
    /// Reconciliation requires realtime semantics: see ADR-0019 Phase 3.
    /// </summary>
    async Task<IReadOnlySet<string>> LoadAppliedVersionsAsync(
        IEnumerable<string> candidateIds,
        CancellationToken cancellationToken = default )
    {
        if ( candidateIds == null )
            throw new ArgumentNullException( nameof( candidateIds ) );

        var found = new HashSet<string>( StringComparer.Ordinal );
        foreach ( var id in candidateIds )
        {
            cancellationToken.ThrowIfCancellationRequested();
            if ( await ExistsAsync( id ).ConfigureAwait( false ) )
                found.Add( id );
        }
        return found;
    }

    /// <summary>
    /// Returns the subset of <paramref name="versions"/> for which the ledger
    /// can satisfy the squash obligation — either a direct row (<c>Migration</c>
    /// row whose version matches) or a transitive squash row whose <c>Replaces</c>
    /// set contains the version (per ADR-0019 A6 transitivity rule).
    /// The default implementation considers direct id matches only; mature
    /// environments that auto-marked an inner squash will fail
    /// <c>MidRangeSquashException</c> against an outer squash unless the
    /// custom store overrides this method.
    /// </summary>
    Task<IReadOnlySet<long>> LoadSatisfyingRowsAsync(
        IEnumerable<long> versions,
        CancellationToken cancellationToken = default )
    {
        if ( versions == null )
            throw new ArgumentNullException( nameof( versions ) );

        // Default: returns an empty set. v2 record stores have no primitive for
        // "scan ledger for rows whose Replaces contains v" and core has no
        // record-id convention knowledge here. Reconciliation against a squash
        // will see no satisfying rows, classify as Fresh (empty ledger) or
        // MidRangeSquashException (partial), forcing the operator to either
        // upgrade the store or run UpAsync on the squash. Shipped providers
        // override with a single-round-trip batch query.
        IReadOnlySet<long> empty = new HashSet<long>();
        return Task.FromResult( empty );
    }
}
