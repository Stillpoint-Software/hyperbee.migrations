#nullable enable
namespace Hyperbee.Migrations.Providers.OpenSearch;

// Provider-specific exception hierarchy. Typed exceptions allow callers to
// pattern-match on classes of failure without parsing log strings.

public class OpenSearchProviderException : Exception
{
    public OpenSearchProviderException( string message ) : base( message ) { }
    public OpenSearchProviderException( string message, Exception inner ) : base( message, inner ) { }
}

public sealed class OpenSearchNotReadyException : OpenSearchProviderException
{
    public OpenSearchNotReadyException( string message ) : base( message ) { }
    public OpenSearchNotReadyException( string message, Exception inner ) : base( message, inner ) { }
}

public sealed class OpenSearchLedgerSchemaMismatchException : OpenSearchProviderException
{
    public OpenSearchLedgerSchemaMismatchException( string message ) : base( message ) { }
}

public sealed class MigrationLockExpiredException : OpenSearchProviderException
{
    public MigrationLockExpiredException( string message ) : base( message ) { }
}

public sealed class AwsSigV4NotConfiguredException : OpenSearchProviderException
{
    public AwsSigV4NotConfiguredException( string message ) : base( message ) { }
}

// R-19: thrown by RollbackStatementsFromAsync when a statement entry has no
// `rollback` field. The author's intent is "this operation is irreversible";
// the runner refuses Down rather than guess at an inverse.

public sealed class RollbackNotSupportedException : OpenSearchProviderException
{
    public int StatementIndex { get; }

    public RollbackNotSupportedException( int statementIndex, string message )
        : base( message )
    {
        StatementIndex = statementIndex;
    }
}

// R-15: thrown at the resource-runner entry point when a statements.json
// file declares a `context:` block AND the runner is configured with
// ContextResolutionPolicy.RequireExplicit AND ActiveContext is null/empty.
// `RequireExplicit` is the production default (set by WithProductionDefaults
// per R-29); silent prod-everywhere behavior is forbidden by the trust
// boundary, so the runner must fail loud rather than guess.

public sealed class MissingActiveContextException : OpenSearchProviderException
{
    public MissingActiveContextException( string message )
        : base( message ) { }
}

// R-19: thrown when a migration's ledger record is in `partially_rolled_back`
// state and the operator has not opted into recovery via OpenSearchMigrationOptions.ForceResume.
// Subsequent runs are refused in either direction until the operator
// inspects the cluster, reconciles state, and explicitly re-runs with
// ForceResume = true (or deletes the record manually for a fresh Up).

public sealed class OpenSearchPartialRollbackException : OpenSearchProviderException
{
    public string RecordId { get; }
    public int? FailedStatementIndex { get; }

    public OpenSearchPartialRollbackException( string recordId, int? failedStatementIndex, string message )
        : base( message )
    {
        RecordId = recordId;
        FailedStatementIndex = failedStatementIndex;
    }
}
