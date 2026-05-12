//#define INTEGRATIONS
using Hyperbee.Migrations;
using Hyperbee.Migrations.Integration.Tests.Container.OpenSearch;
using Hyperbee.Migrations.Providers.OpenSearch.Squash;
using Hyperbee.Migrations.Squash;
using OpenSearch.Client;

namespace Hyperbee.Migrations.Integration.Tests;

#if INTEGRATIONS

// Phase 2 (R-P5): OpenSearch squash codegen determinism gate (C12).
//
// Contract: given identical cluster state, two GenerateAsync calls must
// produce byte-equal SquashGenerationResult.Generated.Content. This
// guarantees the canonicalizer fully removes ephemeral fields (creation_date,
// uuid, version, policy_version, last_updated_time, seq_no, primary_term)
// and that JSON key orderings are sorted away.
//
// These tests exercise the FULL strategy pipeline -- topology capture
// (cluster + plugin matrix + ISM endpoint) + REST-probe snapshot capture +
// canonicalize + emit -- against a live OpenSearch Testcontainers fixture.
// Tests are guarded by `#if INTEGRATIONS`; run locally with
// /p:EnableIntegrationTests=true.

[TestClass]
[DoNotParallelize]
public class OpenSearchSquashDeterminismTests
{
    private IOpenSearchClient _client;
    private const string TestIndexPrefix = "detgate_";

    [TestInitialize]
    public void Setup()
    {
        _client = OpenSearchTestContainer.Client;
        Assert.IsNotNull( _client, "OpenSearchTestContainer must initialize before this test class runs." );
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        // Delete any test-prefixed indices + templates so subsequent tests
        // start from a clean structural state.
        try { await _client.Indices.DeleteAsync( $"{TestIndexPrefix}*" ); } catch { }
        try { await _client.LowLevel.DoRequestAsync<OpenSearch.Net.StringResponse>(
            OpenSearch.Net.HttpMethod.DELETE,
            $"/_index_template/{TestIndexPrefix}*",
            CancellationToken.None ); } catch { }
        try { await _client.LowLevel.DoRequestAsync<OpenSearch.Net.StringResponse>(
            OpenSearch.Net.HttpMethod.DELETE,
            $"/_component_template/{TestIndexPrefix}*",
            CancellationToken.None ); } catch { }
    }

    [TestMethod]
    public async Task EmptyCluster_TwoRuns_ProduceByteEqualContent()
    {
        var content1 = await GenerateOnceAsync();
        var content2 = await GenerateOnceAsync();

        Assert.AreEqual( content1, content2,
            "Two GenerateAsync runs against an unchanged cluster must produce byte-equal output." );
    }

    [TestMethod]
    public async Task PopulatedCluster_TwoRuns_ProduceByteEqualContent()
    {
        // Create structural state directly via the client.
        await _client.Indices.CreateAsync( $"{TestIndexPrefix}users_v1",
            i => i.Map( m => m.Properties( p => p.Text( t => t.Name( "email" ) ) ) ) );
        await _client.Indices.CreateAsync( $"{TestIndexPrefix}users_v2",
            i => i.Map( m => m.Properties( p => p.Text( t => t.Name( "email" ) )
                                                   .Keyword( k => k.Name( "tenant_id" ) ) ) ) );

        var content1 = await GenerateOnceAsync();
        var content2 = await GenerateOnceAsync();

        Assert.AreEqual( content1, content2,
            "Two GenerateAsync runs against the same populated state must produce byte-equal output." );

        // Defense-in-depth: ensure the populated content actually contains
        // the structural objects we created (so we're testing real data, not
        // an empty-cluster pass-through).
        StringAssert.Contains( content1, $"{TestIndexPrefix}users_v1" );
        StringAssert.Contains( content1, $"{TestIndexPrefix}users_v2" );
    }

    [TestMethod]
    public async Task IndexCreationOrder_DoesNotAffectCanonicalOutput()
    {
        // Create indices in one order, capture, then delete and recreate in
        // the reverse order; capture again. Canonicalizer's recursive ordinal
        // sort must make the two outputs byte-equal regardless of creation
        // sequence.
        await _client.Indices.CreateAsync( $"{TestIndexPrefix}zindex",
            i => i.Map( m => m.Properties( p => p.Text( t => t.Name( "z_field" ) ) ) ) );
        await _client.Indices.CreateAsync( $"{TestIndexPrefix}aindex",
            i => i.Map( m => m.Properties( p => p.Text( t => t.Name( "a_field" ) ) ) ) );

        var firstOrder = await GenerateOnceAsync();

        await _client.Indices.DeleteAsync( $"{TestIndexPrefix}zindex" );
        await _client.Indices.DeleteAsync( $"{TestIndexPrefix}aindex" );

        await _client.Indices.CreateAsync( $"{TestIndexPrefix}aindex",
            i => i.Map( m => m.Properties( p => p.Text( t => t.Name( "a_field" ) ) ) ) );
        await _client.Indices.CreateAsync( $"{TestIndexPrefix}zindex",
            i => i.Map( m => m.Properties( p => p.Text( t => t.Name( "z_field" ) ) ) ) );

        var secondOrder = await GenerateOnceAsync();

        Assert.AreEqual( firstOrder, secondOrder,
            "Index creation order must not affect canonical output bytes." );
    }

    // ---- helpers -----------------------------------------------------------

    private async Task<string> GenerateOnceAsync()
    {
        // Build a context that wraps the production capture helper. Capture
        // happens AFTER topology resolution so the ISM prefix is known.
        var topology = await OpenSearchTopologySignature.CaptureAsync( _client );
        var ismPrefix = topology.IsmPathPrefix;

        var ctx = new OpenSearchSquashGenerationContext(
            squashName: "Squash_2000",
            squashVersion: 2000,
            client: _client,
            captureSnapshotAsync: async ( _, ct ) =>
            {
                var blob = await OpenSearchSnapshotCapture.CaptureAsync( _client, ismPrefix, ct );
                return new SnapshotCaptureResult( blob );
            } );

        var descriptors = new[]
        {
            new MigrationDescriptor( typeof( object ), new MigrationAttribute( 1000 ), Array.Empty<long>() ),
            new MigrationDescriptor( typeof( object ), new MigrationAttribute( 2000 ), Array.Empty<long>() ),
        };

        var strategy = new RestStateDiffStrategy(
            new OpenSearchSnapshotCanonicalizer(),
            new OpenSearchDataOpClassifier() );

        var result = await strategy.GenerateAsync( ctx, descriptors, new SquashGenerationOptions() );

        if ( result is SquashGenerationResult.Failed failed )
            Assert.Fail( $"GenerateAsync returned Failed: {failed.Detail}" );

        var generated = (SquashGenerationResult.Generated) result;
        return generated.Content;
    }
}

#endif
