using Microsoft.Extensions.Logging;

namespace Hyperbee.Migrations.Providers.Couchbase;

/// <summary>
/// Couchbase-typed <see cref="MigrationRunner"/> subclass (per ADR-0023).
/// Provides a unique DI handle (<c>GetRequiredService&lt;CouchbaseMigrationRunner&gt;()</c>)
/// so multi-provider hosts can resolve each provider's runner independently
/// without shadowing.
/// </summary>
public sealed class CouchbaseMigrationRunner : MigrationRunner
{
    public CouchbaseMigrationRunner(
        IMigrationRecordStore recordStore,
        CouchbaseMigrationOptions options,
        ILoggerFactory loggerFactory )
        : base( recordStore, options, loggerFactory )
    {
    }
}
