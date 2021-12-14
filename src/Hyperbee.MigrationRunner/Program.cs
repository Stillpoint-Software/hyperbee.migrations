using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace Hyperbee.MigrationRunner;

internal class Program
{
    public static async Task Main( string[] args )
    {
        var config = CreateLocalConfiguration(); // local config without secrets
        var logger = CreateLogger( config );

        try
        {
            logger.Information( "Starting host..." );

            await Host
                .CreateDefaultBuilder()
                .ConfigureAppConfiguration( builder =>
                {
                    builder
                        .AddAppSettingsFile()
                        .AddAppSettingsEnvironmentFile()
                        .AddUserSecrets<Program>()
                        .AddEnvironmentVariables();
                } )
                .ConfigureServices( ( context, services ) =>
                {
                    services
                        .AddCouchbase( context.Configuration )
                        .AddCouchbaseMigrations( context.Configuration )
                        .AddHostedService<MigrationRunnerService>();
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
            Log.CloseAndFlush();
        }
    }

    private static IConfiguration CreateLocalConfiguration()
    {
        return new ConfigurationBuilder()
            .SetBasePath( Directory.GetCurrentDirectory() )
            .AddAppSettingsFile()
            .AddAppSettingsEnvironmentFile()
            .AddEnvironmentVariables()
            .Build();
    }

    private static ILogger CreateLogger( IConfiguration config )
    {
        var jsonFormatter = new CompactJsonFormatter();
        var pathFormat = $".{Path.DirectorySeparatorChar}logs{Path.DirectorySeparatorChar}hyperbee-migrations-.json";

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .ReadFrom.Configuration( config )
            .Enrich.FromLogContext()
            .WriteTo.File( jsonFormatter, pathFormat )
            .WriteTo.Console( restrictedToMinimumLevel: LogEventLevel.Information )
            .CreateLogger();

        return Log.ForContext( typeof(Program) );
    }
}