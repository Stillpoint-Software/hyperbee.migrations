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

        // BucketServicesReadyAsync hits the terse per-bucket config
        // (/pools/default/b/<bucket>) the SDK uses to build its
        // cluster topology. Returns true once kv + n1ql appear in
        // nodesExt[].services and external alt-addresses are
        // advertised. BucketHealthyAsync flips first (nodes go
        // status=healthy as soon as the manager finishes provisioning)
        // but the per-bucket service propagation lags; opening
        // BucketAsync before this returns true throws
        // SocketNotAvailableException on the KV socket.
        Task<bool> BucketServicesReadyAsync( string bucketName, CancellationToken cancellationToken = default );

        Task<JsonNode> GetBucketDetailsAsync( string bucketName, CancellationToken cancellationToken = default );
        Task<JsonNode> GetNodeStatusesAsync( CancellationToken cancellationToken = default );

        Task<bool> ManagementReadyAsync( CancellationToken cancellationToken = default );
    }

    public class CouchbaseRestApiService : ICouchbaseRestApiService
    {
        public HttpClient Client { get; }
        public ILogger<CouchbaseRestApiService> Logger { get; }
        private IList<Uri> ConnectionStringUris { get; set; } = new List<Uri>();

        public static class RestApi
        {
            public static string GetClusterInfo() => "pools";
            public static string GetClusterDetails() => "pools/default";
            public static string GetBucketDetails( string bucketName ) => $"pools/default/buckets/{bucketName}";
            public static string GetBucketTerseConfig( string bucketName ) => $"pools/default/b/{bucketName}";
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

            // REST always uses BootstrapHttpPort (8091 default) /
            // BootstrapHttpPortTls (18091 default). The Couchbase SDK
            // connection-string convention puts the KV port (11210)
            // in `couchbase://host:port` URIs because the SDK speaks
            // binary KV protocol there during bootstrap. The REST API
            // lives on the management HTTP port entirely separately;
            // using the URI's port for REST calls produces "response
            // ended prematurely" when the URI carries the KV port
            // (mapped Testcontainers ports, Capella connection strings,
            // etc.) because we send HTTP to a binary protocol port.
            var httpPort = options.EnableTls.GetValueOrDefault( false )
                ? options.BootstrapHttpPortTls // expected 18091
                : options.BootstrapHttpPort; // expected 8091

            ConnectionStringUris = match.Groups["hosts"].Value.Split( ',' )
                .Select( value =>
                {
                    var (host, _) = HostEndpoint.Parse( value.Trim() );
                    return new Uri( $"{scheme}://{host}:{httpPort}" );
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

        public async Task<bool> BucketServicesReadyAsync( string bucketName, CancellationToken cancellationToken = default )
        {
            // The terse per-bucket config (/pools/default/b/<bucket>) is
            // what the SDK consumes through its cluster-map machinery.
            // It exposes a "nodesExt" array where each node carries a
            // "services" object naming the services it runs and an
            // "alternateAddresses.external" block when alt-addresses are
            // configured at the node level. The bucket is ready for SDK
            // consumption when both KV and N1QL appear on the active
            // node AND -- if the node advertises alt-addresses anywhere
            // in the cluster -- the bucket's terse config also exposes
            // the alt-address kv + n1ql ports. Returning ready before
            // the bucket inherits alt-addresses causes the SDK to fall
            // back to internal Docker IPs and throw
            // SocketNotAvailableException on KV connection setup.
            var nodeServicesJson = await FetchNodeServicesAsync( cancellationToken ).ConfigureAwait( false );
            var clusterHasAltAddresses = NodeHasAlternateAddresses( nodeServicesJson );

            var uri = GetUri( RestApi.GetBucketTerseConfig( bucketName ) );

            using var response = await Client.GetAsync( uri, cancellationToken ).ConfigureAwait( false );
            if ( !response.IsSuccessStatusCode )
                return false;

            var responseBody = await response.Content.ReadAsStreamAsync( cancellationToken ).ConfigureAwait( false );
            var json = JsonNode.Parse( responseBody );
            var nodesExt = json?["nodesExt"]?.AsArray();
            if ( nodesExt == null || nodesExt.Count == 0 )
                return false;

            foreach ( var node in nodesExt )
            {
                var services = node?["services"]?.AsObject();
                if ( services == null )
                    continue;
                if ( services["kv"] == null || services["n1ql"] == null )
                    continue;
                var altExternal = node?["alternateAddresses"]?["external"];
                if ( clusterHasAltAddresses )
                {
                    if ( altExternal == null )
                        return false;
                    if ( altExternal["ports"]?["kv"] == null || altExternal["ports"]?["n1ql"] == null )
                        return false;
                }
                return true;
            }

            return false;
        }

        private async Task<JsonNode> FetchNodeServicesAsync( CancellationToken cancellationToken )
        {
            try
            {
                var uri = GetUri( "pools/default/nodeServices" );
                using var response = await Client.GetAsync( uri, cancellationToken ).ConfigureAwait( false );
                if ( !response.IsSuccessStatusCode )
                    return null;
                var body = await response.Content.ReadAsStreamAsync( cancellationToken ).ConfigureAwait( false );
                return JsonNode.Parse( body );
            }
            catch
            {
                return null;
            }
        }

        private static bool NodeHasAlternateAddresses( JsonNode nodeServices )
        {
            var nodesExt = nodeServices?["nodesExt"]?.AsArray();
            if ( nodesExt == null )
                return false;
            foreach ( var node in nodesExt )
            {
                if ( node?["alternateAddresses"]?["external"] != null )
                    return true;
            }
            return false;
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
