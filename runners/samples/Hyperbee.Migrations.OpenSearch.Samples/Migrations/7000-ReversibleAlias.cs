using Hyperbee.Migrations.Providers.OpenSearch.Resources;

namespace Hyperbee.Migrations.OpenSearch.Samples.Migrations;

// Sample 7: a migration with an opt-in rollback. Demonstrates R-19.
//
// Each statement entry in statements.json may carry an optional `rollback`
// property whose value is itself a statement string. UpAsync dispatches the
// `statement` fields in declaration order; DownAsync dispatches the
// `rollback` fields in REVERSE declaration order. The validation pass
// runs first — if any statement is missing `rollback`, the runner refuses
// Down with RollbackNotSupportedException(StatementIndex) BEFORE mutating
// anything (no half-rolled-back states).
//
// Partial-rollback semantics (R-19): if a rollback statement N fails after
// N+1..M have already rolled back, the migration's ledger entry is
// overwritten to status=partially_rolled_back with failedStatementIndex=N.
// Subsequent runs in EITHER direction are refused with
// OpenSearchPartialRollbackException unless the operator opts in to
// recovery via --force-resume on the runner CLI (or
// OpenSearchMigrationOptions.ForceResume = true programmatically).

[Migration( 7000 )]
public class ReversibleAlias( OpenSearchResourceRunner<ReversibleAlias> runner ) : Migration
{
    public override Task UpAsync( CancellationToken cancellationToken = default )
        => runner.StatementsFromAsync( "statements.json", cancellationToken );

    public override Task DownAsync( CancellationToken cancellationToken = default )
        => runner.RollbackStatementsFromAsync( this, "statements.json", cancellationToken );
}
