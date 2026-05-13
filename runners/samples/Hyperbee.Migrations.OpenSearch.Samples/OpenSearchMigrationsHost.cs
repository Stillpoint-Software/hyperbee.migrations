using Hyperbee.Migrations;
using Hyperbee.Migrations.Providers.OpenSearch;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenSearch.Client;

namespace Hyperbee.Migrations.OpenSearch.Samples;

/// <summary>
/// IMigrationHost implementation discovered by the CLI (per ADR-0024). Wires
/// the sample project's existing AddOpenSearchMigrations setup with the
/// caller-supplied endpoint URI as connection string.
/// </summary>
public class OpenSearchMigrationsHost : IMigrationHost
{
    public Task<IServiceProvider> ConfigureAsync(
        MigrationHostContext context, CancellationToken cancellationToken = default )
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>( new ConfigurationBuilder().Build() );
        services.AddLogging();

        var endpoint = new Uri( context.ConnectionString );
        // DefaultIndex avoids OpenSearch.Net's "Index name is null for the given
        // type" path on the typed surface when the runner's record store reads
        // through generic helpers that resolve via type inference. The record
        // store passes .Index(ledgerIndex) on every typed call explicitly, but
        // OpenSearch.Net's serializer still consults the index inferrer when
        // emitting strongly typed responses; a sensible default keeps that
        // path clean. Matches OpenSearchMigrationOptions.DefaultLedgerIndex.
        var settings = new ConnectionSettings( endpoint )
            .DefaultIndex( ".migrations" );
        services.AddSingleton<IOpenSearchClient>( new OpenSearchClient( settings ) );

        services.AddOpenSearchMigrations( opts =>
        {
            opts.Assemblies = new[] { typeof( OpenSearchMigrationsHost ).Assembly };
            context.OverrideOptions?.Invoke( opts );
        } );

        return Task.FromResult<IServiceProvider>( services.BuildServiceProvider() );
    }
}
