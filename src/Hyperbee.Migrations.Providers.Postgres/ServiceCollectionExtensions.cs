using System.Reflection;
using System.Runtime.Loader;
using Hyperbee.Migrations.Providers.Postgres.Resources;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

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

        services.AddSingleton( PostgresMigrationOptionsFactory );
        services.AddSingleton<MigrationOptions>( provider => provider.GetRequiredService<PostgresMigrationOptions>() );
        services.AddSingleton<IMigrationRecordStore, PostgresRecordStore>();
        services.AddSingleton<MigrationRunner>();
        
        services.AddTransient( typeof(PostgresResourceRunner<>) ); // technically singleton works because of the nature of migrations, but even so ..

        return services;
    }

    private static IEnumerable<T> GetEnumerable<T>( this IConfiguration config, string key ) 
        => config.GetSection( key )
            .Get<IEnumerable<T>>() ?? Enumerable.Empty<T>();
}