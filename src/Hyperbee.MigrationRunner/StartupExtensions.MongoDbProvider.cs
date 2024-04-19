using Hyperbee.Migrations.Providers.MongoDB;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Driver;
using Serilog;
using Serilog.Core;
using Serilog.Events;

// BF TODO We should provide a generalized mechanism for providers to participate in runner setup

namespace Hyperbee.MigrationRunner;

internal static class StartupExtensionsMongoDbProvider
{
    public static IServiceCollection AddMongoDbProvider( this IServiceCollection services, IConfiguration config, ILogger logger = null )
    {
        var connectionString = config["MongoDb:ConnectionString"]; // from appsettings.<ENV>.json

        logger?.Information( $"Connecting to `{connectionString}`." );

        services.AddTransient<IMongoClient, MongoClient>( _ => new MongoClient( connectionString ) );

        return services;
    }

    public static IServiceCollection AddMongoDbMigrations( this IServiceCollection services, IConfiguration config )
    {
        var lockingEnabled = config.GetValue<bool>( "Migrations:Lock:Enabled" );
        var lockName = config["Migrations:Lock:Name"];

        var profiles = (IList<string>) config.GetSection( "Migrations:Profiles" )
            .Get<IEnumerable<string>>() ?? Enumerable.Empty<string>()
            .ToList();

        services.AddMongoDBMigrations( c =>
        {
            c.Profiles = profiles;
            c.LockingEnabled = lockingEnabled;

            // TODO: What do we need configured?
            // c.CollectionName = "migration";
        });

        return services;
    }

    internal static LoggerConfiguration AddMongoDbFilters( this LoggerConfiguration self )
    {
        var npgsqlLevelSwitch = new LoggingLevelSwitch();
        self.MinimumLevel.Override( "MongoDB", npgsqlLevelSwitch );

        npgsqlLevelSwitch.MinimumLevel = LogEventLevel.Warning;
        return self;
    }

}
