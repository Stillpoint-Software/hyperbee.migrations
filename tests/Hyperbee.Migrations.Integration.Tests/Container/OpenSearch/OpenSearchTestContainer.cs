using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using OpenSearch.Client;
using OpenSearch.Net;

namespace Hyperbee.Migrations.Integration.Tests.Container.OpenSearch;

// OpenSearch image is pinned by tag here. Per the plan amendment (PM-11 / A11), image
// bumps must be explicit PR-level decisions. To pin by sha256 digest in CI, replace the
// tag in WithImage() with the digest form: "opensearchproject/opensearch@sha256:<digest>".
//
// Version-support contract (R-15a, NF-6):
//   - Tested version: 2.18.0 (this digest)
//   - Minimum supported OpenSearch version: 2.0.0 (composable index templates baseline)
//   - AWS Managed OpenSearch caveat: AWS trails OSS by version; ISM endpoint may differ
//     between newer (`_plugins/_ism`) and older (`_opendistro/_ism`) AWS domains. The
//     provider's bootstrap probes for the active path (R-21).

public class OpenSearchTestContainer
{
    private const string ImageTag = "opensearchproject/opensearch:2.18.0";
    private const int OpenSearchPort = 9200;
    private const string AdminPassword = "Hyperbee.Migrations.Test#2026";

    public static IOpenSearchClient Client { get; set; }
    public static OpenSearchLowLevelClient LowLevelClient { get; set; }
    public static INetwork Network { get; set; }
    public static string Host { get; set; }
    public static int Port { get; set; }
    public static Uri Endpoint { get; set; }

    public static async Task Initialize( TestContext context )
    {
        var cancellationToken = context.CancellationTokenSource.Token;

        var network = new NetworkBuilder()
            .WithName( Guid.NewGuid().ToString( "D" ) )
            .WithCleanUp( true )
            .Build();

        await network.CreateAsync( cancellationToken )
            .ConfigureAwait( false );

        // Single-node mode disables the security plugin's complexity for tests but still
        // requires the initial admin password (OpenSearch 2.12+). HTTP/SSL is disabled to
        // simplify test client construction; production deploys use TLS.

        var openSearchContainer = new ContainerBuilder()
            .WithImage( ImageTag )
            .WithNetwork( network )
            .WithNetworkAliases( "opensearch" )
            .WithPortBinding( OpenSearchPort, true )
            .WithEnvironment( "discovery.type", "single-node" )
            .WithEnvironment( "OPENSEARCH_INITIAL_ADMIN_PASSWORD", AdminPassword )
            .WithEnvironment( "DISABLE_SECURITY_PLUGIN", "true" )
            .WithEnvironment( "DISABLE_INSTALL_DEMO_CONFIG", "true" )
            .WithEnvironment( "bootstrap.memory_lock", "false" )
            .WithEnvironment( "OPENSEARCH_JAVA_OPTS", "-Xms512m -Xmx512m" )
            .WithCleanUp( true )
            .WithWaitStrategy(
                DotNet.Testcontainers.Builders.Wait.ForUnixContainer()
                    .UntilHttpRequestIsSucceeded( request =>
                        request
                            .ForPath( "/_cluster/health" )
                            .ForPort( OpenSearchPort )
                            .ForStatusCode( System.Net.HttpStatusCode.OK ) ) )
            .Build();

        await openSearchContainer.StartAsync( cancellationToken )
            .ConfigureAwait( false );

        Host = openSearchContainer.Hostname;
        Port = openSearchContainer.GetMappedPublicPort( OpenSearchPort );
        Endpoint = new UriBuilder( "http", Host, Port ).Uri;

        var connectionSettings = new ConnectionSettings( Endpoint )
            .DisableDirectStreaming() // capture request bodies for spike-test wire assertions
            .ThrowExceptions();

        Client = new OpenSearchClient( connectionSettings );
        LowLevelClient = new OpenSearchLowLevelClient( connectionSettings );
        Network = network;
    }
}
