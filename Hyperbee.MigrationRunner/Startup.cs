using Couchbase.Extensions.DependencyInjection;
using Hyperbee.Migrations.Couchbase;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hyperbee.MigrationRunner;

internal class Startup
{
    public IConfiguration Configuration { get; set; }

    public Startup( IConfiguration configuration )
    {
        Configuration = configuration;
    }

    public void ConfigureContainer( IServiceCollection services )
    {
        services.AddCouchbase( Configuration );
        services.AddCouchbaseMigrations( Configuration );
    }
}

internal static class StartupExtensions
{
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
        } )
        .AddCouchbaseBucket<IMigrationBucketProvider>( bucket );

        return services;
    }

    public static IServiceCollection AddCouchbaseMigrations( this IServiceCollection services, IConfiguration config )
    {
        var bucketName = config["Migrations:BucketName"];
        var scopeName = config["Migrations:ScopeName"];
        var collectionName = config["Migrations:CollectionName"];

        var mutexEnabled = config.GetValue<bool>( "Migrations:Mutex:Enabled" );
        var mutexName = config["Migrations:Mutex:Name"];
        var mutexMaxLifetime = TimeSpan.FromSeconds( config.GetValue( "Migrations:Mutex:MaxLifetime", 3600 ) );
        var mutexExpireInterval = TimeSpan.FromSeconds( config.GetValue( "Migrations:Mutex:ExpireInterval", 30 ) );
        var mutexRenewInterval = TimeSpan.FromSeconds( config.GetValue( "Migrations:Mutex:RenewInterval", 15 ) );

        services.AddCouchbaseMigrations( c =>
        {
            c.BucketName = bucketName;
            c.ScopeName = scopeName;
            c.CollectionName = collectionName;

            if ( !mutexEnabled )
                return;

            c.MutexEnabled = true;
            c.MutexName = mutexName;
            c.MutexMaxLifetime = mutexMaxLifetime;
            c.MutexExpireInterval = mutexExpireInterval;
            c.MutexRenewInterval = mutexRenewInterval;
        } );

        return services;
    }
}