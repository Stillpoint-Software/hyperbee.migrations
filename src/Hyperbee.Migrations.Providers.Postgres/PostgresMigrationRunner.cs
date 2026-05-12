using Microsoft.Extensions.Logging;

namespace Hyperbee.Migrations.Providers.Postgres;

/// <summary>
/// Postgres-typed <see cref="MigrationRunner"/> subclass (per ADR-0023).
/// Provides a unique DI handle (<c>GetRequiredService&lt;PostgresMigrationRunner&gt;()</c>)
/// so multi-provider hosts can resolve each provider's runner independently
/// without shadowing.
/// </summary>
/// <remarks>
/// All behavior lives in the base type. This subclass exists for type
/// identity only.
/// </remarks>
public sealed class PostgresMigrationRunner : MigrationRunner
{
    public PostgresMigrationRunner(
        IMigrationRecordStore recordStore,
        PostgresMigrationOptions options,
        ILoggerFactory loggerFactory )
        : base( recordStore, options, loggerFactory )
    {
    }
}
