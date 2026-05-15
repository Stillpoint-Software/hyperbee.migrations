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

        // QueryServiceReadyAsync pings the n1ql /admin/ping endpoint on
        // whichever node serves the query service per the cluster map.
        // The query process can be slow to start even after the cluster
        // map registers it; pinging it directly is the most reliable
        // signal that planner + REST endpoint are both live.
        Task<bool> QueryServiceReadyAsync( CancellationToken cancellationToken = default );

        // ClusterIdleAsync returns true when no cluster task is running
        // (no rebalance, compaction, recovery, or xdcr task in flight).
        // Couchbase rejects CREATE INDEX / ALTER REPLICA while a rebalance
        // is in progress; new buckets briefly trigger a post-creation
        // rebalance even on a single-node cluster.
        Task<bool> ClusterIdleAsync( CancellationToken cancellationToken = default );
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

        public async Task<bool> QueryServiceReadyAsync( CancellationToken cancellationToken = default )
        {
            // Resolve the query service host:port from the cluster map.
            // Honor alt-addresses when the cluster advertises them so we
            // ping the host-reachable port, not the internal Docker IP.
            try
            {
                var nodeServices = await FetchNodeServicesAsync( cancellationToken ).ConfigureAwait( false );
                var nodesExt = nodeServices?["nodesExt"]?.AsArray();
                if ( nodesExt == null || nodesExt.Count == 0 )
                    return false;

                foreach ( var node in nodesExt )
                {
                    var alt = node?["alternateAddresses"]?["external"];
                    string host;
                    int? n1qlPort;
                    if ( alt != null && alt["ports"]?["n1ql"] != null )
                    {
                        host = alt["hostname"]?.ToString() ?? ConnectionStringUris.First().Host;
                        n1qlPort = (int) alt["ports"]["n1ql"];
                    }
                    else
                    {
                        var services = node?["services"]?.AsObject();
                        if ( services?["n1ql"] == null )
                            continue;
                        host = node?["hostname"]?.ToString() ?? ConnectionStringUris.First().Host;
                        n1qlPort = (int) services["n1ql"];
                    }

                    var uri = new Uri( $"http://{host}:{n1qlPort.Value}/admin/ping" );
                    using var response = await Client.GetAsync( uri, HttpCompletionOption.ResponseHeadersRead, cancellationToken ).ConfigureAwait( false );
                    return response.IsSuccessStatusCode;
                }
                return false;
            }
            catch ( HttpRequestException )
            {
                return false;
            }
        }

        public async Task<bool> ClusterIdleAsync( CancellationToken cancellationToken = default )
        {
            // /pools/default/tasks returns an array of cluster tasks. A
            // task with status "running" (rebalance, compaction, etc.)
            // blocks GSI CREATE INDEX with "rebalance in progress".
            // Idle: every task has status != running (or array is empty).
            try
            {
                var uri = GetUri( "pools/default/tasks" );
                using var response = await Client.GetAsync( uri, cancellationToken ).ConfigureAwait( false );
                if ( !response.IsSuccessStatusCode )
                    return false;
                var body = await response.Content.ReadAsStreamAsync( cancellationToken ).ConfigureAwait( false );
                var tasks = JsonNode.Parse( body )?.AsArray();
                if ( tasks == null )
                    return true;
                foreach ( var t in tasks )
                {
                    if ( t?["status"]?.ToString() == "running" )
                        return false;
                }
                return true;
            }
            catch ( HttpRequestException )
            {
                return false;
            }
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
