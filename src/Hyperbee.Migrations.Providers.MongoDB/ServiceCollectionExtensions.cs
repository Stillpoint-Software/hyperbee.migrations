using System.Reflection;
using System.Runtime.Loader;
using Hyperbee.Migrations.Providers.MongoDB.Resources;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hyperbee.Migrations.Providers.MongoDB;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMongoDBMigrations( this IServiceCollection services )
    {
        return AddMongoDBMigrations( services, null, Assembly.GetCallingAssembly() );
    }

    public static IServiceCollection AddMongoDBMigrations( this IServiceCollection services, Action<MongoDBMigrationOptions> configuration )
    {
        return AddMongoDBMigrations( services, configuration, Assembly.GetCallingAssembly() );
    }

    private static IServiceCollection AddMongoDBMigrations( IServiceCollection services, Action<MongoDBMigrationOptions> configuration, Assembly defaultAssembly )
    {
        MongoDBMigrationOptions PostgresMigrationOptionsFactory( IServiceProvider provider )
        {
            var options = new MongoDBMigrationOptions( new DefaultMigrationActivator( provider ) );

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
        services.AddSingleton<MigrationOptions>( provider => provider.GetRequiredService<MongoDBMigrationOptions>() );
        services.AddSingleton<IMigrationRecordStore, MongoDBRecordStore>();
        services.AddSingleton<MigrationRunner>();
        services.AddTransient( typeof( MongoDBResourceRunner<> ) );// technically singleton works because of the nature of migrations, but even so ..

        return services;
    }

    private static IEnumerable<T> GetEnumerable<T>( this IConfiguration config, string key )
        => config.GetSection( key )
            .Get<IEnumerable<T>>() ?? Enumerable.Empty<T>();
}
