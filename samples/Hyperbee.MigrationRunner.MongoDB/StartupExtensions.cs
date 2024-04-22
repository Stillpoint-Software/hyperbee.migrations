using Hyperbee.Migrations.Providers.MongoDB;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Hyperbee.MigrationRunner.MongoDB;

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

    public static IServiceCollection AddMongoDbProvider( this IServiceCollection services, IConfiguration config, ILogger logger = null )
    {
        var connectionString = config["MongoDb:ConnectionString"]; // from appsettings.<ENV>.json

        logger?.Information( $"Connecting to `{connectionString}`." );

        services.AddTransient<IMongoClient, MongoClient>( _ => new MongoClient( connectionString ) );

        return services;
    }

    public static IServiceCollection AddMongoDbMigrations( this IServiceCollection services, IConfiguration config )
    {
        var lockingEnabled = config.GetValue<bool>( "Migrations:Lock:Enabled" );
        var lockName = config["Migrations:Lock:Name"];

        var profiles = (IList<string>) config.GetSection( "Migrations:Profiles" )
            .Get<IEnumerable<string>>() ?? Enumerable.Empty<string>()
            .ToList();

        var databaseName = config.GetValue<string>( "Migrations:DatabaseName" );
        var collectionName = config.GetValue<string>( "Migrations:CollectionName" );

        services.AddMongoDBMigrations( c =>
        {
            c.Profiles = profiles;
            c.LockingEnabled = lockingEnabled;

            c.DatabaseName = databaseName;
            c.CollectionName = collectionName;
        } );

        return services;
    }

    internal static LoggerConfiguration AddMongoDbFilters( this LoggerConfiguration self )
    {
        var npgsqlLevelSwitch = new LoggingLevelSwitch();
        self.MinimumLevel.Override( "MongoDB", npgsqlLevelSwitch );

        npgsqlLevelSwitch.MinimumLevel = LogEventLevel.Warning;
        return self;
    }

}

internal static class ConfigurationHelper
{
    internal static string EnvironmentAppSettingsName => $"appsettings.{Environment.GetEnvironmentVariable( "DOTNET_ENVIRONMENT" ) ?? "Development"}.json";
}
