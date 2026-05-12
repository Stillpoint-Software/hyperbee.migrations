using Microsoft.Extensions.Logging;

namespace Hyperbee.Migrations.Providers.Aerospike;

/// <summary>
/// Aerospike-typed <see cref="MigrationRunner"/> subclass (per ADR-0023).
/// Provides a unique DI handle (<c>GetRequiredService&lt;AerospikeMigrationRunner&gt;()</c>)
/// so multi-provider hosts can resolve each provider's runner independently
/// without shadowing.
/// </summary>
public sealed class AerospikeMigrationRunner : MigrationRunner
{
    public AerospikeMigrationRunner(
        IMigrationRecordStore recordStore,
        AerospikeMigrationOptions options,
        ILoggerFactory loggerFactory )
        : base( recordStore, options, loggerFactory )
    {
    }
}
