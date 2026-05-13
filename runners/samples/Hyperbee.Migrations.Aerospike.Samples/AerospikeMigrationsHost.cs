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
    public async Task<IServiceProvider> ConfigureAsync(
        MigrationHostContext context, CancellationToken cancellationToken = default )
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>( new ConfigurationBuilder().Build() );
        services.AddLogging();

        var (host, port) = ParseHostPort( context.ConnectionString );

        // Bounded retry for first-connect race against freshly-spawned
        // ephemeral containers. The daemon's cluster-map briefly advertises
        // its internal Docker address before the seed handshake stabilizes;
        // failIfNotConnected=false plus a short loop tolerates the
        // "existing connection was forcibly closed" path that
        // `new AsyncClient(host, port)` hits during tend startup.
        var policy = new AsyncClientPolicy
        {
            timeout = 30_000,
            failIfNotConnected = false
        };
        AsyncClient client = null;
        for ( var attempt = 0; attempt < 10 && client == null; attempt++ )
        {
            try
            {
                client = new AsyncClient( policy, host, port );
            }
            catch ( AerospikeException )
            {
                if ( attempt == 9 ) throw;
                await Task.Delay( TimeSpan.FromMilliseconds( 500 ), cancellationToken ).ConfigureAwait( false );
            }
        }

        // failIfNotConnected=false allows the AsyncClient ctor to return even
        // when the seed handshake hasn't completed. The record store's
        // InitializeAsync checks IAerospikeClient.Connected on entry and
        // throws "client is not connected" if the cluster-tend thread is
        // still bringing the seed node online. Poll briefly so we hand the
        // DI container a client that's actually ready to serve requests.
        for ( var attempt = 0; attempt < 20 && !client.Connected; attempt++ )
        {
            await Task.Delay( TimeSpan.FromMilliseconds( 250 ), cancellationToken ).ConfigureAwait( false );
        }

        services.AddSingleton<IAerospikeClient>( client );
        services.AddSingleton<IAsyncClient>( client );

        services.AddAerospikeMigrations( opts =>
        {
            opts.Assemblies = new[] { typeof( AerospikeMigrationsHost ).Assembly };
            context.OverrideOptions?.Invoke( opts );
        } );

        return services.BuildServiceProvider();
    }

    private static (string Host, int Port) ParseHostPort( string connection )
    {
        var colon = connection.IndexOf( ':' );
        if ( colon < 0 ) return (connection, 3000);
        return (connection.Substring( 0, colon ), int.Parse( connection.Substring( colon + 1 ) ));
    }
}
