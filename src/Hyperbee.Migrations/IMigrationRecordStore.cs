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
    async Task<IReadOnlySet<string>> IntersectWithAppliedAsync(
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
    /// Returns the subset of <paramref name="versions"/> that the ledger can
    /// satisfy via a transitive squash row -- a <c>Kind=Squash</c> row whose
    /// <c>Replaces</c> array contains the version (per ADR-0019 A6 transitivity
    /// rule). Shipped providers implement this with a single round-trip batch
    /// query. There is no safe default: a store that cannot answer this
    /// question cannot reconcile transitive squashes, so the default-interface
    /// method throws <see cref="NotSupportedException"/> rather than returning
    /// an empty set and silently misclassifying mature environments that
    /// auto-marked an inner squash.
    /// </summary>
    /// <exception cref="NotSupportedException">
    /// Always, when invoked on a store that has not overridden this method.
    /// Reached only when the runner is reconciling a <c>Kind=Squash</c>
    /// descriptor; v2 stores without squashes never enter this path.
    /// </exception>
    Task<IReadOnlySet<long>> IntersectWithSquashedAsync(
        IEnumerable<long> versions,
        CancellationToken cancellationToken = default )
    {
        if ( versions == null )
            throw new ArgumentNullException( nameof( versions ) );

        // R-3 fail-loud (was: return empty set silently). v2 stores without
        // any squash usage never reach this path -- MigrationRunner.ClassifySquashAsync
        // only invokes IntersectWithSquashedAsync when a Kind=Squash descriptor
        // is being reconciled. A custom store that handles squashes MUST scan
        // the ledger for rows where Kind=Squash AND Replaces contains the
        // input version. Failing silently here lets mature environments that
        // auto-marked an inner squash misclassify as Fresh against an outer
        // squash; surface the contract gap immediately instead.
        throw new NotSupportedException(
            $"IMigrationRecordStore implementation '{GetType().FullName}' does not override " +
            "IntersectWithSquashedAsync; transitive squash reconciliation (ADR-0019 A6) " +
            "cannot proceed. Override this method to scan the ledger for rows where " +
            "Kind=Squash and the Replaces array contains the input version, or use one " +
            "of the shipped record stores (Postgres, MongoDB, Couchbase, OpenSearch, " +
            "Aerospike) that implement the primitive natively." );
    }
}
