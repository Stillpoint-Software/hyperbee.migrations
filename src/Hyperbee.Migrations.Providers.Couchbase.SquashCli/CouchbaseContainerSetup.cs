using System.Net.Http.Headers;
using System.Text;
using Testcontainers.Couchbase;

namespace Hyperbee.Migrations.Providers.Couchbase.SquashCli;

/// <summary>
/// Post-start configuration helpers for a Testcontainers <see cref="CouchbaseContainer"/>.
/// The library's default startup callback (registered in CouchbaseBuilder.Init)
/// fully provisions the cluster: enables kv/index/n1ql/fts services, sets
/// alternate ("external") addresses on the host-mapped ports, creates the
/// default bucket, and locks Administrator credentials. Two extra steps are
/// still required for squash codegen + ledger-bootstrap flows:
/// <list type="bullet">
///   <item>GSI indexer storage mode (<c>forestdb</c> on Community Edition).
///         Required before any <c>CREATE INDEX</c> call. Without this the
///         SDK's <c>CreatePrimaryIndexAsync</c> throws
///         <c>InternalServerFailureException 5000</c>
///         "Please Set Indexer Storage Mode Before Create Index".</item>
///   <item>Cluster data-service RAM quota bumped above the library
///         default (<c>Data.MinimumQuotaMb</c> = 256 MB), so the test
///         buckets fit with headroom. We POST <c>memoryQuota=1024</c>
///         to <c>/pools/default</c>.</item>
/// </list>
/// These cannot be wired via <c>WithStartupCallback</c> because that
/// REPLACES (not appends to) the library's default callback; calling it
/// would skip <c>setupServices</c>/credentials/default-bucket. Instead
/// callers invoke <see cref="PostStartConfigureAsync"/> after
/// <c>container.StartAsync</c> returns.
/// </summary>
public static class CouchbaseContainerSetup
{
    /// <summary>
    /// Apply the post-start tweaks the library default callback skips
    /// (memory quota bump + indexer storage mode). Run AFTER
    /// <c>container.StartAsync</c> returns; the library default has
    /// already provisioned services + credentials by then.
    /// </summary>
    public static async Task PostStartConfigureAsync(
        DotNet.Testcontainers.Containers.IContainer container,
        int mgmtPort,
        int memoryQuotaMb = 1024,
        CancellationToken cancellationToken = default )
    {
        await ConfigureClusterMemoryAsync( container, mgmtPort, memoryQuotaMb, cancellationToken ).ConfigureAwait( false );
        await ConfigureIndexerStorageModeAsync( container, mgmtPort, cancellationToken ).ConfigureAwait( false );
    }

    private static async Task ConfigureClusterMemoryAsync(
        DotNet.Testcontainers.Containers.IContainer container, int mgmtPort, int memoryQuotaMb, CancellationToken cancellationToken )
    {
        using var http = BuildClient( container, mgmtPort );

        const int maxAttempts = 30;
        for ( var attempt = 1; ; attempt++ )
        {
            var form = new FormUrlEncodedContent( new[]
            {
                new KeyValuePair<string, string>( "memoryQuota", memoryQuotaMb.ToString( System.Globalization.CultureInfo.InvariantCulture ) )
            } );
            using var request = new HttpRequestMessage( HttpMethod.Post, "/pools/default" ) { Content = form };
            try
            {
                using var response = await http.SendAsync( request, cancellationToken ).ConfigureAwait( false );
                if ( response.IsSuccessStatusCode )
                    return;

                if ( attempt >= maxAttempts )
                {
                    var body = await response.Content.ReadAsStringAsync( cancellationToken ).ConfigureAwait( false );
                    throw new InvalidOperationException(
                        $"POST /pools/default (memoryQuota={memoryQuotaMb}) failed after {maxAttempts} attempts: HTTP {(int) response.StatusCode}. Body: {body}" );
                }
            }
            catch ( HttpRequestException ) when ( attempt < maxAttempts )
            {
                // transient
            }
            await Task.Delay( TimeSpan.FromMilliseconds( 500 ), cancellationToken ).ConfigureAwait( false );
        }
    }

    private static async Task ConfigureIndexerStorageModeAsync(
        DotNet.Testcontainers.Containers.IContainer container, int mgmtPort, CancellationToken cancellationToken )
    {
        using var http = BuildClient( container, mgmtPort );

        const int maxAttempts = 30;
        for ( var attempt = 1; ; attempt++ )
        {
            var form = new FormUrlEncodedContent( new[]
            {
                new KeyValuePair<string, string>( "storageMode", "forestdb" ),
                new KeyValuePair<string, string>( "logLevel", "info" )
            } );
            using var request = new HttpRequestMessage( HttpMethod.Post, "/settings/indexes" ) { Content = form };
            try
            {
                using var response = await http.SendAsync( request, cancellationToken ).ConfigureAwait( false );
                if ( response.IsSuccessStatusCode )
                    return;

                if ( attempt >= maxAttempts )
                {
                    var body = await response.Content.ReadAsStringAsync( cancellationToken ).ConfigureAwait( false );
                    throw new InvalidOperationException(
                        $"POST /settings/indexes failed after {maxAttempts} attempts: HTTP {(int) response.StatusCode}. Body: {body}" );
                }
            }
            catch ( HttpRequestException ) when ( attempt < maxAttempts )
            {
                // transient
            }
            await Task.Delay( TimeSpan.FromMilliseconds( 500 ), cancellationToken ).ConfigureAwait( false );
        }
    }

    private static HttpClient BuildClient(
        DotNet.Testcontainers.Containers.IContainer container, int mgmtPort )
    {
        var http = new HttpClient
        {
            BaseAddress = new Uri( $"http://{container.Hostname}:{mgmtPort}" )
        };
        http.DefaultRequestHeaders.Authorization = new AuthenticationHeaderValue(
            "Basic",
            Convert.ToBase64String( Encoding.ASCII.GetBytes(
                $"{CouchbaseBuilder.DefaultUsername}:{CouchbaseBuilder.DefaultPassword}" ) ) );
        return http;
    }
}
