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
