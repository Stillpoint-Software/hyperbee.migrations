using System.Reflection;
using System.Runtime.Loader;
using Hyperbee.Migrations.Providers.Postgres.Resources;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hyperbee.Migrations.Providers.Postgres;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddPostgresMigrations( this IServiceCollection services )
    {
        return AddPostgresMigrations( services, null, Assembly.GetCallingAssembly() );
    }

    public static IServiceCollection AddPostgresMigrations( this IServiceCollection services, Action<PostgresMigrationOptions> configuration )
    {
        return AddPostgresMigrations( services, configuration, Assembly.GetCallingAssembly() );
    }

    private static IServiceCollection AddPostgresMigrations( IServiceCollection services, Action<PostgresMigrationOptions> configuration, Assembly defaultAssembly )
    {
        PostgresMigrationOptions PostgresMigrationOptionsFactory( IServiceProvider provider )
        {
            var options = new PostgresMigrationOptions( new DefaultMigrationActivator( provider ) );

            // invoke the configuration

            configuration?.Invoke( options );

            // concat any options.Assemblies with IConfiguration `FromAssemblies` and

            var config = provider.GetRequiredService<IConfiguration>();

            var nameAssemblies = config
                .GetEnumerable<string>( "Migrations:FromAssemblies" )
                .Select( name => Assembly.Load( new AssemblyName( name ) ) );

            var pathAssemblies = config
                .GetEnumerable<string>( "Migrations:FromPaths" )
                .Select( name => AssemblyLoadContext.Default.LoadFromAssemblyPath( Path.GetFullPath( name ) ) );

            options.Assemblies = options.Assemblies
                .Concat( nameAssemblies )
                .Concat( pathAssemblies )
                .Distinct()
                .DefaultIfEmpty( defaultAssembly )
                .ToList();

            return options;
        }

        // Concrete provider-typed registrations (per ADR-0023). Stored via
        // TryAddSingleton + factory delegate so duplicate AddPostgresMigrations
        // calls are idempotent and the internal record-store type stays
        // internal.
        services.TryAddSingleton( PostgresMigrationOptionsFactory );
        services.TryAddSingleton<PostgresRecordStore>( provider => new PostgresRecordStore(
            provider.GetRequiredService<NpgsqlDataSource>(),
            provider.GetRequiredService<PostgresMigrationOptions>(),
            provider.GetRequiredService<ILogger<PostgresRecordStore>>() ) );
        services.TryAddSingleton( provider => new PostgresMigrationRunner(
            provider.GetRequiredService<PostgresRecordStore>(),
            provider.GetRequiredService<PostgresMigrationOptions>(),
            provider.GetRequiredService<ILoggerFactory>() ) );

        // Legacy single-provider aliases (per ADR-0023 amendment F1).
        // RegisterBaseAliases installs MigrationOptions / IMigrationRecordStore /
        // MigrationRunner pointing at this provider when called first;
        // replaces them with throwing factories naming both providers when
        // a second Add{Provider}Migrations call follows.
        services.RegisterBaseAliases(
            "Postgres",
            provider => provider.GetRequiredService<PostgresMigrationOptions>(),
            provider => provider.GetRequiredService<PostgresRecordStore>(),
            provider => provider.GetRequiredService<PostgresMigrationRunner>() );

        services.AddTransient( typeof( PostgresResourceRunner<> ) ); // technically singleton works because of the nature of migrations, but even so ..

        return services;
    }

    private static IEnumerable<T> GetEnumerable<T>( this IConfiguration config, string key )
        => config.GetSection( key ).Get<IEnumerable<T>>() ?? [];
}
