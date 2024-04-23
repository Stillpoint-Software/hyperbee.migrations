using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace Hyperbee.MigrationRunner.MongoDB;

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
                        .AddMongoDbProvider( context.Configuration, logger )
                        .AddMongoDbMigrations( context.Configuration )
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
            .AddMongoDbFilters()
            .WriteTo.File( jsonFormatter, pathFormat, rollingInterval: RollingInterval.Day, shared: true )
            .WriteTo.Console( restrictedToMinimumLevel: LogEventLevel.Information )
            .CreateLogger();

        return Log.ForContext( typeof( Program ) );
    }

    private static Dictionary<string, string> SwitchMappings()
    {
        // pass array of FromAssemblies: -a AssemblyName1 -a AssemblyName2

        return new Dictionary<string, string>()
        {
            // short names
            { "-f", "[Migrations:FromPaths]" },
            { "-a", "[Migrations:FromAssemblies]" },
            { "-p", "[Migrations:Profiles]" },
            { "-d", "Migrations:DatabaseName" },
            { "-v", "Migrations:CollectionName" },

            { "-cs", "MongoDb:ConnectionString" },

            // aliases
            { "--file", "[Migrations:FromPaths]" },
            { "--assembly", "[Migrations:FromAssemblies]" },
            { "--profile", "[Migrations:Profiles]" },
            { "--database", "Migrations:DatabaseName" },
            { "--collection", "Migrations:CollectionName" },

            { "--connection", "MongoDb:ConnectionString" }
        };
    }
}

