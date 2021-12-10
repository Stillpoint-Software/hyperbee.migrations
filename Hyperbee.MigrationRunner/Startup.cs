using Couchbase.Extensions.DependencyInjection;
using Hyperbee.Migrations;
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
        services.AddCouchbaseMigrations( _ =>
        {
            _.BucketName = Configuration["Migrations:BucketName"];
            _.ScopeName = Configuration["Migrations:ScopeName"];
            _.CollectionName = Configuration["Migrations:CollectionName"];

            if ( Configuration.GetValue<bool>( "Migrations:Mutex:Enabled" ) )
            {
                _.MutexEnabled = true;
                _.MutexName = Configuration["Migrations:Mutex:Name"];
                _.MutexMaxLifetime = TimeSpan.FromSeconds( Configuration.GetValue( "Migrations:Mutex:MaxLifetime", 3600 ) );
                _.MutexExpireInterval = TimeSpan.FromSeconds( Configuration.GetValue( "Migrations:Mutex:ExpireInterval", 30 ) );
                _.MutexRenewInterval = TimeSpan.FromSeconds( Configuration.GetValue( "Migrations:Mutex:RenewInterval", 15 ) );
            }
        } );
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
}