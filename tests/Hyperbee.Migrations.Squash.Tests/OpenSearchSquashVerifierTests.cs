using FluentAssertions;
using Hyperbee.Migrations.Providers.OpenSearch.Squash;
using Hyperbee.Migrations.Squash;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using OpenSearch.Client;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 2 Task 2.6: OpenSearchSquashVerifier unit coverage.
//
// Exercises the A4 byte-equality round logic with synthetic capture
// delegates. Real Testcontainers + apply-and-snapshot round-trip lives in
// the Phase 2 integration suite.

[TestClass]
public class OpenSearchSquashVerifierTests
{
    private const string SnapshotBlob = """
        [alias]
        {"users-current": {"aliases": {"users-v1": {}}}}

        [index_template]
        {"users-template": {"index_patterns": ["users-*"]}}
        """;

    private const string DivergentSnapshotBlob = """
        [alias]
        {"users-current": {"aliases": {"users-v2": {}}}}

        [index_template]
        {"users-template": {"index_patterns": ["users-*"]}}
        """;

    private static OpenSearchTopologySignature SyntheticTopology() => new()
    {
        ServerMajor = 2,
        ServerMinor = 13,
        Distribution = "opensearch",
        ClusterName = "test-cluster",
        NodeCount = 1,
        Plugins = new[] { "opensearch-index-management" },
        IsmPathPrefix = "_plugins/_ism"
    };

    private static OpenSearchSquashGenerationContext MakeContext( string captureBlob = SnapshotBlob )
    {
        var client = Substitute.For<IOpenSearchClient>();

        return new OpenSearchSquashGenerationContext(
            squashName: "Squash_2000",
            squashVersion: 2000,
            client: client,
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
    public void ProviderId_IsOpenSearch()
    {
        var verifier = new OpenSearchSquashVerifier( new OpenSearchSnapshotCanonicalizer() );
        verifier.ProviderId.Should().Be( "opensearch" );
    }

    [TestMethod]
    public void Constructor_NullCanonicalizer_Throws()
    {
        Action act = () => new OpenSearchSquashVerifier( null! );
        act.Should().Throw<ArgumentNullException>().WithParameterName( "canonicalizer" );
    }

    [TestMethod]
    public async Task VerifyAsync_WrongContextType_ReturnsFailed()
    {
        var verifier = new OpenSearchSquashVerifier( new OpenSearchSnapshotCanonicalizer() )
        {
            CaptureFromGeneratedAsync = ( _, _, _ ) => Task.FromResult( new SnapshotCaptureResult( SnapshotBlob ) )
        };

        var wrongContext = Substitute.For<ISquashGenerationContext>();
        wrongContext.ProviderId.Returns( "aerospike" );

        var result = await verifier.VerifyAsync( wrongContext, MakeGenerated() );

        result.Should().BeOfType<VerificationResult.Failed>()
            .Which.Detail.Should().Contain( "OpenSearchSquashGenerationContext" );
    }

    [TestMethod]
    public async Task VerifyAsync_MissingCaptureDelegate_ReturnsFailed()
    {
        var verifier = new OpenSearchSquashVerifier( new OpenSearchSnapshotCanonicalizer() );
        // CaptureFromGeneratedAsync not wired.

        var result = await verifier.VerifyAsync( MakeContext(), MakeGenerated() );

        result.Should().BeOfType<VerificationResult.Failed>()
            .Which.Detail.Should().Contain( "CaptureFromGeneratedAsync" );
    }

    [TestMethod]
    public async Task VerifyAsync_NullGenerated_ReturnsFailed()
    {
        var verifier = new OpenSearchSquashVerifier( new OpenSearchSnapshotCanonicalizer() )
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
        var verifier = new OpenSearchSquashVerifier( new OpenSearchSnapshotCanonicalizer() )
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
        var verifier = new OpenSearchSquashVerifier( new OpenSearchSnapshotCanonicalizer() )
        {
            // Both A and B return the same blob -> canonical match.
            CaptureFromGeneratedAsync = ( _, _, _ ) => Task.FromResult( new SnapshotCaptureResult( SnapshotBlob ) )
        };

        var result = await verifier.VerifyAsync( MakeContext( SnapshotBlob ), MakeGenerated() );

        result.Should().BeOfType<VerificationResult.Success>();
        var success = (VerificationResult.Success) result;
        success.Topology.Should().BeAssignableTo<OpenSearchTopologySignature>();
        success.Elapsed.Should().BeGreaterThanOrEqualTo( TimeSpan.Zero );
    }

    [TestMethod]
    public async Task VerifyAsync_DivergentSnapshots_ReturnsFailedWithDiff()
    {
        var verifier = new OpenSearchSquashVerifier( new OpenSearchSnapshotCanonicalizer() )
        {
            CaptureFromGeneratedAsync = ( _, _, _ ) => Task.FromResult( new SnapshotCaptureResult( DivergentSnapshotBlob ) )
        };

        var result = await verifier.VerifyAsync( MakeContext( SnapshotBlob ), MakeGenerated() );

        result.Should().BeOfType<VerificationResult.Failed>();
        var failed = (VerificationResult.Failed) result;
        failed.Detail.Should().Contain( "verification failed" );
        failed.DiffSummary.Should().NotBeNullOrWhiteSpace();
        failed.DiffSummary.Should().Contain( "snapshot A" );
        failed.DiffSummary.Should().Contain( "snapshot B" );
        // The divergent snapshots differ on the aliases target index.
        failed.DiffSummary.Should().Contain( "users-v1" ).And.Contain( "users-v2" );
    }

    [TestMethod]
    public async Task VerifyAsync_EphemeralDifferenceOnly_ReturnsSuccess()
    {
        // The canonicalizer strips ephemerals, so two captures whose ONLY
        // difference is in the ephemeral fields (creation_date, uuid, etc.)
        // should canonicalize identically and verify as Success. This is
        // the load-bearing property that lets the verifier run without
        // synchronizing clock sources between the A and B containers.
        const string blobA = """
            [index_template]
            {"t1": {"creation_date": "1700000000", "version": 1, "index_patterns": ["x-*"]}}
            """;

        const string blobB = """
            [index_template]
            {"t1": {"creation_date": "1800000000", "version": 2, "index_patterns": ["x-*"]}}
            """;

        var verifier = new OpenSearchSquashVerifier( new OpenSearchSnapshotCanonicalizer() )
        {
            CaptureFromGeneratedAsync = ( _, _, _ ) => Task.FromResult( new SnapshotCaptureResult( blobB ) )
        };

        var result = await verifier.VerifyAsync( MakeContext( blobA ), MakeGenerated() );

        result.Should().BeOfType<VerificationResult.Success>();
    }

    [TestMethod]
    public async Task VerifyAsync_CaptureThrows_ReturnsFailedWithCause()
    {
        var verifier = new OpenSearchSquashVerifier( new OpenSearchSnapshotCanonicalizer() )
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

        var verifier = new OpenSearchSquashVerifier( new OpenSearchSnapshotCanonicalizer() )
        {
            CaptureFromGeneratedAsync = ( _, _, ct ) =>
            {
                ct.ThrowIfCancellationRequested();
                return Task.FromResult( new SnapshotCaptureResult( SnapshotBlob ) );
            }
        };

        var ctx = new OpenSearchSquashGenerationContext(
            squashName: "Squash_2000",
            squashVersion: 2000,
            client: Substitute.For<IOpenSearchClient>(),
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

        var summary = OpenSearchSquashVerifier.SummarizeDiff( aLines, bLines );

        summary.Should().Contain( "only in snapshot A" );
        summary.Should().Contain( "only in snapshot B" );
        summary.Should().Contain( "truncated" );
    }
}
