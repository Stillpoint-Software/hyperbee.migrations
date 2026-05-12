using Microsoft.Extensions.Logging;

namespace Hyperbee.Migrations.Providers.MongoDB;

/// <summary>
/// MongoDB-typed <see cref="MigrationRunner"/> subclass (per ADR-0023).
/// Provides a unique DI handle (<c>GetRequiredService&lt;MongoDBMigrationRunner&gt;()</c>)
/// so multi-provider hosts can resolve each provider's runner independently
/// without shadowing.
/// </summary>
public sealed class MongoDBMigrationRunner : MigrationRunner
{
    public MongoDBMigrationRunner(
        IMigrationRecordStore recordStore,
        MongoDBMigrationOptions options,
        ILoggerFactory loggerFactory )
        : base( recordStore, options, loggerFactory )
    {
    }
}
