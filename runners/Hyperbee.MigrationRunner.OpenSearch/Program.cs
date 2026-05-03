using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace Hyperbee.MigrationRunner.OpenSearch;

internal class Program
{
    public static async Task Main( string[] args )
    {
        var logger = CreateLogger();

        try
        {
            logger.Information( "Starting host..." );
            logger.Information( $"Using environment settings '{ConfigurationHelper.EnvironmentAppSettingsName}'." );

            await Host
                .CreateDefaultBuilder()
                .ConfigureAppConfiguration( builder =>
                {
                    builder
                        .AddAppSettingsFile()
                        .AddAppSettingsEnvironmentFile()
                        .AddUserSecrets<Program>()
                        .AddEnvironmentVariables()
                        .AddCommandLineEx( args, SwitchMappings() );
                } )
                .ConfigureServices( ( context, services ) =>
                {
                    services
                        .AddOpenSearchProvider( context.Configuration, logger )
                        .AddOpenSearchMigrations( context.Configuration )
                        .AddHostedService<MainService>();
                } )
                .UseSerilog()
                .RunConsoleAsync();
        }
        catch ( Exception ex )
        {
            logger.Fatal( ex, "Initialization Failure." );
        }
        finally
        {
            await Log.CloseAndFlushAsync();
        }
    }

    private static ILogger CreateLogger()
    {
        var config = new ConfigurationBuilder()
            .SetBasePath( Directory.GetCurrentDirectory() )
            .AddAppSettingsFile()
            .AddAppSettingsEnvironmentFile()
            .AddEnvironmentVariables()
            .Build();

        var jsonFormatter = new CompactJsonFormatter();
        var pathFormat = $".{Path.DirectorySeparatorChar}logs{Path.DirectorySeparatorChar}hyperbee-migrations-.json";

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .ReadFrom.Configuration( config )
            .Enrich.FromLogContext()
            .AddOpenSearchFilters()
            .WriteTo.File( jsonFormatter, pathFormat, rollingInterval: RollingInterval.Day, shared: true )
            .WriteTo.Console( restrictedToMinimumLevel: LogEventLevel.Information )
            .CreateLogger();

        return Log.ForContext( typeof( Program ) );
    }

    private static Dictionary<string, string> SwitchMappings()
    {
        return new Dictionary<string, string>()
        {
            // short names
            { "-f", "[Migrations:FromPaths]" },
            { "-a", "[Migrations:FromAssemblies]" },
            { "-p", "[Migrations:Profiles]" },
            { "-cs", "OpenSearch:ConnectionString" },
            { "-u", "OpenSearch:Authentication:UserName" },

            // aliases
            { "--file", "[Migrations:FromPaths]" },
            { "--assembly", "[Migrations:FromAssemblies]" },
            { "--profile", "[Migrations:Profiles]" },

            { "--connection", "OpenSearch:ConnectionString" },

            // R-21 — auth (basic, API key, mTLS). Mode is a string parsed
            // case-insensitively: Anonymous | Basic | ApiKey | ClientCertificate.
            // Setting Mode is optional when only Basic credentials are given —
            // the provider treats `--user` + `--password` without an explicit
            // Mode as Basic (back-compat with the runner's earlier shape).
            { "--auth-mode", "OpenSearch:Authentication:Mode" },

            { "--user", "OpenSearch:Authentication:UserName" },
            { "--password", "OpenSearch:Authentication:Password" },

            { "--api-key-id", "OpenSearch:Authentication:ApiKeyId" },
            { "--api-key", "OpenSearch:Authentication:ApiKey" },

            { "--client-cert", "OpenSearch:Authentication:ClientCertificatePath" },
            { "--client-cert-password", "OpenSearch:Authentication:ClientCertificatePassword" },

            { "--ledger", "Migrations:LedgerIndex" },
            { "--lock", "Migrations:LockIndex" },
            { "--lock-name", "Migrations:LockName" },

            // R-19: opt-in recovery from a partially_rolled_back ledger entry.
            // The provider option is OpenSearchMigrationOptions.ForceResume;
            // the operator passes --force-resume after they have manually
            // reconciled cluster state. Without this flag, ExistsAsync throws
            // OpenSearchPartialRollbackException on subsequent runs against a
            // partially-rolled-back record.
            { "--force-resume", "Migrations:ForceResume" }
        };
    }
}
