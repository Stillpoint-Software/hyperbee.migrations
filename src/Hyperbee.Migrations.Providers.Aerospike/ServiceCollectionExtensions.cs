using System.Reflection;
using System.Runtime.Loader;
using Hyperbee.Migrations.Providers.Aerospike.Resources;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hyperbee.Migrations.Providers.Aerospike;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAerospikeMigrations( this IServiceCollection services )
    {
        return AddAerospikeMigrations( services, null, Assembly.GetCallingAssembly() );
    }

    public static IServiceCollection AddAerospikeMigrations( this IServiceCollection services, Action<AerospikeMigrationOptions> configuration )
    {
        return AddAerospikeMigrations( services, configuration, Assembly.GetCallingAssembly() );
    }

    private static IServiceCollection AddAerospikeMigrations( IServiceCollection services, Action<AerospikeMigrationOptions> configuration, Assembly defaultAssembly )
    {
        AerospikeMigrationOptions AerospikeMigrationOptionsFactory( IServiceProvider provider )
        {
            var options = new AerospikeMigrationOptions( new DefaultMigrationActivator( provider ) );

            // invoke the configuration

            configuration?.Invoke( options );

            // concat any options.Assemblies with IConfiguration `FromAssemblies` and `FromPaths`

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

        services.AddSingleton( AerospikeMigrationOptionsFactory );
        services.AddSingleton<MigrationOptions>( provider => provider.GetRequiredService<AerospikeMigrationOptions>() );
        services.AddSingleton<IMigrationRecordStore, AerospikeRecordStore>();
        services.AddSingleton<MigrationRunner>();
        services.AddTransient( typeof( AerospikeResourceRunner<> ) );

        return services;
    }

    private static IEnumerable<T> GetEnumerable<T>( this IConfiguration config, string key )
        => config.GetSection( key ).Get<IEnumerable<T>>() ?? [];
}
