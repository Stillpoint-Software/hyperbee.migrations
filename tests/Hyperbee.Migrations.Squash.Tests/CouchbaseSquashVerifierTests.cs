using Couchbase;
using FluentAssertions;
using Hyperbee.Migrations.Providers.Couchbase.Services;
using Hyperbee.Migrations.Providers.Couchbase.Squash;
using Hyperbee.Migrations.Squash;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 4 Task 4.6: CouchbaseSquashVerifier unit coverage.
//
// Structurally near-copy-paste from MongoDB Task 3.6 + OpenSearch Task 2.6
// + Aerospike Task 1.6 -- fourth verifier of identical shape. Tests confirm
// the pattern holds for the fourth provider.

[TestClass]
public class CouchbaseSquashVerifierTests
{
    private const string SnapshotBlob = """
        [buckets]
        {"myapp": {"bucketType": "membase", "ramQuotaMB": 256}}

        [indexes]
        {"myapp/_default/_default/idx_email": {"name": "idx_email", "keyspace_id": "myapp"}}
        """;

    private const string DivergentSnapshotBlob = """
        [buckets]
        {"myapp": {"bucketType": "membase", "ramQuotaMB": 256}}

        [indexes]
        {"myapp/_default/_default/idx_phone": {"name": "idx_phone", "keyspace_id": "myapp"}}
        """;

    private static CouchbaseTopologySignature SyntheticTopology() => new()
    {
        ServerMajor = 7,
        ServerMinor = 2,
        Edition = "Enterprise",
        Services = new[] { "kv", "n1ql", "index" },
        BucketName = "myapp",
        BucketType = "membase",
        StorageBackend = "couchstore",
        ReplicaCount = 1,
        MemoryQuotaMB = 256
    };

    private static CouchbaseSquashGenerationContext MakeContext( string captureBlob = SnapshotBlob )
    {
        var cluster = Substitute.For<ICluster>();
        var restApi = Substitute.For<ICouchbaseRestApiService>();

        return new CouchbaseSquashGenerationContext(
            squashName: "Squash_2000",
            squashVersion: 2000,
            cluster: cluster,
            restApi: restApi,
            bucketName: "myapp",
            captureSnapshotAsync: ( _, _ ) => Task.FromResult( new SnapshotCaptureResult( captureBlob ) ) );
    }

    private static SquashGenerationResult.Generated MakeGenerated(
        string content = "{}",
        long[] replaces = null )
    {
        return new SquashGenerationResult.Generated(
            Content: content,
            Kind: ContentKind.CanonicalJson,
            Encoding: ContentEncoding.Utf8,
            Replaces: replaces ?? new long[] { 1000, 2000 },
            Diagnostics: Array.Empty<string>(),
            Topology: SyntheticTopology() );
    }

    [TestMethod]
    public void ProviderId_IsCouchbase()
    {
        var verifier = new CouchbaseSquashVerifier( new CouchbaseSnapshotCanonicalizer() );
        verifier.ProviderId.Should().Be( "couchbase" );
    }

    [TestMethod]
    public void Constructor_NullCanonicalizer_Throws()
    {
        Action act = () => new CouchbaseSquashVerifier( null );
        act.Should().Throw<ArgumentNullException>().WithParameterName( "canonicalizer" );
    }

    [TestMethod]
    public async Task VerifyAsync_WrongContextType_ReturnsFailed()
    {
        var verifier = new CouchbaseSquashVerifier( new CouchbaseSnapshotCanonicalizer() )
        {
            CaptureFromGeneratedAsync = ( _, _, _ ) => Task.FromResult( new SnapshotCaptureResult( SnapshotBlob ) )
        };

        var wrongContext = Substitute.For<ISquashGenerationContext>();
        wrongContext.ProviderId.Returns( "mongodb" );

        var result = await verifier.VerifyAsync( wrongContext, MakeGenerated() );

        result.Should().BeOfType<VerificationResult.Failed>()
            .Which.Detail.Should().Contain( "CouchbaseSquashGenerationContext" );
    }

    [TestMethod]
    public async Task VerifyAsync_MissingCaptureDelegate_ReturnsFailed()
    {
        var verifier = new CouchbaseSquashVerifier( new CouchbaseSnapshotCanonicalizer() );

        var result = await verifier.VerifyAsync( MakeContext(), MakeGenerated() );

        result.Should().BeOfType<VerificationResult.Failed>()
            .Which.Detail.Should().Contain( "CaptureFromGeneratedAsync" );
    }

    [TestMethod]
    public async Task VerifyAsync_NullGenerated_ReturnsFailed()
    {
        var verifier = new CouchbaseSquashVerifier( new CouchbaseSnapshotCanonicalizer() )
        {
            CaptureFromGeneratedAsync = ( _, _, _ ) => Task.FromResult( new SnapshotCaptureResult( SnapshotBlob ) )
        };

        var result = await verifier.VerifyAsync( MakeContext(), generated: null );

        result.Should().BeOfType<VerificationResult.Failed>()
            .Which.Detail.Should().Contain( "non-null Generated" );
    }

    [TestMethod]
    public async Task VerifyAsync_EmptyReplaces_ReturnsFailed()
    {
        var verifier = new CouchbaseSquashVerifier( new CouchbaseSnapshotCanonicalizer() )
        {
            CaptureFromGeneratedAsync = ( _, _, _ ) => Task.FromResult( new SnapshotCaptureResult( SnapshotBlob ) )
        };

        var result = await verifier.VerifyAsync( MakeContext(), MakeGenerated( replaces: Array.Empty<long>() ) );

        result.Should().BeOfType<VerificationResult.Failed>()
            .Which.Detail.Should().Contain( "Replaces is empty" );
    }

    [TestMethod]
    public async Task VerifyAsync_MatchingSnapshots_ReturnsSuccess()
    {
        var verifier = new CouchbaseSquashVerifier( new CouchbaseSnapshotCanonicalizer() )
        {
            CaptureFromGeneratedAsync = ( _, _, _ ) => Task.FromResult( new SnapshotCaptureResult( SnapshotBlob ) )
        };

        var result = await verifier.VerifyAsync( MakeContext( SnapshotBlob ), MakeGenerated() );

        result.Should().BeOfType<VerificationResult.Success>();
        var success = (VerificationResult.Success) result;
        success.Topology.Should().BeAssignableTo<CouchbaseTopologySignature>();
        success.Elapsed.Should().BeGreaterThanOrEqualTo( TimeSpan.Zero );
    }

    [TestMethod]
    public async Task VerifyAsync_DivergentSnapshots_ReturnsFailedWithDiff()
    {
        var verifier = new CouchbaseSquashVerifier( new CouchbaseSnapshotCanonicalizer() )
        {
            CaptureFromGeneratedAsync = ( _, _, _ ) => Task.FromResult( new SnapshotCaptureResult( DivergentSnapshotBlob ) )
        };

        var result = await verifier.VerifyAsync( MakeContext( SnapshotBlob ), MakeGenerated() );

        result.Should().BeOfType<VerificationResult.Failed>();
        var failed = (VerificationResult.Failed) result;
        failed.Detail.Should().Contain( "verification failed" );
        failed.DiffSummary.Should().NotBeNullOrWhiteSpace();
        failed.DiffSummary.Should().Contain( "snapshot A" ).And.Contain( "snapshot B" );
        failed.DiffSummary.Should().Contain( "idx_email" ).And.Contain( "idx_phone" );
    }

    [TestMethod]
    public async Task VerifyAsync_EphemeralDifferenceOnly_ReturnsSuccess()
    {
        // Two captures whose ONLY difference is in ephemeral fields
        // (id, docCount) must canonicalize identically -> Success. Critical
        // for verification rounds where the A and B containers have
        // different server-generated index ids and runtime stats.
        const string blobA = """
            [buckets]
            {"myapp": {"bucketType": "membase", "docCount": 1, "ramQuotaMB": 256}}

            [indexes]
            {"myapp/_default/_default/idx_email": {"id": "id-aaa", "name": "idx_email", "keyspace_id": "myapp", "state": "online"}}
            """;

        const string blobB = """
            [buckets]
            {"myapp": {"bucketType": "membase", "docCount": 999999, "ramQuotaMB": 256}}

            [indexes]
            {"myapp/_default/_default/idx_email": {"id": "id-bbb", "name": "idx_email", "keyspace_id": "myapp", "state": "online"}}
            """;

        var verifier = new CouchbaseSquashVerifier( new CouchbaseSnapshotCanonicalizer() )
        {
            CaptureFromGeneratedAsync = ( _, _, _ ) => Task.FromResult( new SnapshotCaptureResult( blobB ) )
        };

        var result = await verifier.VerifyAsync( MakeContext( blobA ), MakeGenerated() );

        result.Should().BeOfType<VerificationResult.Success>();
    }

    [TestMethod]
    public async Task VerifyAsync_CaptureThrows_ReturnsFailedWithCause()
    {
        var verifier = new CouchbaseSquashVerifier( new CouchbaseSnapshotCanonicalizer() )
        {
            CaptureFromGeneratedAsync = ( _, _, _ ) => throw new InvalidOperationException( "container exploded" )
        };

        var result = await verifier.VerifyAsync( MakeContext( SnapshotBlob ), MakeGenerated() );

        result.Should().BeOfType<VerificationResult.Failed>();
        var failed = (VerificationResult.Failed) result;
        failed.Detail.Should().Contain( "container exploded" );
        failed.Cause.Should().BeOfType<InvalidOperationException>();
    }

    [TestMethod]
    public async Task VerifyAsync_CancellationPropagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var verifier = new CouchbaseSquashVerifier( new CouchbaseSnapshotCanonicalizer() )
        {
            CaptureFromGeneratedAsync = ( _, _, ct ) =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult( new SnapshotCaptureResult( SnapshotBlob ) );
            }
        };

        var ctx = new CouchbaseSquashGenerationContext(
            squashName: "Squash_2000",
            squashVersion: 2000,
            cluster: Substitute.For<ICluster>(),
            restApi: Substitute.For<ICouchbaseRestApiService>(),
            bucketName: "myapp",
            captureSnapshotAsync: ( _, ct ) =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult( new SnapshotCaptureResult( SnapshotBlob ) );
            } );

        Func<Task> act = async () => await verifier.VerifyAsync( ctx, MakeGenerated(), cts.Token );
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    [TestMethod]
    public void SummarizeDiff_TruncatesLargeDeltas()
    {
        var aLines = string.Join( '\n', Enumerable.Range( 0, 50 ).Select( i => $"A_LINE_{i}" ) );
        var bLines = string.Join( '\n', Enumerable.Range( 0, 50 ).Select( i => $"B_LINE_{i}" ) );

        var summary = CouchbaseSquashVerifier.SummarizeDiff( aLines, bLines );

        summary.Should().Contain( "only in snapshot A" );
        summary.Should().Contain( "only in snapshot B" );
        summary.Should().Contain( "truncated" );
    }
}
