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
    // rest-api service used to get configuration information that is currently 
    // unavailable through the `standard` net client sdk

    public interface ICouchbaseRestApiService
    {
        Task<JsonNode> GetClusterInfoAsync( CancellationToken cancellationToken = default );
        Task<JsonNode> GetClusterDetailsAsync( CancellationToken cancellationToken = default );
    }

    internal class CouchbaseRestApiService : ICouchbaseRestApiService
    {
        public HttpClient Client { get; }

        public CouchbaseRestApiService( HttpClient httpClient, IOptions<ClusterOptions> options, ILogger<CouchbaseRestApiService> logger )
        {
            Client = httpClient;
            httpClient.BaseAddress = GetAdminUri( options.Value );
        }

        private static Uri GetAdminUri( ClusterOptions options )
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

            var (host, _) = match.Groups["hosts"].Value.Split( ',' )
                .Select( value => HostEndpoint.Parse( value.Trim() ) )
                .FirstOrDefault();

            var port = options.EnableTls.GetValueOrDefault( false )
                ? options.BootstrapHttpPortTls // expected 18091
                : options.BootstrapHttpPort; // expected 8091

            return new Uri( $"{scheme}://{host}:{port}" );
        }

        public async Task<JsonNode> GetClusterInfoAsync( CancellationToken cancellationToken = default )
        {
            // retrieve cluster information
            var uri = new Uri( "pools", UriKind.Relative );

            var response = await Client.GetAsync( uri, cancellationToken ).ConfigureAwait( false );
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStreamAsync( cancellationToken ).ConfigureAwait( false );
            var node = JsonNode.Parse( responseBody );

            return node;
        }

        public async Task<JsonNode> GetClusterDetailsAsync( CancellationToken cancellationToken = default )
        {
            // retrieve cluster details
            var uri = new Uri( "pools/default", UriKind.Relative );

            var response = await Client.GetAsync( uri, cancellationToken ).ConfigureAwait( false );
            response.EnsureSuccessStatusCode();

            var responseBody = await response.Content.ReadAsStreamAsync( cancellationToken ).ConfigureAwait( false );
            var node = JsonNode.Parse( responseBody );

            return node;
        }
    }
}
