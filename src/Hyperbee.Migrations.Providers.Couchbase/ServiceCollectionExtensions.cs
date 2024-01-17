using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Hyperbee.Migrations.Providers.Couchbase.Resources;
using Hyperbee.Migrations.Providers.Couchbase.Services;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hyperbee.Migrations.Providers.Couchbase;

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

        services.AddSingleton( CouchbaseMigrationOptionsFactory );
        services.AddSingleton<MigrationOptions>( provider => provider.GetRequiredService<CouchbaseMigrationOptions>() );
        services.AddSingleton<IMigrationRecordStore, CouchbaseRecordStore>();
        services.AddSingleton<MigrationRunner>();
        
        services.AddTransient( typeof(CouchbaseResourceRunner<>) ); // technically singleton works because of the nature of migrations, but even so ..

        // add support for calling couchbase web sdk api (outside the couchbase net sdk).

        services.AddScoped<CouchbaseAuthenticationHandler>();

        services.AddHttpClient();
        services.AddHttpClient<ICouchbaseRestApiService, CouchbaseRestApiService>()
            .AddHttpMessageHandler<CouchbaseAuthenticationHandler>();

        services.AddTransient<ICouchbaseBootstrapper,CouchbaseBootstrapper>();

        return services;
    }

    private static IEnumerable<T> GetEnumerable<T>( this IConfiguration config, string key ) 
        => config.GetSection( key )
            .Get<IEnumerable<T>>() ?? Enumerable.Empty<T>();
}