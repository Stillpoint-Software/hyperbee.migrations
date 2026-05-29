using System.Threading;
using System.Threading.Tasks;

namespace Hyperbee.Migrations;

/// <summary>
/// Optional capability on an <see cref="IMigrationRecordStore"/> for Tier-2
/// transaction-scoped apply (ADR-0028). A store implements this only when its
/// backing engine can wrap a migration's body and the ledger write in one
/// transaction (Postgres always; MongoDB on a replica set). Stores whose engines
/// have no usable transaction for migration work (OpenSearch, Aerospike,
/// Couchbase DDL) deliberately do NOT implement this, so the runner falls back to
/// the Tier-1 in-flight sentinel (ADR-0027).
/// </summary>
public interface ITransactionalRecordStore
{
    /// <summary>
    /// Begins a transaction spanning the migration body and journal write, or
    /// returns <c>null</c> when a transaction is not available for this run (e.g.
    /// a MongoDB standalone deployment, or an operator opt-out). A null result
    /// tells the runner to use the Tier-1 sentinel path instead.
    /// </summary>
    /// <remarks>
    /// The returned scope's provider handle is published on
    /// <see cref="MigrationContext.AmbientTransaction"/> by the runner; the store's
    /// own <see cref="IMigrationRecordStore.WriteAsync(MigrationRecord, WritePrecondition, CancellationToken)"/>
    /// and the provider's resource runner must enroll in that ambient handle when
    /// present.
    /// </remarks>
    Task<IMigrationTransactionScope> BeginTransactionAsync( CancellationToken cancellationToken = default );
}
