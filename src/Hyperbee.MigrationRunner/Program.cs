using Couchbase.Extensions.DependencyInjection;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Serilog;
using Serilog.Events;
using Serilog.Formatting.Compact;
using ILogger = Serilog.ILogger;

namespace Hyperbee.MigrationRunner;

internal static class Program
{
    public static async Task Main( string[] args )
    {
        var config = CreateLocalConfiguration(); // local config without secrets
        var logger = CreateLogger( config );

        ICouchbaseLifetimeService couchbaseLifetime = null;

        try
        {
            logger.Information( "Starting ..." );

            var host = Host.CreateDefaultBuilder()
                .ConfigureAppConfiguration( builder =>
                {
                    builder.AddJsonSettingsAndEnvironment()
                        .AddUserSecrets( typeof(Program).Assembly );
                } )
                .ConfigureServices( ( context, services ) =>
                {
                    services.AddCouchbase( context.Configuration );
                    services.AddCouchbaseMigrations( context.Configuration );
                } )
                .UseSerilog()
                .Build();

            // for this application, choosing Build() and CreateScope() instead of .RunConsoleAsync()
            // which requires a registered IHostService and adds a lot of extra boilerplate.

            using var serviceScope = host.Services.CreateScope();
            {
                try
                {
                    var provider = serviceScope.ServiceProvider;

                    couchbaseLifetime = provider.GetRequiredService<ICouchbaseLifetimeService>();
                    var app = provider.GetRequiredService<Hyperbee.Migrations.MigrationRunner>();
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
            if ( couchbaseLifetime != null )
                await couchbaseLifetime.CloseAsync();

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

        return Log.ForContext( typeof(Program) );
    }
}