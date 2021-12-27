using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Couchbase;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hyperbee.Migrations.Couchbase.Services
{
    // https://docs.couchbase.com/server/current/rest-api/rest-endpoints-all.html
    //
    // rest-api service is used to get configuration information that is currently 
    // unavailable through the `standard` net client sdk

    internal interface ICouchbaseRestApiService
    {
        Task<bool> ClusterHealthyAsync( CancellationToken cancellationToken = default );
        Task<JsonNode> GetClusterInfoAsync( CancellationToken cancellationToken = default );
        Task<JsonNode> GetClusterDetailsAsync( CancellationToken cancellationToken = default );

        Task<bool> BucketHealthyAsync( string bucketName, CancellationToken cancellationToken = default );
        Task<JsonNode> GetBucketDetailsAsync( string bucketName, CancellationToken cancellationToken = default );

        Task<bool> ManagementReadyAsync( CancellationToken cancellationToken = default );
    }

    internal class CouchbaseRestApiService : ICouchbaseRestApiService
    {
        public HttpClient Client { get; }

        public static class RestApi
        {
            public static string GetClusterInfo() => "pools";
            public static string GetClusterDetails() => "pools/default";
            public static string GetBucketDetails( string bucketName ) => $"pools/default/buckets/{bucketName}";
        }

        public CouchbaseRestApiService( HttpClient httpClient, IOptions<ClusterOptions> options, ILogger<CouchbaseRestApiService> logger )
        {
             
            Client = httpClient;
            httpClient.BaseAddress = GetManagementUri( options.Value );
        }

        private static Uri GetManagementUri( ClusterOptions options )
        {
            var connectionStringRegex = new Regex(
                "^((?<scheme>[^://]+)://)?((?<username>[^\n@]+)@)?(?<hosts>[^\n?]+)?(\\?(?<params>(.+)))?",
                RegexOptions.CultureInvariant
            );

            var match = connectionStringRegex.Match( options.ConnectionString! );

            if ( !match.Success )
                throw new ArgumentException( "Invalid couchbase connection string." );

            if ( !match.Groups["hosts"].Success )
                throw new ArgumentException( "No hosts in couchbase connection string." );

            var scheme = options.EnableTls.GetValueOrDefault( false )
                ? "https"
                : "http";

            var (host, _) = match.Groups["hosts"].Value.Split( ',' ) // taking the first host. this could be smarter/randomized.
                .Select( value => HostEndpoint.Parse( value.Trim() ) )
                .FirstOrDefault();

            var port = options.EnableTls.GetValueOrDefault( false )
                ? options.BootstrapHttpPortTls // expected 18091
                : options.BootstrapHttpPort; // expected 8091

            return new Uri( $"{scheme}://{host}:{port}" );
        }

        private static Uri GetUri( string path )
        {
            return new Uri( path, UriKind.Relative );
        }

        public async Task<bool> ClusterHealthyAsync( CancellationToken cancellationToken = default )
        {
            var result = await GetClusterDetailsAsync( cancellationToken ).ConfigureAwait( false );
            return NodesAreHealthy( result );
        }

        public async Task<JsonNode> GetClusterInfoAsync( CancellationToken cancellationToken = default )
        {
            // retrieve cluster information
            var uri = GetUri( RestApi.GetClusterInfo() );

            var response = await Client.GetAsync( uri, cancellationToken )
                .ConfigureAwait( false );

            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStreamAsync( cancellationToken )
                .ConfigureAwait( false );

            var node = JsonNode.Parse( responseBody );

            return node;
        }

        public async Task<JsonNode> GetClusterDetailsAsync( CancellationToken cancellationToken = default )
        {
            // retrieve cluster details
            var uri = GetUri( RestApi.GetClusterDetails() );

            var response = await Client.GetAsync( uri, cancellationToken )
                .ConfigureAwait( false );

            response.EnsureSuccessStatusCode();
            
            var responseBody = await response.Content.ReadAsStreamAsync( cancellationToken )
                .ConfigureAwait( false );

            return JsonNode.Parse( responseBody );
        }

        public async Task<bool> BucketHealthyAsync( string bucketName, CancellationToken cancellationToken = default )
        {
            var result = await GetBucketDetailsAsync( bucketName, cancellationToken ).ConfigureAwait( false );
            return NodesAreHealthy( result );
        }

        public async Task<JsonNode> GetBucketDetailsAsync( string bucketName, CancellationToken cancellationToken = default )
        {
            // retrieve bucket details
            var uri = GetUri( RestApi.GetBucketDetails( bucketName ) );

            var response = await Client.GetAsync( uri, cancellationToken )
                .ConfigureAwait( false );

            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStreamAsync( cancellationToken )
                .ConfigureAwait( false );

            return JsonNode.Parse( responseBody );
        }

        public async Task<bool> ManagementReadyAsync( CancellationToken cancellationToken = default )
        {
            // `ping` by calling the top-level uri

            var uri = GetUri( RestApi.GetClusterInfo() );

            try
            {
                var response = await Client.GetAsync( uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken )
                    .ConfigureAwait( false );

                response.EnsureSuccessStatusCode();
                return true;
            }
            catch ( HttpRequestException ex )
            {
                if ( ex.StatusCode != null )
                    throw;

                // connection failure - site not ready - etc
            }
 
            return false;
        }

        private static bool NodesAreHealthy( JsonNode result )
        {
            var status = result!["nodes"]!.AsArray()
                .Where( x => x["clusterMembership"]?.ToString() == "active" )
                .Select( x => x["status"]?.ToString() )
                .Where( x => x != null )
                .ToList();

            return status.All( x => x == "healthy" ); // states: warmup, healthy, ??
        }
    }
}
