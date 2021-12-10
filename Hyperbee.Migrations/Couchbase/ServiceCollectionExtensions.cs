using System;
using System.Reflection;
using Microsoft.Extensions.DependencyInjection;

namespace Hyperbee.Migrations.Couchbase;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCouchbaseMigrations( this IServiceCollection services )
    {
        return AddCouchbaseMigrationRunner( services, null, Assembly.GetCallingAssembly() );
    }

    public static IServiceCollection AddCouchbaseMigrations( this IServiceCollection services, Action<MigrationOptions> configuration )
    {
        return AddCouchbaseMigrationRunner( services, configuration, Assembly.GetCallingAssembly() );
    }

    private static IServiceCollection AddCouchbaseMigrationRunner( IServiceCollection services, Action<MigrationOptions> configuration, Assembly callingAssembly )
    {
        if ( callingAssembly == null )
            callingAssembly = Assembly.GetEntryAssembly();

        services.AddSingleton( provider => MigrationOptionsFactory( provider, callingAssembly, configuration ) );
        services.AddSingleton<IMigrationRecordStore, CouchbaseRecordStore>();
        services.AddSingleton<MigrationRunner>();

        return services;
    }

    private static MigrationOptions MigrationOptionsFactory( IServiceProvider provider, Assembly callingAssembly, Action<MigrationOptions> configuration = null )
    {
        var migrationResolver = new DefaultMigrationActivator( provider );
        var options = new MigrationOptions( migrationResolver );

        configuration?.Invoke( options );

        // if no assemblies explicitly configured
        if ( options.Assemblies.Count == 0 )
            options.Assemblies.Add( callingAssembly );

        return options;
    }
}