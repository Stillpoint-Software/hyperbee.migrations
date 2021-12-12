using Couchbase.Extensions.DependencyInjection;
using Hyperbee.Migrations.Couchbase;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hyperbee.MigrationRunner;

internal static class StartupExtensions
{
    internal static IConfigurationBuilder AddJsonSettingsAndEnvironment( this IConfigurationBuilder builder )
    {
        return builder
            .AddJsonFile( "appsettings.json", optional: false, reloadOnChange: true )
            .AddJsonFile( $"appsettings.{Environment.GetEnvironmentVariable( "NETCORE_ENVIRONMENT" ) ?? "Development"}.json", optional: true )
            .AddEnvironmentVariables();
    }

    public static IServiceCollection AddCouchbase( this IServiceCollection services, IConfiguration config )
    {
        var connectionString = config["Couchbase:ConnectionString"]; // from appsettings.<ENV>.json couchbase://localhost
        var userName = config["Couchbase:UserName"]; // from secrets.json or aws:secrets
        var password = config["Couchbase:Password"]; // from secrets.json or aws:secrets
        var bucket = config["Migrations:BucketName"]; // from appsettings.json

        var maxHttpConnections = config.GetValue<int>( "Couchbase:MaxConnectionLimit" );

        if ( maxHttpConnections <= 0 )
            maxHttpConnections = 10;

        services.AddCouchbase( c =>
        {
            c.EnableTls = false;
            c.WithBuckets( bucket );
            c.WithConnectionString( connectionString );
            c.WithCredentials( userName, password );
            c.MaxHttpConnections = maxHttpConnections;
        } );

        return services;
    }

    public static IServiceCollection AddCouchbaseMigrations( this IServiceCollection services, IConfiguration config )
    {
        var bucketName = config["Migrations:BucketName"];
        var scopeName = config["Migrations:ScopeName"];
        var collectionName = config["Migrations:CollectionName"];

        var lockingEnabled = config.GetValue<bool>( "Migrations:Lock:Enabled" );
        var lockName = config["Migrations:Lock:Name"];
        var lockMaxLifetime = TimeSpan.FromSeconds( config.GetValue( "Migrations:Lock:MaxLifetime", 3600 ) );
        var lockExpireInterval = TimeSpan.FromSeconds( config.GetValue( "Migrations:Lock:ExpireInterval", 30 ) );
        var lockRenewInterval = TimeSpan.FromSeconds( config.GetValue( "Migrations:Lock:RenewInterval", 15 ) );

        services.AddCouchbaseMigrations( c =>
        {
            c.BucketName = bucketName;
            c.ScopeName = scopeName;
            c.CollectionName = collectionName;

            if ( !lockingEnabled )
                return;

            c.LockingEnabled = true;
            c.LockName = lockName;
            c.LockMaxLifetime = lockMaxLifetime;
            c.LockExpireInterval = lockExpireInterval;
            c.LockRenewInterval = lockRenewInterval;
        } );

        return services;
    }
}