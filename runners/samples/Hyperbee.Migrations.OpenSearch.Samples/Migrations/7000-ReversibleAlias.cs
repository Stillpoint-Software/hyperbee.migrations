using Hyperbee.Migrations.Providers.OpenSearch.Resources;

namespace Hyperbee.Migrations.OpenSearch.Samples.Migrations;

// Sample 7: a reversible migration authored in the recommended .pql form.
//
// Up and Down live in SEPARATE scripts: statements.pql (forward) and
// statements.down.pql (teardown). Both dispatch IN WRITTEN ORDER -- the
// author authors the explicit teardown sequence in statements.down.pql
// (reverse of Up here). This differs from the legacy .statements.json
// form, where each entry carries an inline `rollback` field and Down
// auto-reverses; that form remains supported for backward compatibility.
//
// If statements.down.pql is absent, the runner refuses Down loudly with
// RollbackNotSupportedException BEFORE mutating anything (no half states).
//
// Partial-rollback semantics (R-19): if a down statement N fails after
// earlier ones already applied, the migration's ledger entry is overwritten
// to status=partially_rolled_back with failedStatementIndex=N. Subsequent
// runs in EITHER direction are refused with OpenSearchPartialRollbackException
// unless the operator opts in to recovery via --force-resume on the runner
// CLI (or OpenSearchMigrationOptions.ForceResume = true programmatically).

[Migration( 7000 )]
public class ReversibleAlias( OpenSearchResourceRunner<ReversibleAlias> runner ) : Migration
{
    public override Task UpAsync( CancellationToken cancellationToken = default )
        => runner.StatementsFromAsync( "statements.pql", cancellationToken );

    public override Task DownAsync( CancellationToken cancellationToken = default )
        => runner.RollbackStatementsFromAsync( this, "statements.down.pql", cancellationToken );
}
