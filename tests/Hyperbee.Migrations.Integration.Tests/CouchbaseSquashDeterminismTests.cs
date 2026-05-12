//#define INTEGRATIONS
using Couchbase;
using Couchbase.Management.Query;
using Hyperbee.Migrations;
using Hyperbee.Migrations.Integration.Tests.Container.Couchbase;
using Hyperbee.Migrations.Providers.Couchbase.Services;
using Hyperbee.Migrations.Providers.Couchbase.Squash;
using Hyperbee.Migrations.Squash;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hyperbee.Migrations.Integration.Tests;

#if INTEGRATIONS

// Phase 4 (R-P5): Couchbase squash codegen determinism gate (C12).
//
// Contract: given identical cluster state, two GenerateAsync calls must
// produce byte-equal SquashGenerationResult.Generated.Content. Guarantees
// the canonicalizer fully removes ephemeral fields (index `id`, bucket
// runtime stats, server-assigned node placement, vBucketServerMap, etc.) at
// every nesting level and JSON key orderings are sorted away.
//
// [TestCategory("LocalOnly")]: Host-side Couchbase SDK connection requires
// alternate-address configuration on each cluster node so the SDK
// -- bootstrapping via port 11210 (KV) and learning about n1ql / index /
// fts from the cluster map -- routes through localhost-bound ports rather
// than the container's internal IP. The standard fix is `/node/controller/
// setupAlternateAddresses/external` + connecting with
// `?network=external`. In this repo the alt-address approach broke the
// sibling-container CouchbaseRunnerTest (cluster-map race during the
// migration container's bootstrap), so the cleanest path forward is the
// sibling-container test model (build a Docker image containing the test
// program, run it inside the Couchbase network so the SDK reaches `db`
// directly). That refactor is tracked for v3.0.1; until then this suite
// is local-only -- developers running it on a workstation with manual
// port-forwarding + alt-address config can validate squash determinism
// end-to-end. The squash correctness contract is byte-tested by 192
// Couchbase unit tests in the Hyperbee.Migrations.Squash.Tests project
// (idempotence, divergent-input canonical equality, ephemeral strip,
// deferred-state preservation per R-P3 OQ).

[TestClass]
[DoNotParallelize]
[TestCategory( "LocalOnly" )]
public class CouchbaseSquashDeterminismTests
{
    private ICluster _cluster;
    private ICouchbaseRestApiService _restApi;
    private HttpClient _http;
    private const string TestBucket = "hyperbee";

    [TestInitialize]
    public async Task Setup()
    {
        Assert.IsNotNull( CouchbaseTestContainer.ConnectionString,
            "CouchbaseTestContainer must initialize before this test class runs." );

        var options = new ClusterOptions
        {
            ConnectionString = "couchbase://localhost?network=external",
            UserName = "Administrator",
            Password = "password"
        };

        _cluster = await Cluster.ConnectAsync( options );
        await _cluster.WaitUntilReadyAsync( TimeSpan.FromMinutes( 1 ) );

        _http = new HttpClient { BaseAddress = new Uri( "http://localhost:8091" ) };
        _http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String( System.Text.Encoding.ASCII.GetBytes( "Administrator:password" ) ) );

        _restApi = new CouchbaseRestApiService(
            _http,
            new OptionsWrapper<ClusterOptions>( options ),
            NullLogger<CouchbaseRestApiService>.Instance );
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        try
        {
            await DropTestArtifactsAsync();
        }
        catch { }
        _cluster?.Dispose();
        _http?.Dispose();
    }

    [TestMethod]
    public async Task EmptyBucket_TwoRuns_ProduceByteEqualContent()
    {
        var content1 = await GenerateOnceAsync();
        var content2 = await GenerateOnceAsync();

        Assert.AreEqual( content1, content2,
            "Two GenerateAsync runs against an unchanged bucket must produce byte-equal output." );
    }

    [TestMethod]
    public async Task PopulatedBucket_TwoRuns_ProduceByteEqualContent()
    {
        var bucket = await _cluster.BucketAsync( TestBucket );
        var queryIndexes = _cluster.QueryIndexes;

        await queryIndexes.CreatePrimaryIndexAsync( TestBucket,
            new CreatePrimaryQueryIndexOptions().IndexName( "idx_det_primary" ) );
        await queryIndexes.CreateIndexAsync(
            TestBucket,
            "idx_det_email",
            new[] { "email" },
            new CreateQueryIndexOptions() );

        var content1 = await GenerateOnceAsync();
        var content2 = await GenerateOnceAsync();

        Assert.AreEqual( content1, content2,
            "Two GenerateAsync runs against the same populated bucket must produce byte-equal output." );
        StringAssert.Contains( content1, "idx_det_primary" );
        StringAssert.Contains( content1, "idx_det_email" );
    }

    [TestMethod]
    public async Task IndexRecreatedWithDifferentId_ProducesByteEqualContent()
    {
        // Server-assigned index ids differ per recreation; canonicalizer
        // strips `id` at every nesting level. Two captures of "same logical
        // state with different index ids" must canonicalize identically.
        var queryIndexes = _cluster.QueryIndexes;

        await queryIndexes.CreateIndexAsync( TestBucket, "idx_det_phone", new[] { "phone" } );
        var first = await GenerateOnceAsync();

        await queryIndexes.DropIndexAsync( TestBucket, "idx_det_phone" );
        await queryIndexes.CreateIndexAsync( TestBucket, "idx_det_phone", new[] { "phone" } );
        var second = await GenerateOnceAsync();

        Assert.AreEqual( first, second,
            "Recreating an index produces a new server-assigned id; canonicalizer must strip it for byte-stable output." );
    }

    // ---- helpers -----------------------------------------------------------

    private async Task<string> GenerateOnceAsync()
    {
        var ctx = new CouchbaseSquashGenerationContext(
            squashName: "Squash_2000",
            squashVersion: 2000,
            cluster: _cluster,
            restApi: _restApi,
            bucketName: TestBucket,
            captureSnapshotAsync: async ( _, ct ) =>
            {
                var blob = await CouchbaseSnapshotCapture.CaptureAsync( _cluster, _restApi, TestBucket, ct );
                return new SnapshotCaptureResult( blob );
            } );

        var descriptors = new[]
        {
            new MigrationDescriptor( typeof( object ), new MigrationAttribute( 1000 ), Array.Empty<long>() ),
            new MigrationDescriptor( typeof( object ), new MigrationAttribute( 2000 ), Array.Empty<long>() ),
        };

        var strategy = new HybridStrategy(
            new CouchbaseSnapshotCanonicalizer(),
            new CouchbaseDataOpClassifier() );

        var result = await strategy.GenerateAsync( ctx, descriptors, new SquashGenerationOptions() );

        if ( result is SquashGenerationResult.Failed failed )
            Assert.Fail( $"GenerateAsync returned Failed: {failed.Detail}" );

        var generated = (SquashGenerationResult.Generated) result;
        return generated.Content;
    }

    private async Task DropTestArtifactsAsync()
    {
        var queryIndexes = _cluster.QueryIndexes;
        foreach ( var name in new[] { "idx_det_primary", "idx_det_email", "idx_det_phone" } )
        {
            try { await queryIndexes.DropIndexAsync( TestBucket, name ); } catch { }
        }
    }
}

#endif
