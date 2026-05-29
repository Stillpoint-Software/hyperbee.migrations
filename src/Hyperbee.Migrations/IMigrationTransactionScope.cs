using System;
using System.Threading.Tasks;

namespace Hyperbee.Migrations;

/// <summary>
/// A provider-owned transaction spanning a migration's body AND its journal write
/// (Tier 2, ADR-0028). When a record store can supply one
/// (<see cref="ITransactionalRecordStore"/>), the runner wraps the migration body
/// and the ledger write in a single scope: commit on success, rollback on any
/// failure (including interruption). Rollback is atomic, so an interrupted
/// transactional migration leaves no partial data and no ledger row -- the restart
/// is "fail-clean" and no in-flight sentinel (ADR-0027 Tier 1) is needed.
/// </summary>
/// <remarks>
/// The concrete scope carries the provider-specific ambient handle (e.g. a shared
/// <c>NpgsqlConnection</c>+<c>NpgsqlTransaction</c> for Postgres, or an
/// <c>IClientSessionHandle</c> for MongoDB). The runner publishes the scope on
/// <see cref="MigrationContext.AmbientTransaction"/> so the provider's resource
/// runner and record store enroll their operations in the same transaction.
/// </remarks>
public interface IMigrationTransactionScope : IAsyncDisposable
{
    /// <summary>Commits the transaction. Called after the body and journal write succeed.</summary>
    Task CommitAsync();

    /// <summary>
    /// Rolls back the transaction. Called when the body or journal write throws.
    /// Safe to call after a commit/rollback already happened (idempotent no-op).
    /// </summary>
    Task RollbackAsync();
}
