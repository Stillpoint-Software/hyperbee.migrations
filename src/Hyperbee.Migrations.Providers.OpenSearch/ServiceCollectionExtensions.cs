using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hyperbee.Migrations.Providers.OpenSearch;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddOpenSearchMigrations( this IServiceCollection services )
        => AddOpenSearchMigrations( services, null, Assembly.GetCallingAssembly() );

    public static IServiceCollection AddOpenSearchMigrations( this IServiceCollection services, Action<OpenSearchMigrationOptions> configuration )
        => AddOpenSearchMigrations( services, configuration, Assembly.GetCallingAssembly() );

    private static IServiceCollection AddOpenSearchMigrations( IServiceCollection services, Action<OpenSearchMigrationOptions> configuration, Assembly defaultAssembly )
    {
        OpenSearchMigrationOptions OpenSearchMigrationOptionsFactory( IServiceProvider provider )
        {
            var options = new OpenSearchMigrationOptions( new DefaultMigrationActivator( provider ) );

            configuration?.Invoke( options );

            // concat options.Assemblies with IConfiguration `FromAssemblies` and `FromPaths`

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

        services.AddSingleton( OpenSearchMigrationOptionsFactory );
        services.AddSingleton<MigrationOptions>( provider => provider.GetRequiredService<OpenSearchMigrationOptions>() );

        // IMigrationRecordStore registration deferred to Phase 1 — Task 1.6
        // services.AddSingleton<IMigrationRecordStore, OpenSearchRecordStore>();

        services.AddSingleton<MigrationRunner>();

        services.TryAddSingleton( TimeProvider.System );

        return services;
    }

    /// <summary>
    /// Marks the registration to apply production-safe defaults: Green health threshold,
    /// PerMigration waits, UNSAFE/NO WAIT justification required, RequireExplicit context
    /// resolution. Per ADR-0012 — explicit forcing function over hidden environment-profile
    /// coupling. Per-option settings chained after this win (handled by the options factory
    /// applying user configuration after defaults).
    /// </summary>
    /// <remarks>
    /// Phase 0 scaffolding registers the marker only. Phase 6 lands the options-factory
    /// integration that applies the four defaults before user configuration runs.
    /// </remarks>
    public static IServiceCollection WithProductionDefaults( this IServiceCollection services )
    {
        services.TryAddSingleton<UseProductionDefaultsMarker>();
        return services;
    }

    private static IEnumerable<T> GetEnumerable<T>( this IConfiguration config, string key )
        => config.GetSection( key ).Get<IEnumerable<T>>() ?? [];
}

internal sealed class UseProductionDefaultsMarker { }
