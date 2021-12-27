using System.Net;
using System.Text.RegularExpressions;
using Couchbase;
using Couchbase.Core.Exceptions;
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
                        .AddEnvironmentVariables()
                        .AddCommandLine( args, SwitchMappings() ); 
                } )
                .ConfigureServices( ( context, services ) =>
                {
                    services
                        .AddCouchbase( context.Configuration )
                        .AddCouchbaseMigrations( context.Configuration )
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
            .AddCouchbaseFilters()
            .WriteTo.File( jsonFormatter, pathFormat, rollingInterval: RollingInterval.Day, shared: true )
            .WriteTo.Console( restrictedToMinimumLevel: LogEventLevel.Information )
            .CreateLogger();

        return Log.ForContext( typeof(Program) );
    }

    private static IDictionary<string,string> SwitchMappings()
    {
        // pass array of FromAssemblies: --n:0 AssemblyName1 --n:1 AssemblyName2

        return new Dictionary<string, string>()
        {
            // short names
            { "-n", "FromPaths" },
            { "-a", "FromAssemblies" },
            { "-p", "Profiles" },
            { "-b", "BucketName" },
            { "-s", "ScopeName" },
            { "-c", "CollectionName" },

            // aliases
            { "--names", "FromPaths" },
            { "--assemblies", "FromAssemblies" },
            { "--profiles", "Profiles" },
            { "--bucket", "BucketName" },
            { "--scope", "ScopeName" },
            { "--collection", "CollectionName" }
        };
    }
}

internal static class CouchbaseLogFilters
{
    internal static LoggerConfiguration AddCouchbaseFilters( this LoggerConfiguration self )
    {
        static TValue WithProperty<TValue>( LogEvent x, string propertyName )
        {
            return (TValue)((ScalarValue) x.Properties[propertyName]).Value;
        }

        // For GetAllScopesAsync false exceptions
        self.Filter.ByExcluding( x => x.Exception is CouchbaseException { Context: ManagementErrorContext { HttpStatus: HttpStatusCode.OK } } );

        // For GetBucketAsync noise caused by resolving buckets
        var pattern = new Regex( @"^The Bucket \[.+\] could not be selected." ); // log noise cause by couchbase net client

        self.Filter.ByExcluding( x => WithProperty<string>( x, "SourceContext" ) == "Couchbase.Core.ClusterNode" && pattern.IsMatch( x.MessageTemplate.Text ) );

        return self;
    }
}