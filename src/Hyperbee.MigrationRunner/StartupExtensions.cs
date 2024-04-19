using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;

namespace Hyperbee.MigrationRunner;

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

    //BF TODO we should allow Providers to be dynamically bound
    //        currently we are hard linked to couchbase

    public static IServiceCollection AddProvider( this IServiceCollection services, IConfiguration config, ILogger logger = null )
    {
        var provider = config["Provider"];
        return provider switch
        {
            "Postgresql" => services.AddPostgresProvider( config, logger ),
            "MongoDb" => services.AddMongoDbProvider( config, logger ),
            "Couchbase" => services.AddCouchbaseProvider( config, logger ),
            _ => throw new InvalidOperationException( $"Invalid Provider: {provider}" )
        };
    }

    public static IServiceCollection AddMigrations( this IServiceCollection services, IConfiguration config )
    {
        var provider = config["Provider"];
        return provider switch
        {
            "Postgresql" => services.AddPostgresMigrations( config ),
            "MongoDb" => services.AddMongoDbMigrations( config ),
            "Couchbase" => services.AddCouchbaseMigrations( config ),
            _ => throw new InvalidOperationException( $"Invalid Provider: {provider}" )
        };
    }

    internal static LoggerConfiguration AddProviderLoggerFilters( this LoggerConfiguration loggerConfig, IConfiguration config )
    {
        var provider = config["Provider"];
        return provider switch
        {
            "Postgresql" => loggerConfig.AddPostgresFilters(),
            "MongoDb" => loggerConfig.AddMongoDbFilters(),
            "Couchbase" => loggerConfig.AddCouchbaseFilters(),
            _ => throw new InvalidOperationException( $"Invalid Provider: {provider}" )
        };
    }
}

internal static class ConfigurationHelper
{
    internal static string EnvironmentAppSettingsName => $"appsettings.{Environment.GetEnvironmentVariable("DOTNET_ENVIRONMENT") ?? "Development"}.json";
}