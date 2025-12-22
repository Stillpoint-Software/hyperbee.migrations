using System.Text;
using System.Text.Json;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Networks;
using Testcontainers.Couchbase;

namespace Hyperbee.Migrations.Integration.Tests.Container.Couchbase;

public class CouchbaseTestContainer
{
    public static string ConnectionString { get; set; }
    public static INetwork Network { get; set; }

    public static async Task Initialize( TestContext context )
    {
        var cancellationToken = context.CancellationTokenSource.Token;

        var network = new NetworkBuilder()
            .WithName( "couch_net" )
            .WithCleanUp( true )
            .Build();

        await network.CreateAsync( cancellationToken )
            .ConfigureAwait( false );

        var couchbaseContainer = new CouchbaseBuilder()
            .WithCleanUp( true )
            .WithNetwork( network )
            .WithNetworkAliases( "db" )

            .WithPortBinding( 80, 80 )
            .WithPortBinding( 11210, 11210 )
            .WithPortBinding( 8091, 8091 )
            .WithPortBinding( 8092, 8092 )
            .WithPortBinding( 8093, 8093 )
            .WithPortBinding( 8094, 8094 )
            .WithPortBinding( 8095, 8095 )
            .WithPortBinding( 8096, 8096 )

            .WithStartupCallback( ConfigureCouchbaseAsync )   // Override default test container config
            .Build();

        await couchbaseContainer.StartAsync( cancellationToken )
            .ConfigureAwait( false );

        ConnectionString = "couchbase://db";
        Network = network;
    }

    private static readonly ISet<CouchbaseService> EnabledServices = new HashSet<CouchbaseService> { CouchbaseService.Data, CouchbaseService.Index, CouchbaseService.Query, CouchbaseService.Search };

    private static readonly IWaitUntil WaitUntilNodeIsReady = new HttpWaitStrategy().ForPath( "/pools" ).ForPort( CouchbaseBuilder.MgmtPort );

    private static async Task ConfigureCouchbaseAsync( CouchbaseContainer container, CancellationToken cancellationToken = default )
    {
        await WaitStrategy.WaitUntilAsync(
                () => WaitUntilNodeIsReady.UntilAsync( container ),
                TimeSpan.FromSeconds( 5 ),
                TimeSpan.FromMinutes( 3 ),
                3, cancellationToken )
            .ConfigureAwait( false );

        var buckets = new List<string> { "hyperbee" };

        using ( var httpClient = new HttpClient( new RetryHandler() ) )
        {
            httpClient.BaseAddress = new UriBuilder( Uri.UriSchemeHttp, container.Hostname, container.GetMappedPublicPort( CouchbaseBuilder.MgmtPort ) ).Uri;

            using ( var request = new RenameNodeRequest( container ) )
            {
                using var response = await httpClient.SendAsync( request, cancellationToken )
                    .ConfigureAwait( false );
                await EnsureSuccessStatusCodeAsync( response )
                    .ConfigureAwait( false );
            }

            using ( var request = new SetupNodeServicesRequest( EnabledServices.ToArray() ) )
            {
                using var response = await httpClient.SendAsync( request, cancellationToken )
                    .ConfigureAwait( false );
                await EnsureSuccessStatusCodeAsync( response )
                    .ConfigureAwait( false );
            }

            using ( var request = new SetupMemoryQuotasRequest( EnabledServices.ToArray() ) )
            {
                using var response = await httpClient.SendAsync( request, cancellationToken )
                    .ConfigureAwait( false );
                await EnsureSuccessStatusCodeAsync( response )
                    .ConfigureAwait( false );
            }

            using ( var request = new SetupIndexRequest() )
            {
                using var response = await httpClient.SendAsync( request, cancellationToken )
                    .ConfigureAwait( false );
                await EnsureSuccessStatusCodeAsync( response )
                    .ConfigureAwait( false );
            }


            foreach ( var bucket in buckets )
            {
                using var request = new CreateBucketRequest( bucket );
                using var response = await httpClient.SendAsync( request, cancellationToken )
                    .ConfigureAwait( false );
                await EnsureSuccessStatusCodeAsync( response )
                    .ConfigureAwait( false );

            }

            // This HTTP request initiates the provisioning of the single-node cluster.
            // All subsequent requests following this HTTP request require credentials.
            // Setting the credentials upfront interfere with other HTTP requests.
            // We got frequently: System.IO.IOException The response ended prematurely.
            using ( var request = new SetupCredentialsRequest() )
            {
                using var response = await httpClient.SendAsync( request, cancellationToken )
                    .ConfigureAwait( false );
                await EnsureSuccessStatusCodeAsync( response )
                    .ConfigureAwait( false );
            }
        }

        // As long as we do not expose the bucket API, we do not need to iterate over all of them.
        var waitUntilBucketIsCreated = buckets.Aggregate( DotNet.Testcontainers.Builders.Wait.ForUnixContainer(), ( waitStrategy, bucket )
            => waitStrategy.UntilHttpRequestIsSucceeded( request
                => request
                    .ForPath( "/pools/default/buckets/" + bucket )
                    .ForPort( CouchbaseBuilder.MgmtPort )
                    .ForResponseMessageMatching( AllServicesEnabledAsync )
                    .WithHeader( BasicAuthenticationHeader.Key, BasicAuthenticationHeader.Value ) ) )
            .Build()
            .Last();

        await WaitStrategy.WaitUntilAsync( () => waitUntilBucketIsCreated.UntilAsync( container ), TimeSpan.FromSeconds( 2 ), TimeSpan.FromMinutes( 5 ), 1, cancellationToken )
            .ConfigureAwait( false );
    }

    private static readonly KeyValuePair<string, string> BasicAuthenticationHeader = new( "Authorization", "Basic " + Convert.ToBase64String( Encoding.GetEncoding( "ISO-8859-1" ).GetBytes( string.Join( ":", CouchbaseBuilder.DefaultUsername, CouchbaseBuilder.DefaultPassword ) ) ) );

    private static async Task EnsureSuccessStatusCodeAsync( HttpResponseMessage response )
    {
        try
        {
            response.EnsureSuccessStatusCode();
        }
        catch ( Exception e )
        {
            var content = await response.Content.ReadAsStringAsync()
                .ConfigureAwait( false );

            throw new InvalidOperationException( content, e );
        }
    }

    private static async Task<bool> AllServicesEnabledAsync( HttpResponseMessage response )
    {
        var jsonString = await response.Content.ReadAsStringAsync()
            .ConfigureAwait( false );

        try
        {
            var services = JsonDocument.Parse( jsonString )
                .RootElement
                .GetProperty( "nodes" )
                .EnumerateArray()
                .ElementAt( 0 )
                .GetProperty( "services" )
                .EnumerateArray()
                .Select( service => service.GetString() )
                .Where( service => service != null );

            return EnabledServices.All( enabledService => services.Any( service => service.StartsWith( enabledService.Identifier ) ) );
        }
        catch
        {
            return false;
        }
    }

    private sealed class SetupCredentialsRequest : HttpRequestMessage
    {
        public SetupCredentialsRequest()
            : base( HttpMethod.Post, "/settings/web" )
        {
            var content = new Dictionary<string, string>
            {
                { "username", CouchbaseBuilder.DefaultUsername },
                { "password", CouchbaseBuilder.DefaultPassword },
                { "port", "SAME" }
            };
            Content = new FormUrlEncodedContent( content );
        }
    }

    private sealed class CreateBucketRequest : HttpRequestMessage
    {
        public CreateBucketRequest( string name, bool flushEnabled = false, int quotaMiB = 100, int replicaNumber = 0 )
            : base( HttpMethod.Post, "/pools/default/buckets" )
        {
            var content = new Dictionary<string, string>
            {
                { "name", name },
                { "flushEnabled", flushEnabled ? "1" : "0" },
                { "ramQuota", quotaMiB.ToString() },
                { "replicaNumber", replicaNumber.ToString() }
            };
            Content = new FormUrlEncodedContent( content );
        }
    }


    private sealed class RenameNodeRequest : HttpRequestMessage
    {
        public RenameNodeRequest( CouchbaseContainer container )
            : base( HttpMethod.Post, "/node/controller/rename" )
        {
            var content = new Dictionary<string, string> { { "hostname", container.IpAddress } };
            Content = new FormUrlEncodedContent( content );
        }
    }

    private sealed class SetupNodeServicesRequest : HttpRequestMessage
    {
        public SetupNodeServicesRequest( params CouchbaseService[] enabledServices )
            : base( HttpMethod.Post, "/node/controller/setupServices" )
        {
            var content = new Dictionary<string, string> { { "services", string.Join( ",", enabledServices.Select( enabledService => enabledService.Identifier ) ) } };
            Content = new FormUrlEncodedContent( content );
        }
    }

    private sealed class SetupMemoryQuotasRequest : HttpRequestMessage
    {
        public SetupMemoryQuotasRequest( params CouchbaseService[] enabledServices )
            : base( HttpMethod.Post, "/pools/default" )
        {
            var content = new Dictionary<string, string>();

            foreach ( var enabledService in enabledServices )
            {
                if ( !enabledService.HasQuota )
                {
                    continue;
                }

                if ( CouchbaseService.Data.Equals( enabledService ) )
                {
                    content.Add( "memoryQuota", enabledService.MinimumQuotaMb.ToString() );
                }
                else
                {
                    content.Add( enabledService.Identifier + "MemoryQuota", enabledService.MinimumQuotaMb.ToString() );
                }
            }

            Content = new FormUrlEncodedContent( content );
        }
    }

    private sealed class SetupIndexRequest : HttpRequestMessage
    {
        public SetupIndexRequest( string logLevel = "info" )
            : base( HttpMethod.Post, "/settings/indexes" )
        {
            var content = new Dictionary<string, string>
            {
                { "logLevel", logLevel },
                { "storageMode", "forestdb" },
            };
            Content = new FormUrlEncodedContent( content );
        }
    }

    private sealed class RetryHandler : DelegatingHandler
    {
        private const int MaxRetries = 5;

        public RetryHandler()
            : base( new HttpClientHandler() )
        {
        }

        protected override async Task<HttpResponseMessage> SendAsync( HttpRequestMessage request, CancellationToken cancellationToken )
        {
            for ( var _ = 0; _ < MaxRetries; _++ )
            {
                try
                {
                    return await base.SendAsync( request, cancellationToken )
                        .ConfigureAwait( false );
                }
                catch ( HttpRequestException )
                {
                    await Task.Delay( TimeSpan.FromSeconds( 1 ), cancellationToken )
                        .ConfigureAwait( false );
                }
            }

            throw new HttpRequestException( $"Unable to configure Couchbase. The HTTP request '{request.RequestUri}' did not complete successfully." );
        }
    }
}


public readonly struct CouchbaseService
{
    /// <summary>
    /// Gets the Data service.
    /// </summary>
    public static readonly CouchbaseService Data = new( "kv", 256 );

    /// <summary>
    /// Gets the Index service.
    /// </summary>
    public static readonly CouchbaseService Index = new( "index", 256 );

    /// <summary>
    /// Gets the Query service.
    /// </summary>
    public static readonly CouchbaseService Query = new( "n1ql", 0 );

    /// <summary>
    /// Gets the Search service.
    /// </summary>
    public static readonly CouchbaseService Search = new( "fts", 256 );

    /// <summary>
    /// Gets the Analytics service.
    /// </summary>
    public static readonly CouchbaseService Analytics = new( "cbas", 1024 );

    /// <summary>
    /// Gets the Eventing service.
    /// </summary>
    public static readonly CouchbaseService Eventing = new( "eventing", 256 );

    /// <summary>
    /// Initializes a new instance of the <see cref="CouchbaseService" /> struct.
    /// </summary>
    /// <param name="identifier">The identifier.</param>
    /// <param name="minimumQuotaMb">The minimum quota in MB.</param>
    private CouchbaseService( string identifier, ushort minimumQuotaMb )
    {
        Identifier = identifier;
        MinimumQuotaMb = minimumQuotaMb;
    }

    /// <summary>
    /// Gets the identifier.
    /// </summary>
    public string Identifier { get; }

    /// <summary>
    /// Gets the minimum quota in MB.
    /// </summary>
    public ushort MinimumQuotaMb { get; }

    /// <summary>
    /// Gets a value indicating whether the service has a minimum quota or not.
    /// </summary>
    public bool HasQuota => MinimumQuotaMb > 0;
}
