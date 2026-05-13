using Hyperbee.Migrations;
using Hyperbee.Migrations.Providers.Postgres;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hyperbee.Migrations.Postgres.Samples;

/// <summary>
/// IMigrationHost implementation discovered by the CLI (per ADR-0024). Wires
/// the sample project's existing Add{Provider}Migrations setup with the
/// caller-supplied connection string. The CLI's squash + recover verbs
/// discover this type by walking the migration assembly's reference closure.
/// </summary>
public class PostgresMigrationsHost : IMigrationHost
{
    public Task<IServiceProvider> ConfigureAsync(
        MigrationHostContext context, CancellationToken cancellationToken = default )
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>( new ConfigurationBuilder().Build() );
        services.AddLogging();

        var dataSource = new NpgsqlDataSourceBuilder( context.ConnectionString ).Build();
        services.AddSingleton( dataSource );

        services.AddPostgresMigrations( opts =>
        {
            opts.Assemblies = new[] { typeof( PostgresMigrationsHost ).Assembly };
            context.OverrideOptions?.Invoke( opts );
        } );

        return Task.FromResult<IServiceProvider>( services.BuildServiceProvider() );
    }
}
