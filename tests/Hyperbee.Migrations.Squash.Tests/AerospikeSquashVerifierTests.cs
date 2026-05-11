using FluentAssertions;
using Hyperbee.Migrations.Providers.Aerospike.Squash;
using Hyperbee.Migrations.Squash;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 1 (R-P1, R-P6): AerospikeSquashVerifier unit coverage.
//
// Exercises the A4 byte-equality round logic with synthetic capture
// delegates. Real Testcontainers + apply-and-snapshot round-trip lives in
// the Phase 1 integration suite.

[TestClass]
public class AerospikeSquashVerifierTests
{
    private const string SnapshotBlob = """
        [sets]
        ns=test:set=users;ns=test:set=orders

        [sindex]
        ns=test:indexname=idx_email:set=users:bin=email:type=STRING
        """;

    private const string DivergentSnapshotBlob = """
        [sets]
        ns=test:set=users

        [sindex]
        ns=test:indexname=idx_age:set=users:bin=age:type=NUMERIC
        """;

    private static AerospikeTopologySignature SyntheticTopology() => new()
    {
        ServerMajor = 6,
        ServerMinor = 4,
        Namespace = "test",
        ReplicationFactor = 2,
        DefaultTtl = 2592000,
        NsupPeriod = 120,
        MemorySize = 1073741824L,
        StorageEngine = "memory",
        ClusterName = "null"
    };

    private static AerospikeSquashGenerationContext MakeContext(
        string captureBlob = SnapshotBlob )
    {
        var client = Substitute.For<Aerospike.Client.IAerospikeClient>();

        return new AerospikeSquashGenerationContext(
            squashName: "Squash_2000",
            squashVersion: 2000,
            client: client,
            @namespace: "test",
            captureSnapshotAsync: ( _, _ ) => Task.FromResult( new SnapshotCaptureResult( captureBlob ) ) );
    }

    private static SquashGenerationResult.Generated MakeGenerated(
        string content = "CREATE SET test.users;",
        long[] replaces = null )
    {
        return new SquashGenerationResult.Generated(
            Content: content,
            Kind: ContentKind.SqlText,
            Encoding: ContentEncoding.Utf8,
            Replaces: replaces ?? new long[] { 1000, 2000 },
            Diagnostics: Array.Empty<string>(),
            Topology: SyntheticTopology() );
    }

    [TestMethod]
    public void ProviderId_IsAerospike()
    {
        var verifier = new AerospikeSquashVerifier( new AerospikeSnapshotCanonicalizer() );
        verifier.ProviderId.Should().Be( "aerospike" );
    }

    [TestMethod]
    public void Constructor_NullCanonicalizer_Throws()
    {
        Action act = () => new AerospikeSquashVerifier( null! );
        act.Should().Throw<ArgumentNullException>().WithParameterName( "canonicalizer" );
    }

    [TestMethod]
    public async Task VerifyAsync_WrongContextType_ReturnsFailed()
    {
        var verifier = new AerospikeSquashVerifier( new AerospikeSnapshotCanonicalizer() )
        {
            CaptureFromGeneratedAsync = ( _, _, _ ) => Task.FromResult( new SnapshotCaptureResult( SnapshotBlob ) )
        };

        var wrongContext = Substitute.For<ISquashGenerationContext>();
        wrongContext.ProviderId.Returns( "postgres" );

        var result = await verifier.VerifyAsync( wrongContext, MakeGenerated() );

        result.Should().BeOfType<VerificationResult.Failed>()
            .Which.Detail.Should().Contain( "AerospikeSquashGenerationContext" );
    }

    [TestMethod]
    public async Task VerifyAsync_MissingCaptureDelegate_ReturnsFailed()
    {
        var verifier = new AerospikeSquashVerifier( new AerospikeSnapshotCanonicalizer() );
        // CaptureFromGeneratedAsync not wired.

        var result = await verifier.VerifyAsync( MakeContext(), MakeGenerated() );

        result.Should().BeOfType<VerificationResult.Failed>()
            .Which.Detail.Should().Contain( "CaptureFromGeneratedAsync" );
    }

    [TestMethod]
    public async Task VerifyAsync_NullGenerated_ReturnsFailed()
    {
        var verifier = new AerospikeSquashVerifier( new AerospikeSnapshotCanonicalizer() )
        {
            CaptureFromGeneratedAsync = ( _, _, _ ) => Task.FromResult( new SnapshotCaptureResult( SnapshotBlob ) )
        };

        var result = await verifier.VerifyAsync( MakeContext(), generated: null! );

        result.Should().BeOfType<VerificationResult.Failed>()
            .Which.Detail.Should().Contain( "non-null Generated" );
    }

    [TestMethod]
    public async Task VerifyAsync_EmptyReplaces_ReturnsFailed()
    {
        var verifier = new AerospikeSquashVerifier( new AerospikeSnapshotCanonicalizer() )
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
        var verifier = new AerospikeSquashVerifier( new AerospikeSnapshotCanonicalizer() )
        {
            // Both A and B return the same blob -> canonical match.
            CaptureFromGeneratedAsync = ( _, _, _ ) => Task.FromResult( new SnapshotCaptureResult( SnapshotBlob ) )
        };

        var result = await verifier.VerifyAsync( MakeContext( SnapshotBlob ), MakeGenerated() );

        result.Should().BeOfType<VerificationResult.Success>();
        var success = (VerificationResult.Success) result;
        success.Topology.Should().BeAssignableTo<AerospikeTopologySignature>();
        success.Elapsed.Should().BeGreaterThanOrEqualTo( TimeSpan.Zero );
    }

    [TestMethod]
    public async Task VerifyAsync_DivergentSnapshots_ReturnsFailedWithDiff()
    {
        var verifier = new AerospikeSquashVerifier( new AerospikeSnapshotCanonicalizer() )
        {
            // Generated capture returns a divergent blob.
            CaptureFromGeneratedAsync = ( _, _, _ ) => Task.FromResult( new SnapshotCaptureResult( DivergentSnapshotBlob ) )
        };

        var result = await verifier.VerifyAsync( MakeContext( SnapshotBlob ), MakeGenerated() );

        result.Should().BeOfType<VerificationResult.Failed>();
        var failed = (VerificationResult.Failed) result;
        failed.Detail.Should().Contain( "verification failed" );
        failed.DiffSummary.Should().NotBeNullOrWhiteSpace();
        failed.DiffSummary.Should().Contain( "snapshot A" );
        failed.DiffSummary.Should().Contain( "snapshot B" );
        // The historical (A) snapshot has 2 sets; the generated (B) has 1.
        failed.DiffSummary.Should().Contain( "CREATE SET test.orders" );
    }

    [TestMethod]
    public async Task VerifyAsync_CaptureThrows_ReturnsFailedWithCause()
    {
        var verifier = new AerospikeSquashVerifier( new AerospikeSnapshotCanonicalizer() )
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

        var verifier = new AerospikeSquashVerifier( new AerospikeSnapshotCanonicalizer() )
        {
            CaptureFromGeneratedAsync = ( _, _, ct ) =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult( new SnapshotCaptureResult( SnapshotBlob ) );
            }
        };

        var ctx = new AerospikeSquashGenerationContext(
            squashName: "Squash_2000",
            squashVersion: 2000,
            client: Substitute.For<Aerospike.Client.IAerospikeClient>(),
            @namespace: "test",
            captureSnapshotAsync: ( _, ct ) =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult( new SnapshotCaptureResult( SnapshotBlob ) );
            } );

        Func<Task> act = async () => await verifier.VerifyAsync( ctx, MakeGenerated(), cts.Token );
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---- internal helper -----------------------------------------------------

    [TestMethod]
    public void SummarizeDiff_TruncatesLargeDeltas()
    {
        var aLines = string.Join( '\n', Enumerable.Range( 0, 50 ).Select( i => $"A_LINE_{i}" ) );
        var bLines = string.Join( '\n', Enumerable.Range( 0, 50 ).Select( i => $"B_LINE_{i}" ) );

        var summary = AerospikeSquashVerifier.SummarizeDiff( aLines, bLines );

        summary.Should().Contain( "only in snapshot A" );
        summary.Should().Contain( "only in snapshot B" );
        summary.Should().Contain( "truncated" );
    }
}
