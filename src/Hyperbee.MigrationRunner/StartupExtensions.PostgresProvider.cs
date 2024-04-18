using Hyperbee.Migrations.Providers.Postgres;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Core;
using Serilog.Events;

// BF TODO We should provide a generalized mechanism for providers to participate in runner setup

namespace Hyperbee.MigrationRunner;

internal static class StartupExtensionsPostgresProvider
{
    public static IServiceCollection AddPostgresProvider( this IServiceCollection services, IConfiguration config, ILogger logger = null )
    {
        var connectionString = config["Postgresql:ConnectionString"]; // from appsettings.<ENV>.json

        logger?.Information( $"Connecting to `{connectionString}`." );

        services.AddNpgsqlDataSource( connectionString );

        return services;
    }

    public static IServiceCollection AddPostgresMigrations( this IServiceCollection services, IConfiguration config )
    {

        var lockingEnabled = config.GetValue<bool>( "Migrations:Lock:Enabled" );
        var lockName = config["Migrations:Lock:Name"];


        var profiles = (IList<string>) config.GetSection( "Migrations:Profiles" )
            .Get<IEnumerable<string>>() ?? Enumerable.Empty<string>()
            .ToList();

        services.AddPostgresMigrations( c =>
        {
            c.Profiles = profiles;
            c.LockName = lockName;
            c.LockingEnabled = lockingEnabled;

            // TODO: What do we need configured?
            // c.SchemaName = "migration";
            // c.TableName = "ledger";
            // c.LockName = "ledger_lock";
        } );

        return services;
    }

    internal static LoggerConfiguration AddPostgresFilters( this LoggerConfiguration self )
    {
        var npgsqlLevelSwitch = new LoggingLevelSwitch();
        self.MinimumLevel.Override( "Npgsql", npgsqlLevelSwitch );

        npgsqlLevelSwitch.MinimumLevel = LogEventLevel.Warning;
        return self;
    }

}
