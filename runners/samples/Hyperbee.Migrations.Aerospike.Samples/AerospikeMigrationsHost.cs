using Aerospike.Client;
using Hyperbee.Migrations;
using Hyperbee.Migrations.Providers.Aerospike;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Hyperbee.Migrations.Aerospike.Samples;

/// <summary>
/// IMigrationHost implementation discovered by the CLI (per ADR-0024). Wires
/// the sample project's existing AddAerospikeMigrations setup with the
/// caller-supplied connection string (Aerospike `host:port` form).
/// </summary>
public class AerospikeMigrationsHost : IMigrationHost
{
    public Task<IServiceProvider> ConfigureAsync(
        MigrationHostContext context, CancellationToken cancellationToken = default )
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>( new ConfigurationBuilder().Build() );
        services.AddLogging();

        var (host, port) = ParseHostPort( context.ConnectionString );
        var client = new AsyncClient( host, port );
        services.AddSingleton<IAerospikeClient>( client );
        services.AddSingleton<IAsyncClient>( client );

        services.AddAerospikeMigrations( opts =>
        {
            opts.Assemblies = new[] { typeof( AerospikeMigrationsHost ).Assembly };
            context.OverrideOptions?.Invoke( opts );
        } );

        return Task.FromResult<IServiceProvider>( services.BuildServiceProvider() );
    }

    private static (string Host, int Port) ParseHostPort( string connection )
    {
        var colon = connection.IndexOf( ':' );
        if ( colon < 0 ) return (connection, 3000);
        return (connection.Substring( 0, colon ), int.Parse( connection.Substring( colon + 1 ) ));
    }
}
