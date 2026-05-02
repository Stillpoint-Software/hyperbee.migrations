using Hyperbee.Migrations.Providers.OpenSearch;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using OpenSearch.Client;
using Serilog;
using Serilog.Core;
using Serilog.Events;

namespace Hyperbee.MigrationRunner.OpenSearch;

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

    public static IServiceCollection AddOpenSearchProvider( this IServiceCollection services, IConfiguration config, ILogger logger = null )
    {
        // Do not log credentials. Connection-string-only logging is safe.
        var connectionString = config["OpenSearch:ConnectionString"] ?? "http://localhost:9200";
        logger?.Information( $"Connecting to `{connectionString}`." );

        // R-21: provider-side AddOpenSearchClient handles all three core auth
        // modes (Basic, ApiKey, ClientCertificate) plus Anonymous and the
        // legacy flat OpenSearch:UserName/Password back-compat. SigV4 (AWS
        // Managed) lands as an opt-in extension in plan task 3.2.
        services.AddOpenSearchClient( config );

        return services;
    }

    public static IServiceCollection AddOpenSearchMigrations( this IServiceCollection services, IConfiguration config )
    {
        var lockingEnabled = config.GetValue<bool>( "Migrations:Lock:Enabled" );
        var lockName = config["Migrations:LockName"];
        var lockIndex = config["Migrations:LockIndex"];
        var ledgerIndex = config["Migrations:LedgerIndex"];

        var profiles = (IList<string>) (config.GetSection( "Migrations:Profiles" )
            .Get<IEnumerable<string>>() ?? []).ToList();

        // R-19: ForceResume bypasses the partially_rolled_back lockout. CLI
        // exposure is `--force-resume`; the operator should set this only
        // after manually reconciling cluster state.
        var forceResume = config.GetValue<bool>( "Migrations:ForceResume" );

        services.AddOpenSearchMigrations( c =>
        {
            c.Profiles = profiles;
            c.LockingEnabled = lockingEnabled;

            if ( !string.IsNullOrEmpty( lockName ) )
                c.LockName = lockName;
            if ( !string.IsNullOrEmpty( lockIndex ) )
                c.LockIndex = lockIndex;
            if ( !string.IsNullOrEmpty( ledgerIndex ) )
                c.LedgerIndex = ledgerIndex;

            c.ForceResume = forceResume;
        } );

        return services;
    }

    internal static LoggerConfiguration AddOpenSearchFilters( this LoggerConfiguration self )
    {
        // OpenSearch.Client logs at Information for every request; raise to
        // Warning so the runner's Information-level console output stays
        // about the migration run, not per-request HTTP chatter.
        var openSearchLevelSwitch = new LoggingLevelSwitch();
        self.MinimumLevel.Override( "OpenSearch", openSearchLevelSwitch );

        openSearchLevelSwitch.MinimumLevel = LogEventLevel.Warning;
        return self;
    }
}

internal static class ConfigurationHelper
{
    internal static string EnvironmentAppSettingsName => $"appsettings.{Environment.GetEnvironmentVariable( "DOTNET_ENVIRONMENT" ) ?? "Development"}.json";
}
