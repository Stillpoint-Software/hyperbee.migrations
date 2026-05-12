using Couchbase.Extensions.DependencyInjection;
using Hyperbee.Migrations;
using Hyperbee.Migrations.Providers.Couchbase;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Hyperbee.Migrations.Couchbase.Samples;

/// <summary>
/// IMigrationHost implementation discovered by the CLI (per ADR-0024). Wires
/// the sample project's AddCouchbaseMigrations setup with the caller-supplied
/// connection string.
/// </summary>
public class CouchbaseMigrationsHost : IMigrationHost
{
    public Task<IServiceProvider> ConfigureAsync(
        MigrationHostContext context, CancellationToken cancellationToken = default )
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>( new ConfigurationBuilder().Build() );
        services.AddLogging();

        services.AddCouchbase( opts =>
        {
            opts.ConnectionString = context.ConnectionString;
            opts.UserName = "Administrator";
            opts.Password = "password";
        } );

        services.AddCouchbaseMigrations( opts =>
        {
            opts.BucketName = "hyperbee";
            opts.Assemblies = new[] { typeof( CouchbaseMigrationsHost ).Assembly };
            context.OverrideOptions?.Invoke( opts );
        } );

        return Task.FromResult<IServiceProvider>( services.BuildServiceProvider() );
    }
}
