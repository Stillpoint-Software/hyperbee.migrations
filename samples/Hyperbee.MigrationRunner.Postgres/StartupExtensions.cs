using Hyperbee.Migrations.Providers.Postgres;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Hyperbee.MigrationRunner.Postgres;

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

    public static IServiceCollection AddPostgresProvider( this IServiceCollection services, IConfiguration config, ILogger logger = null )
    {
        var connectionString = config["Postgresql:ConnectionString"]; // from appsettings.<ENV>.json

        //Note: do not log sensitive data
        //logger?.Information( $"Connecting to `{connectionString}`." );

        services.AddNpgsqlDataSource( connectionString );

        return services;
    }

    public static IServiceCollection AddPostgresMigrations( this IServiceCollection services, IConfiguration config )
    {

        var lockingEnabled = config.GetValue<bool>( "Migrations:Lock:Enabled" );
        var lockName = config["Migrations:Lock:Name"];


        var profiles = (IList<string>) config.GetSection( "Migrations:Profiles" )
            .Get<IEnumerable<string>>() ?? Enumerable.Empty<string>()
            .ToList();

        var schemaName = config.GetValue<string>( "Migrations:SchemaName" );
        var tableName = config.GetValue<string>( "Migrations:TableName" );

        services.AddPostgresMigrations( c =>
        {
            c.Profiles = profiles;
            c.LockName = lockName;
            c.LockingEnabled = lockingEnabled;

            c.SchemaName = schemaName;
            c.TableName = tableName;
        } );

        return services;
    }

    internal static LoggerConfiguration AddPostgresFilters( this LoggerConfiguration self )
    {
        var npgsqlLevelSwitch = new LoggingLevelSwitch();
        self.MinimumLevel.Override( "Npgsql", npgsqlLevelSwitch );

        npgsqlLevelSwitch.MinimumLevel = LogEventLevel.Warning;
        return self;
    }
}

internal static class ConfigurationHelper
{
    internal static string EnvironmentAppSettingsName => $"appsettings.{Environment.GetEnvironmentVariable( "DOTNET_ENVIRONMENT" ) ?? "Development"}.json";
}
