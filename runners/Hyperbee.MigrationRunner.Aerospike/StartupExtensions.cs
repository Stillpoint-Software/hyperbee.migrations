using Aerospike.Client;
using Hyperbee.Migrations.Providers.Aerospike;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Hyperbee.MigrationRunner.Aerospike;

internal static class StartupExtensions
{
    internal static IConfigurationBuilder AddAppSettingsFile( this IConfigurationBuilder builder )
    {
        return builder
            .AddJsonFile( "appsettings.json", optional: false, reloadOnChange: true );
    }

    internal static IConfigurationBuilder AddAppSettingsEnvironmentFile( this IConfigurationBuilder builder )
    {
        return builder
            .AddJsonFile( ConfigurationHelper.EnvironmentAppSettingsName, optional: true );
    }

    public static IServiceCollection AddAerospikeProvider( this IServiceCollection services, IConfiguration config, ILogger logger = null )
    {
        var connectionString = config["Aerospike:ConnectionString"];

        var host = connectionString ?? "localhost";
        var port = 3000;

        // parse host:port if provided
        if ( connectionString != null && connectionString.Contains( ':' ) )
        {
            var parts = connectionString.Split( ':' );
            host = parts[0];
            port = int.Parse( parts[1] );
        }

        var asyncClient = new AsyncClient( host, port );

        services.AddSingleton<IAsyncClient>( asyncClient );
        services.AddSingleton<IAerospikeClient>( asyncClient );

        return services;
    }

    public static IServiceCollection AddAerospikeMigrations( this IServiceCollection services, IConfiguration config )
    {
        var lockingEnabled = config.GetValue<bool>( "Migrations:Lock:Enabled" );
        var lockName = config["Migrations:Lock:Name"];
        var lockMaxLifetime = TimeSpan.FromSeconds( config.GetValue( "Migrations:Lock:MaxLifetime", 3600 ) );

        var profiles = (IList<string>) config.GetSection( "Migrations:Profiles" )
            .Get<IEnumerable<string>>() ?? Enumerable.Empty<string>()
            .ToList();

        var namespaceName = config.GetValue<string>( "Migrations:Namespace" );
        var migrationSet = config.GetValue<string>( "Migrations:MigrationSet" );

        services.AddAerospikeMigrations( c =>
        {
            c.Profiles = profiles;
            c.LockName = lockName;
            c.LockingEnabled = lockingEnabled;
            c.LockMaxLifetime = lockMaxLifetime;

            c.Namespace = namespaceName;
            c.MigrationSet = migrationSet;
        } );

        return services;
    }

    internal static LoggerConfiguration AddAerospikeFilters( this LoggerConfiguration self )
    {
        var aerospikeLevelSwitch = new LoggingLevelSwitch();
        self.MinimumLevel.Override( "Aerospike", aerospikeLevelSwitch );

        aerospikeLevelSwitch.MinimumLevel = LogEventLevel.Warning;
        return self;
    }
}

internal static class ConfigurationHelper
{
    internal static string EnvironmentAppSettingsName => $"appsettings.{Environment.GetEnvironmentVariable( "DOTNET_ENVIRONMENT" ) ?? "Development"}.json";
}
