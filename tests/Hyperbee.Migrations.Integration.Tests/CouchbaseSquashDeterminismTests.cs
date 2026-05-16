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
// F-1 closed: IsolatedCouchbaseContainer now configures alternate
// ("external") addresses via PUT /node/controller/setupAlternateAddresses
// after container start. The Couchbase SDK with `?network=external`
// receives a cluster map advertising the host-bound localhost service
// ports instead of the container's internal Docker addresses, so
// host-side connections succeed cleanly. Combined with the
// couchbase:community-7.6.2 image pin (fixes the 7.0.2 planner-catalog
// refresh bug), all 6 IsolatedCouchbaseContainer squash tests run
// green in CI under the per-provider matrix.

[TestClass]
[DoNotParallelize]
public class CouchbaseSquashDeterminismTests
{
    private IsolatedCouchbaseContainer _container;
    private ICluster _cluster;
    private ICouchbaseRestApiService _restApi;
    private HttpClient _http;
    [TestInitialize]
    public async Task Setup()
    {
        _container = await IsolatedCouchbaseContainer.StartAsync();
        _cluster = _container.ClusterHandle;

        var options = new ClusterOptions
        {
            ConnectionString = _container.ConnectionString,
            BootstrapHttpPort = _container.MgmtPort
        };

        _http = new HttpClient
        {
            BaseAddress = new Uri( $"http://localhost:{_container.MgmtPort}" )
        };
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
        _http?.Dispose();
        if ( _container != null )
            await _container.DisposeAsync();
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
        var bucket = await _cluster.BucketAsync( _container.BucketName );
        var queryIndexes = _cluster.QueryIndexes;

        await CouchbaseIndexRetry.CreateThenWaitReadyAsync(
            _cluster, _container.BucketName, "idx_det_primary", watchPrimaryUnnamed: false,
            () => queryIndexes.CreatePrimaryIndexAsync( _container.BucketName,
                new CreatePrimaryQueryIndexOptions().IndexName( "idx_det_primary" ) ) );
        await CouchbaseIndexRetry.CreateThenWaitReadyAsync(
            _cluster, _container.BucketName, "idx_det_email", watchPrimaryUnnamed: false,
            () => queryIndexes.CreateIndexAsync(
                _container.BucketName,
                "idx_det_email",
                new[] { "email" },
                new CreateQueryIndexOptions() ) );

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

        await CouchbaseIndexRetry.CreateThenWaitReadyAsync(
            _cluster, _container.BucketName, "idx_det_phone", watchPrimaryUnnamed: false,
            () => queryIndexes.CreateIndexAsync( _container.BucketName, "idx_det_phone", new[] { "phone" } ) );
        var first = await GenerateOnceAsync();

        await CouchbaseIndexRetry.DropThenWaitGoneAsync(
            _cluster, _container.BucketName, "idx_det_phone",
            () => queryIndexes.DropIndexAsync( _container.BucketName, "idx_det_phone" ) );
        await CouchbaseIndexRetry.CreateThenWaitReadyAsync(
            _cluster, _container.BucketName, "idx_det_phone", watchPrimaryUnnamed: false,
            () => queryIndexes.CreateIndexAsync( _container.BucketName, "idx_det_phone", new[] { "phone" } ) );
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
            bucketName: _container.BucketName,
            captureSnapshotAsync: async ( _, ct ) =>
            {
                var blob = await CouchbaseSnapshotCapture.CaptureAsync( _cluster, _restApi, _container.BucketName, ct );
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
            try { await queryIndexes.DropIndexAsync( _container.BucketName, name ); } catch { }
        }
    }
}

#endif
