//#define INTEGRATIONS
#nullable enable
using Hyperbee.Migrations.Integration.Tests.Container.OpenSearch;
using Hyperbee.Migrations.Providers.OpenSearch;
using Hyperbee.Migrations.Providers.OpenSearch.Internal;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Bootstrap;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Bootstrap.Steps;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hyperbee.Migrations.Integration.Tests;

#if INTEGRATIONS
// R-21 #3 — ISM endpoint capability detection against a live cluster.
//
// The Testcontainers image (opensearchproject/opensearch:2.18.0) exposes
// the modern `/_plugins/_ism` surface, so the step must resolve to
// ModernPrefix here. Older AWS Managed domains (1.x and earlier) expose
// the legacy `/_opendistro/_ism` surface; that path is exercised by the
// AWS Managed scheduled validation runbook (R-28c), not by single-node
// CI.

[TestClass]
public class OpenSearchIsmEndpointDetectIntegrationTests
{
    [TestMethod]
    [TestCategory( "OpenSearch" )]
    [TestCategory( "R-21" )]
    public async Task IsmEndpointDetectStep_OpenSearch218_ResolvesToModernPrefix()
    {
        var capability = new IsmEndpointCapability();
        var step = new IsmEndpointDetectStep( capability );

        var context = new BootstrapContext
        {
            Client = OpenSearchTestContainer.Client,
            Options = new OpenSearchMigrationOptions(),
            TimeProvider = TimeProvider.System,
            LoggerFactory = NullLoggerFactory.Instance,
            CancellationToken = default
        };

        var outcome = await step.ExecuteAsync( context );

        Assert.AreEqual( "ism-detect", outcome.Name );
        Assert.AreEqual( StepStatus.Succeeded, outcome.Status,
            $"detect step should succeed against OpenSearch 2.18; failed: {outcome.Detail}" );
        Assert.IsTrue( capability.IsResolved );
        Assert.AreEqual( IsmEndpointDetectStep.ModernPrefix, capability.IsmPathPrefix,
            "OpenSearch 2.18.0 exposes the modern /_plugins/_ism surface" );
    }
}
#endif
