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
        //BF TODO currently hard coded for couchbase
        return services.AddCouchbaseProvider( config, logger );
    }

    public static IServiceCollection AddMigrations( this IServiceCollection services, IConfiguration config )
    {
        //BF TODO currently hard coded for couchbase
        return services.AddCouchbaseMigrations( config );
    }

    internal static LoggerConfiguration AddProviderLoggerFilters( this LoggerConfiguration loggerConfig )
    {
        //BF TODO currently hard coded for couchbase
        return loggerConfig.AddCouchbaseFilters();
    }
}

internal static class ConfigurationHelper
{
    internal static string EnvironmentAppSettingsName => $"appsettings.{Environment.GetEnvironmentVariable( "ASPNETCORE_ENVIRONMENT" ) ?? "Development"}.json";
}