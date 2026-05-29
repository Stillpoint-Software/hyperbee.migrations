using System.Threading;
using System.Threading.Tasks;
using Npgsql;

namespace Hyperbee.Migrations.Providers.Postgres;

// ADR-0028 Tier-2 scope for Postgres: owns a single open NpgsqlConnection +
// NpgsqlTransaction for the duration of one migration. Published on
// MigrationContext.AmbientTransaction so PostgresResourceRunner (the body) and
// PostgresRecordStore (the journal write) enroll their commands in the same
// transaction. The runner commits on success, rolls back on failure, and always
// disposes. Postgres has fully transactional DDL+DML, so a clean rollback leaves
// neither partial schema/data nor a ledger row (fail-clean).
internal sealed class PostgresMigrationTransaction : IMigrationTransactionScope
{
    public NpgsqlConnection Connection { get; }
    public NpgsqlTransaction Transaction { get; }

    private int _completed;

    private PostgresMigrationTransaction( NpgsqlConnection connection, NpgsqlTransaction transaction )
    {
        Connection = connection;
        Transaction = transaction;
    }

    public static async Task<PostgresMigrationTransaction> CreateAsync( NpgsqlDataSource dataSource, CancellationToken cancellationToken )
    {
        var connection = await dataSource.OpenConnectionAsync( cancellationToken ).ConfigureAwait( false );
        var transaction = await connection.BeginTransactionAsync( cancellationToken ).ConfigureAwait( false );
        return new PostgresMigrationTransaction( connection, transaction );
    }

    public async Task CommitAsync()
    {
        if ( Interlocked.Exchange( ref _completed, 1 ) == 0 )
            await Transaction.CommitAsync().ConfigureAwait( false );
    }

    public async Task RollbackAsync()
    {
        // Idempotent: the runner calls Rollback in its catch even if a prior
        // Commit ran (the guard makes the second call a no-op). Disposing an
        // uncommitted transaction also rolls back, so this is belt-and-suspenders.
        if ( Interlocked.Exchange( ref _completed, 1 ) == 0 )
            await Transaction.RollbackAsync().ConfigureAwait( false );
    }

    public async ValueTask DisposeAsync()
    {
        await Transaction.DisposeAsync().ConfigureAwait( false );
        await Connection.DisposeAsync().ConfigureAwait( false );
    }
}
