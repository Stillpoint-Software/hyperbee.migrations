using FluentAssertions;
using Hyperbee.Migrations.Providers.MongoDB.Squash;
using Hyperbee.Migrations.Squash;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MongoDB.Driver;
using NSubstitute;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 3 Task 3.6: MongoDBSquashVerifier unit coverage.
//
// Same A4 byte-equality round logic as Aerospike + OpenSearch, structurally
// near-copy-paste. Tests mirror OpenSearch Task 2.6 to confirm the pattern
// holds for the third provider.

[TestClass]
public class MongoDBSquashVerifierTests
{
    private const string SnapshotBlob = """
        [collections]
        {"users": {"type": "collection"}}

        [indexes]
        {"users": [{"key": {"email": 1}, "name": "idx_email"}]}
        """;

    private const string DivergentSnapshotBlob = """
        [collections]
        {"users": {"type": "collection"}}

        [indexes]
        {"users": [{"key": {"phone": 1}, "name": "idx_phone"}]}
        """;

    private static MongoDBTopologySignature SyntheticTopology() => new()
    {
        ServerMajor = 7,
        ServerMinor = 0,
        FeatureCompatibilityVersion = "7.0",
        DeploymentTopology = "Standalone",
        ReplicaSetName = "",
        DatabaseName = "appdb",
        DefaultReadConcern = "local",
        DefaultWriteConcern = "1",
        StorageEngine = "wiredTiger"
    };

    private static MongoDBSquashGenerationContext MakeContext( string captureBlob = SnapshotBlob )
    {
        var client = Substitute.For<IMongoClient>();

        return new MongoDBSquashGenerationContext(
            squashName: "Squash_2000",
            squashVersion: 2000,
            client: client,
            databaseName: "appdb",
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
    public void ProviderId_IsMongoDB()
    {
        var verifier = new MongoDBSquashVerifier( new MongoDBSnapshotCanonicalizer() );
        verifier.ProviderId.Should().Be( "mongodb" );
    }

    [TestMethod]
    public void Constructor_NullCanonicalizer_Throws()
    {
        Action act = () => new MongoDBSquashVerifier( null );
        act.Should().Throw<ArgumentNullException>().WithParameterName( "canonicalizer" );
    }

    [TestMethod]
    public async Task VerifyAsync_WrongContextType_ReturnsFailed()
    {
        var verifier = new MongoDBSquashVerifier( new MongoDBSnapshotCanonicalizer() )
        {
            CaptureFromGeneratedAsync = ( _, _, _ ) => Task.FromResult( new SnapshotCaptureResult( SnapshotBlob ) )
        };

        var wrongContext = Substitute.For<ISquashGenerationContext>();
        wrongContext.ProviderId.Returns( "aerospike" );

        var result = await verifier.VerifyAsync( wrongContext, MakeGenerated() );

        result.Should().BeOfType<VerificationResult.Failed>()
            .Which.Detail.Should().Contain( "MongoDBSquashGenerationContext" );
    }

    [TestMethod]
    public async Task VerifyAsync_MissingCaptureDelegate_ReturnsFailed()
    {
        var verifier = new MongoDBSquashVerifier( new MongoDBSnapshotCanonicalizer() );

        var result = await verifier.VerifyAsync( MakeContext(), MakeGenerated() );

        result.Should().BeOfType<VerificationResult.Failed>()
            .Which.Detail.Should().Contain( "CaptureFromGeneratedAsync" );
    }

    [TestMethod]
    public async Task VerifyAsync_NullGenerated_ReturnsFailed()
    {
        var verifier = new MongoDBSquashVerifier( new MongoDBSnapshotCanonicalizer() )
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
        var verifier = new MongoDBSquashVerifier( new MongoDBSnapshotCanonicalizer() )
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
        var verifier = new MongoDBSquashVerifier( new MongoDBSnapshotCanonicalizer() )
        {
            CaptureFromGeneratedAsync = ( _, _, _ ) => Task.FromResult( new SnapshotCaptureResult( SnapshotBlob ) )
        };

        var result = await verifier.VerifyAsync( MakeContext( SnapshotBlob ), MakeGenerated() );

        result.Should().BeOfType<VerificationResult.Success>();
        var success = (VerificationResult.Success) result;
        success.Topology.Should().BeAssignableTo<MongoDBTopologySignature>();
        success.Elapsed.Should().BeGreaterThanOrEqualTo( TimeSpan.Zero );
    }

    [TestMethod]
    public async Task VerifyAsync_DivergentSnapshots_ReturnsFailedWithDiff()
    {
        var verifier = new MongoDBSquashVerifier( new MongoDBSnapshotCanonicalizer() )
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
        // (uuid, v) must canonicalize identically -> Success. Critical
        // for verification rounds where the A and B containers have
        // different server-generated UUIDs.
        const string blobA = """
            [collections]
            {"users": {"info": {"uuid": "uuid-aaa"}, "type": "collection"}}

            [indexes]
            {"users": [{"v": 1, "key": {"email": 1}, "name": "idx_email"}]}
            """;

        const string blobB = """
            [collections]
            {"users": {"info": {"uuid": "uuid-bbb"}, "type": "collection"}}

            [indexes]
            {"users": [{"v": 2, "key": {"email": 1}, "name": "idx_email"}]}
            """;

        var verifier = new MongoDBSquashVerifier( new MongoDBSnapshotCanonicalizer() )
        {
            CaptureFromGeneratedAsync = ( _, _, _ ) => Task.FromResult( new SnapshotCaptureResult( blobB ) )
        };

        var result = await verifier.VerifyAsync( MakeContext( blobA ), MakeGenerated() );

        result.Should().BeOfType<VerificationResult.Success>();
    }

    [TestMethod]
    public async Task VerifyAsync_CaptureThrows_ReturnsFailedWithCause()
    {
        var verifier = new MongoDBSquashVerifier( new MongoDBSnapshotCanonicalizer() )
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

        var verifier = new MongoDBSquashVerifier( new MongoDBSnapshotCanonicalizer() )
        {
            CaptureFromGeneratedAsync = ( _, _, ct ) =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult( new SnapshotCaptureResult( SnapshotBlob ) );
            }
        };

        var ctx = new MongoDBSquashGenerationContext(
            squashName: "Squash_2000",
            squashVersion: 2000,
            client: Substitute.For<IMongoClient>(),
            databaseName: "appdb",
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

        var summary = MongoDBSquashVerifier.SummarizeDiff( aLines, bLines );

        summary.Should().Contain( "only in snapshot A" );
        summary.Should().Contain( "only in snapshot B" );
        summary.Should().Contain( "truncated" );
    }
}
