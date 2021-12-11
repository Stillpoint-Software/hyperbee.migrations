using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;

namespace Hyperbee.MigrationRunner;

internal static class Program
{
    public static async Task Main( string[] args )
    {
        var config = CreateLocalConfiguration(); // local config without secrets
        var logger = CreateLogger( config );

        try
        {
            logger.Information( "Starting ..." );

            var host = Host.CreateDefaultBuilder()
                .ConfigureAppConfiguration( x =>
                {
                    x.AddJsonSettingsAndEnvironment()
                        .AddUserSecrets( typeof(Program).Assembly );
                } )
                .ConfigureServices( ( context, services ) =>
                {
                    var startup = new Startup( context.Configuration );
                    startup.ConfigureContainer( services );
                } )
                .UseSerilog()
                .Build();

            using var serviceScope = host.Services.CreateScope();
            {
                try
                {
                    var app = serviceScope.ServiceProvider.GetRequiredService<Hyperbee.Migrations.MigrationRunner>();
                    await app.RunAsync();
                }
                catch ( Exception ex )
                {
                    logger.Fatal( ex, "Application Failure." );
                }
            }
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
            .AddJsonSettingsAndEnvironment()
            .Build();
    }

    private static ILogger CreateLogger( IConfiguration config )
    {
        var jsonFormatter = new CompactJsonFormatter();
        var pathFormat = $".{Path.DirectorySeparatorChar}logs{Path.DirectorySeparatorChar}hyperbee-migrations.json";

        Log.Logger = new LoggerConfiguration()
            .MinimumLevel.Debug()
            .ReadFrom.Configuration( config )
            .Enrich.FromLogContext()
            .WriteTo.File( jsonFormatter, pathFormat )
            .WriteTo.Console( restrictedToMinimumLevel: LogEventLevel.Information )
            .CreateLogger();

        return Log.ForContext<Startup>();
    }
}

internal static class ConfigureExtensions
{
    internal static IConfigurationBuilder AddJsonSettingsAndEnvironment( this IConfigurationBuilder builder )
    {
        return builder
            .AddJsonFile( "appsettings.json", optional: false, reloadOnChange: true )
            .AddJsonFile( $"appsettings.{Environment.GetEnvironmentVariable( "ASPNETCORE_ENVIRONMENT" ) ?? "Development"}.json", optional: true )
            .AddEnvironmentVariables();
    }
}