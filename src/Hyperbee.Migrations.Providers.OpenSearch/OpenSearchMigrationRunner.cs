using Microsoft.Extensions.Logging;

namespace Hyperbee.Migrations.Providers.OpenSearch;

/// <summary>
/// OpenSearch-typed <see cref="MigrationRunner"/> subclass (per ADR-0023).
/// Provides a unique DI handle (<c>GetRequiredService&lt;OpenSearchMigrationRunner&gt;()</c>)
/// so multi-provider hosts can resolve each provider's runner independently
/// without shadowing.
/// </summary>
public sealed class OpenSearchMigrationRunner : MigrationRunner
{
    public OpenSearchMigrationRunner(
        IMigrationRecordStore recordStore,
        OpenSearchMigrationOptions options,
        ILoggerFactory loggerFactory )
        : base( recordStore, options, loggerFactory )
    {
    }
}
