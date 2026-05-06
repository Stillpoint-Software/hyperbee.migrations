#nullable enable
namespace Hyperbee.Migrations.Providers.OpenSearch;

// Provider-specific exception hierarchy. Typed exceptions allow callers to
// pattern-match on classes of failure without parsing log strings.

/// <summary>
/// Base type for all OpenSearch-provider exceptions. Catch this to handle any
/// provider-originated failure without coupling to a specific subclass.
/// </summary>
public class OpenSearchProviderException : Exception
{
    /// <summary>Initializes a new instance with a descriptive message.</summary>
    public OpenSearchProviderException( string message ) : base( message ) { }

    /// <summary>Initializes a new instance with a descriptive message and the underlying cause.</summary>
    public OpenSearchProviderException( string message, Exception inner ) : base( message, inner ) { }
}

/// <summary>
/// Bootstrap could not bring the cluster to a usable state — the ledger or lock
/// index could not be created/verified, the cluster did not reach the configured
/// health threshold, or a required step failed.
/// </summary>
public sealed class OpenSearchNotReadyException : OpenSearchProviderException
{
    /// <summary>Initializes a new instance with a descriptive message.</summary>
    public OpenSearchNotReadyException( string message ) : base( message ) { }

    /// <summary>Initializes a new instance with a descriptive message and the underlying cause.</summary>
    public OpenSearchNotReadyException( string message, Exception inner ) : base( message, inner ) { }
}

/// <summary>
/// Ledger index exists but its mapping is missing one of the required forensic
/// fields (R-06). The ledger schema is immutable; recreate the index to recover.
/// </summary>
public sealed class OpenSearchLedgerSchemaMismatchException : OpenSearchProviderException
{
    /// <summary>Initializes a new instance with a descriptive message.</summary>
    public OpenSearchLedgerSchemaMismatchException( string message ) : base( message ) { }
}

/// <summary>
/// Migration lock exceeded <see cref="OpenSearchMigrationOptions.LockMaxLifetime"/>.
/// The in-flight migration's <see cref="System.Threading.CancellationToken"/> has
/// been signaled; the runner is winding down.
/// </summary>
public sealed class MigrationLockExpiredException : OpenSearchProviderException
{
    /// <summary>Initializes a new instance with a descriptive message.</summary>
    public MigrationLockExpiredException( string message ) : base( message ) { }
}

/// <summary>
/// AWS SigV4 authentication was requested via
/// <see cref="OpenSearchAuthenticationOptions"/> but the required configuration
/// (region, credentials, service) was not supplied.
/// </summary>
public sealed class AwsSigV4NotConfiguredException : OpenSearchProviderException
{
    /// <summary>Initializes a new instance with a descriptive message.</summary>
    public AwsSigV4NotConfiguredException( string message ) : base( message ) { }
}

/// <summary>
/// R-19: thrown by Down execution when a statement has no <c>rollback</c> field.
/// The author's intent is "this operation is irreversible"; the runner refuses
/// to guess at an inverse rather than risk corruption.
/// </summary>
public sealed class RollbackNotSupportedException : OpenSearchProviderException
{
    /// <summary>Index of the statement (within the migration's statement list) that lacks a rollback definition.</summary>
    public int StatementIndex { get; }

    /// <summary>Initializes a new instance for the statement at <paramref name="statementIndex"/>.</summary>
    public RollbackNotSupportedException( int statementIndex, string message )
        : base( message )
    {
        StatementIndex = statementIndex;
    }
}

/// <summary>
/// R-15: thrown at the resource-runner entry point when a <c>statements.json</c>
/// declares a <c>context:</c> block, <see cref="ContextResolutionPolicy.RequireExplicit"/>
/// is in effect, and <see cref="OpenSearchMigrationOptions.ActiveContext"/> is unset.
/// </summary>
/// <remarks>
/// <see cref="ContextResolutionPolicy.RequireExplicit"/> is the production default
/// (per R-29's <c>WithProductionDefaults</c>); silent prod-everywhere behavior is
/// forbidden by the trust boundary, so the runner fails loud rather than guess.
/// </remarks>
public sealed class MissingActiveContextException : OpenSearchProviderException
{
    /// <summary>Initializes a new instance with a descriptive message.</summary>
    public MissingActiveContextException( string message )
        : base( message ) { }
}

/// <summary>
/// R-19: thrown when a migration's ledger record is in <c>partially_rolled_back</c>
/// state and the operator has not opted into recovery via
/// <see cref="OpenSearchMigrationOptions.ForceResume"/>. Subsequent runs are refused
/// in either direction until the operator inspects the cluster, reconciles state,
/// and explicitly re-runs with <c>ForceResume = true</c> (or deletes the record
/// manually for a fresh Up).
/// </summary>
public sealed class OpenSearchPartialRollbackException : OpenSearchProviderException
{
    /// <summary>Identifier of the migration record stuck in <c>partially_rolled_back</c> state.</summary>
    public string RecordId { get; }

    /// <summary>Index of the statement (within the migration's rollback sequence) that failed, if known.</summary>
    public int? FailedStatementIndex { get; }

    /// <summary>Initializes a new instance describing the stuck record and (optionally) the failing statement index.</summary>
    public OpenSearchPartialRollbackException( string recordId, int? failedStatementIndex, string message )
        : base( message )
    {
        RecordId = recordId;
        FailedStatementIndex = failedStatementIndex;
    }
}
