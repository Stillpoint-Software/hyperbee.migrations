using System;
using System.Reflection;
using Hyperbee.Migrations.Activators;
using Microsoft.Extensions.DependencyInjection;

namespace Hyperbee.Migrations;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMigrations( this IServiceCollection services )
    {
        return AddMigrationRunner( services, null, Assembly.GetCallingAssembly() );
    }

    public static IServiceCollection AddMigrations( this IServiceCollection services, Action<MigrationOptions> configuration )
    {
        return AddMigrationRunner( services, configuration, Assembly.GetCallingAssembly() );
    }

    private static IServiceCollection AddMigrationRunner( IServiceCollection services, Action<MigrationOptions> configuration, Assembly callingAssembly )
    {
        if ( callingAssembly == null )
            callingAssembly = Assembly.GetEntryAssembly();

        services.AddSingleton( provider => MigrationOptionsFactory( provider, callingAssembly, configuration ) );
        services.AddSingleton<IMigrationRecordStore, DefaultMigrationRecordStore>();
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