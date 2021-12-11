using System;
using System.Collections.Generic;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Hyperbee.Migrations.Couchbase;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCouchbaseMigrations( this IServiceCollection services )
    {
        return AddCouchbaseMigrationRunner( services, null, Assembly.GetCallingAssembly() );
    }

    public static IServiceCollection AddCouchbaseMigrations( this IServiceCollection services, Action<CouchbaseMigrationOptions> configuration )
    {
        return AddCouchbaseMigrationRunner( services, configuration, Assembly.GetCallingAssembly() );
    }

    private static IServiceCollection AddCouchbaseMigrationRunner( IServiceCollection services, Action<CouchbaseMigrationOptions> configuration, Assembly callingAssembly )
    {
        if ( callingAssembly == null )
            callingAssembly = Assembly.GetEntryAssembly();

        services.AddSingleton( provider => MigrationOptionsFactory( provider, callingAssembly, configuration ) );
        services.AddSingleton<IMigrationRecordStore, CouchbaseRecordStore>();
        services.AddSingleton<MigrationRunner>();

        return services;
    }

    private static MigrationOptions MigrationOptionsFactory( IServiceProvider provider, Assembly callingAssembly, Action<CouchbaseMigrationOptions> configuration = null )
    {
        var options = new CouchbaseMigrationOptions
        {
            MigrationActivator = new DefaultMigrationActivator( provider )
        };

        configuration?.Invoke( options );

        // if no assemblies were explicitly configured
        if ( options.Assemblies.Count == 0 )
            options.Assemblies.Add( callingAssembly );

        return options;
    }
}