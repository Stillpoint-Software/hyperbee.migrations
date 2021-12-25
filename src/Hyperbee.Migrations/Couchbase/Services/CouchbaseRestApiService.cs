using System;
using System.Linq;
using System.Net.Http;
using System.Text.Json.Nodes;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;
using Couchbase;
using Couchbase.Core.Exceptions;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hyperbee.Migrations.Couchbase.Services
{
    // https://docs.couchbase.com/server/current/rest-api/rest-endpoints-all.html
    //
    // rest-api service is used to get configuration information that is currently 
    // unavailable (or broken) through the `standard` net client sdk

    internal interface ICouchbaseRestApiService
    {
        Task<JsonNode> GetClusterInfoAsync( CancellationToken cancellationToken = default );
        Task<JsonNode> GetClusterDetailsAsync( CancellationToken cancellationToken = default );
        Task<JsonNode> GetBucketDetailsAsync( string bucketName, CancellationToken cancellationToken = default );
        Task WaitUntilManagementReadyAsync( TimeSpan timeout );
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

            var (host, _) = match.Groups["hosts"].Value.Split( ',' ) // taking the first one. this could be smarter/randomized.
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

            var node = JsonNode.Parse( responseBody );

            return node;
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

            var node = JsonNode.Parse( responseBody );

            return node; // nodes[].clusterMembership 'active', .status 'healthy'
        }

        public async Task WaitUntilManagementReadyAsync( TimeSpan timeout )
        {
            // we will `ping` the top-level uri

            var uri = GetUri( RestApi.GetClusterInfo() );

            using var tokenSource = new CancellationTokenSource();
            tokenSource.CancelAfter( timeout );
            var cancellationToken = tokenSource.Token;

            var count = 0;

            while ( true )
            {
                try
                {
                    cancellationToken.ThrowIfCancellationRequested();

                    if ( count++ > 0 )
                        await Task.Delay( 1000, cancellationToken ).ConfigureAwait( false ); // inside the try catch

                    var response = await Client.GetAsync( uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken )
                        .ConfigureAwait( false );

                    response.EnsureSuccessStatusCode();
                    return;
                }
                catch ( HttpRequestException ex )
                {
                    if ( ex.StatusCode != null )
                        throw;

                    // connection failure - site not ready - etc

                }
                catch ( OperationCanceledException ex )
                {
                    throw new UnambiguousTimeoutException( $"Timed out after {timeout}.", ex );
                }
                catch ( Exception ex )
                {
                    throw new CouchbaseException( "An error has occurred, see the inner exception for details.", ex );
                }
            }
        }
    }
}
