using Hyperbee.Migrations;
using Hyperbee.Migrations.Providers.MongoDB;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using MongoDB.Driver;

namespace Hyperbee.Migrations.MongoDB.Samples;

/// <summary>
/// IMigrationHost implementation discovered by the CLI (per ADR-0024). Wires
/// the sample project's AddMongoDBMigrations setup with the caller-supplied
/// connection string.
/// </summary>
public class MongoDBMigrationsHost : IMigrationHost
{
    public Task<IServiceProvider> ConfigureAsync(
        MigrationHostContext context, CancellationToken cancellationToken = default )
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>( new ConfigurationBuilder().Build() );
        services.AddLogging();

        services.AddSingleton<IMongoClient>( new MongoClient( context.ConnectionString ) );

        services.AddMongoDBMigrations( opts =>
        {
            opts.Assemblies = new[] { typeof( MongoDBMigrationsHost ).Assembly };
            context.OverrideOptions?.Invoke( opts );
        } );

        return Task.FromResult<IServiceProvider>( services.BuildServiceProvider() );
    }
}
