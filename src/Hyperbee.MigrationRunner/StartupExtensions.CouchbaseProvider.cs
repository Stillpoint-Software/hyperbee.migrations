using System.Net;
using System.Text.RegularExpressions;
using Couchbase;
using Couchbase.Core.Exceptions;
using Couchbase.Extensions.DependencyInjection;
using Hyperbee.Migrations.Providers.Couchbase;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Serilog;
using Serilog.Events;

// BF TODO We should provide a generalized mechanism for providers to participate in runner setup

namespace Hyperbee.MigrationRunner;

internal static partial class StartupExtensionsCouchbaseProvider
{
    public static IServiceCollection AddCouchbaseProvider( this IServiceCollection services, IConfiguration config, ILogger logger = null )
    {
        var connectionString = config["Couchbase:ConnectionString"]; // from appsettings.<ENV>.json couchbase://localhost
        var userName = config["Couchbase:UserName"]; // from secrets.json or aws:secrets
        var password = config["Couchbase:Password"]; // from secrets.json or aws:secrets
        var bucket = config["Migrations:BucketName"]; // from appsettings.json

        var maxHttpConnections = config.GetValue<int>( "Couchbase:MaxConnectionLimit" );

        if ( maxHttpConnections <= 0 )
            maxHttpConnections = 10;

        logger?.Information( $"User `{userName}` connecting to `{connectionString}`." );

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

        var profiles = (IList<string>) config.GetSection( "Migrations:Profiles" )
            .Get<IEnumerable<string>>() ?? Enumerable.Empty<string>()
            .ToList();

        services.AddCouchbaseMigrations( c =>
        {
            c.Profiles = profiles;

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

    internal static LoggerConfiguration AddCouchbaseFilters( this LoggerConfiguration self )
    {
        // For GetAllScopesAsync false exceptions
        self.Filter.ByExcluding( x => x.Exception is CouchbaseException { Context: ManagementErrorContext { HttpStatus: HttpStatusCode.OK } } );

        // For GetBucketAsync noise caused by resolving buckets
        var pattern1 = RegexBucketNotSelectedMessage(); // log noise cause by couchbase net client
        self.Filter.ByExcluding( x => WithProperty<string>( x, "SourceContext" ) == "Couchbase.Core.ClusterNode" && pattern1.IsMatch( x.MessageTemplate.Text ) );

        // For internal couchbase timeouts waiting for bucket creation
        // For GetBucketAsync noise caused by resolving buckets
        var pattern2 = RegexClusterMapIssueMessage(); // log noise cause by couchbase net client
        self.Filter.ByExcluding( x => WithProperty<string>( x, "SourceContext" ) == "Couchbase.Core.Configuration.Server.ConfigHandler" && pattern2.IsMatch( x.MessageTemplate.Text ) && x.Exception is UnambiguousTimeoutException );

        return self;

        static TValue WithProperty<TValue>( LogEvent x, string propertyName )
        {
            return (TValue) ((ScalarValue) x.Properties[propertyName]).Value;
        }
    }

    [GeneratedRegex( @"^The Bucket \[.+\] could not be selected." )]
    private static partial Regex RegexBucketNotSelectedMessage();

    [GeneratedRegex( "^Issue getting Cluster Map on server {server}!" )]
    private static partial Regex RegexClusterMapIssueMessage();
}
