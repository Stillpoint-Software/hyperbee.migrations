using System;
using System.Linq;
using System.Reflection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hyperbee.Migrations.Couchbase;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCouchbaseMigrations( this IServiceCollection services )
    {
        return AddCouchbaseMigrations( services, null, Assembly.GetCallingAssembly() );
    }

    public static IServiceCollection AddCouchbaseMigrations( this IServiceCollection services, Action<CouchbaseMigrationOptions> configuration )
    {
        return AddCouchbaseMigrations( services, configuration, Assembly.GetCallingAssembly() );
    }

    private static IServiceCollection AddCouchbaseMigrations( IServiceCollection services, Action<CouchbaseMigrationOptions> configuration, Assembly defaultAssembly )
    {
        CouchbaseMigrationOptions CouchbaseMigrationOptionsFactory( IServiceProvider provider )
        {
            var options = new CouchbaseMigrationOptions( new DefaultMigrationActivator( provider ) );

            // invoke the configuration

            configuration?.Invoke( options );

            // concat any options.Assemblies with IConfiguration `FromAssemblies`

            options.Assemblies = provider.GetRequiredService<IConfiguration>()
                .GetSection( "Migrations:FromAssemblies" )
                .Get<string[]>()
                .Select( name => Assembly.Load( new AssemblyName( name ) ) )
                .Concat( options.Assemblies ) // add existing items
                .Distinct()
                .DefaultIfEmpty( defaultAssembly )
                .ToList();

            return options;
        }

        services.AddSingleton( CouchbaseMigrationOptionsFactory );
        services.AddSingleton<MigrationOptions>( provider => provider.GetRequiredService<CouchbaseMigrationOptions>() );
        services.AddSingleton<IMigrationRecordStore, CouchbaseRecordStore>();
        services.AddSingleton<MigrationRunner>();

        return services;
    }
}