using System;
using System.Collections.Generic;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Couchbase;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hyperbee.Migrations.Providers.Couchbase.Services
{
    // https://docs.couchbase.com/server/current/rest-api/rest-endpoints-all.html
    //
    // rest-api service is used to get configuration information that is currently 
    // unavailable through the `standard` net client sdk

    public interface ICouchbaseRestApiService
    {
        Task<bool> ClusterHealthyAsync( CancellationToken cancellationToken = default );
        Task<JsonNode> GetClusterInfoAsync( CancellationToken cancellationToken = default );
        Task<JsonNode> GetClusterDetailsAsync( CancellationToken cancellationToken = default );

        Task<bool> BucketHealthyAsync( string bucketName, CancellationToken cancellationToken = default );
        Task<JsonNode> GetBucketDetailsAsync( string bucketName, CancellationToken cancellationToken = default );
        Task<JsonNode> GetNodeStatusesAsync( CancellationToken cancellationToken = default );

        Task<bool> ManagementReadyAsync( CancellationToken cancellationToken = default );
    }

    internal class CouchbaseRestApiService : ICouchbaseRestApiService
    {
        public HttpClient Client { get; }
        public ILogger<CouchbaseRestApiService> Logger { get; }
        private IList<Uri> ConnectionStringUris { get; set; } = new List<Uri>();

        public static class RestApi
        {
            public static string GetClusterInfo() => "pools";
            public static string GetClusterDetails() => "pools/default";
            public static string GetBucketDetails( string bucketName ) => $"pools/default/buckets/{bucketName}";
            public static string GetNodeStatuses() => "nodeStatuses";

            // uris of interest
            // http://localhost:8091/pools/default/nodeServices lists services and ports
        }

        public CouchbaseRestApiService( HttpClient httpClient, IOptions<ClusterOptions> options, ILogger<CouchbaseRestApiService> logger )
        {
            Client = httpClient;
            Logger = logger;
            GetConnectionStringUris( options.Value );
        }

        private void GetConnectionStringUris( ClusterOptions options )
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

            var defaultPort = options.EnableTls.GetValueOrDefault( false )
                ? options.BootstrapHttpPortTls // expected 18091
                : options.BootstrapHttpPort; // expected 8091

            ConnectionStringUris = match.Groups["hosts"].Value.Split( ',' ) 
                .Select( value =>
                {
                    var (host, port) = HostEndpoint.Parse( value.Trim() );
                    return new Uri( $"{scheme}://{host}:{port.GetValueOrDefault(defaultPort)}" );
                } )
                .ToList();
        }

        private Uri GetUri( string path )
        {
            var baseUri = ConnectionStringUris.First();
            var relativeUri = new Uri( path, UriKind.Relative );

            return new Uri( baseUri, relativeUri );
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

        public async Task<JsonNode> GetNodeStatusesAsync( CancellationToken cancellationToken = default )
        {
            // retrieve node statuses
            var uri = GetUri( RestApi.GetNodeStatuses() );

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

                Logger.LogWarning( "{message}", ex.Message );

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
