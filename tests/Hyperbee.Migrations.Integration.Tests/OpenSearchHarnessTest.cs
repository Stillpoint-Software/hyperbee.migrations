//#define INTEGRATIONS
using Hyperbee.Migrations.Integration.Tests.Container.OpenSearch;
using OpenSearch.Net;

namespace Hyperbee.Migrations.Integration.Tests;

#if INTEGRATIONS
[TestClass]
// Gating (ADR-0031): shared assembly-fixture container, no Docker image build,
// not multi-node. Runs on every PR.
[TestCategory( "Gating" )]
public class OpenSearchHarnessTest
{
    private OpenSearch.Client.IOpenSearchClient Client;
    private OpenSearchLowLevelClient LowLevel;

    [TestInitialize]
    public void Setup()
    {
        Client = OpenSearchTestContainer.Client;
        LowLevel = OpenSearchTestContainer.LowLevelClient;
    }

    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "Smoke" )]
    public async Task HelloWorld_ClusterHealthYellowOrGreen()
    {
        // Hello-world smoke test (Plan Task 0.5):
        // verify Testcontainers harness boots OpenSearch and the cluster reaches a usable state.
        // This proves only that the harness works — real provider correctness is exercised by
        // the AST/grammar/middleware spike tests in Tasks 0.5/0.6 (per the kill criterion).

        var health = await LowLevel.Cluster.HealthAsync<StringResponse>();

        Assert.IsTrue( health.Success, $"Cluster health request failed: {health.OriginalException?.Message}" );

        var body = health.Body;
        Assert.IsNotNull( body, "Cluster health response body was null." );
        Assert.IsTrue(
            body.Contains( "\"status\":\"yellow\"" ) || body.Contains( "\"status\":\"green\"" ),
            $"Cluster status was not yellow or green: {body}" );
    }
}
#endif
