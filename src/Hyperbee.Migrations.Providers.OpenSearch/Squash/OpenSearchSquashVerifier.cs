#nullable enable
using System.Diagnostics;
using System.Text;
using Hyperbee.Migrations.Squash;

namespace Hyperbee.Migrations.Providers.OpenSearch.Squash;

/// <summary>
/// OpenSearch squash verifier per ADR-0019 + amendment A4 (snapshot caching +
/// parallel A/B capture) and A18 (container lifecycle on failure).
/// </summary>
/// <remarks>
/// <para>
/// V1 verification round shape (mirrors Aerospike Task 1.6):
/// <list type="number">
///   <item>Capture snapshot A by re-applying the historical migration range
///         through the upper bound against a fresh ephemeral cluster.</item>
///   <item>Capture snapshot B by applying the GENERATED squash content to a
///         second fresh ephemeral cluster.</item>
///   <item>Canonicalize both via <see cref="OpenSearchSnapshotCanonicalizer"/>.</item>
///   <item>Byte-compare. Mismatch surfaces a <see cref="VerificationResult.Failed"/>
///         with a line-by-line diff summary (truncated to 20 lines per side);
///         per A18 the failed container is retained only when caller policy
///         opts in (the policy is enforced by the capture-delegate
///         implementation, not by the verifier).</item>
/// </list>
/// </para>
/// <para>
/// The actual container lifecycle (spin / tear down / retain on failure) is
/// driven by the injected
/// <see cref="OpenSearchSquashGenerationContext.CaptureSnapshotAsync"/> +
/// <see cref="CaptureFromGeneratedAsync"/> delegates; the verifier is policy-only.
/// </para>
/// </remarks>
public sealed class OpenSearchSquashVerifier : ISquashVerifier
{
    public string ProviderId => OpenSearchTopologySignature.ProviderIdValue;

    private readonly OpenSearchSnapshotCanonicalizer _canonicalizer;

    /// <summary>
    /// Captures a snapshot from a fresh cluster after applying the GENERATED
    /// squash content (not the historical migration range). The verifier
    /// requires this shape because the squash content itself is what's being
    /// verified. Tests inject a fake; production wires Testcontainers
    /// OpenSearch + apply-the-canonical-blob-then-recapture.
    /// </summary>
    public Func<string, OpenSearchTopologySignature, CancellationToken, Task<SnapshotCaptureResult>>? CaptureFromGeneratedAsync { get; init; }

    public OpenSearchSquashVerifier( OpenSearchSnapshotCanonicalizer canonicalizer )
    {
        _canonicalizer = canonicalizer ?? throw new ArgumentNullException( nameof( canonicalizer ) );
    }

    public async Task<VerificationResult> VerifyAsync(
        ISquashGenerationContext context,
        SquashGenerationResult.Generated generated,
        CancellationToken cancellationToken = default )
    {
        if ( context is not OpenSearchSquashGenerationContext osContext )
            return new VerificationResult.Failed(
                Detail: $"OpenSearchSquashVerifier requires OpenSearchSquashGenerationContext (received `{context?.GetType().Name ?? "<null>"}`).",
                DiffSummary: "" );

        if ( CaptureFromGeneratedAsync == null )
            return new VerificationResult.Failed(
                Detail:
                    "OpenSearchSquashVerifier requires CaptureFromGeneratedAsync to be wired by the caller. " +
                    "Production: Testcontainers + apply-canonical-blob-then-recapture. Tests: synthetic capture.",
                DiffSummary: "" );

        if ( generated == null )
            return new VerificationResult.Failed(
                Detail: "OpenSearchSquashVerifier requires a non-null Generated result.",
                DiffSummary: "" );

        if ( generated.Replaces == null || generated.Replaces.Count == 0 )
            return new VerificationResult.Failed(
                Detail: "Generated.Replaces is empty; cannot determine the upper-bound version to verify against.",
                DiffSummary: "" );

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var topology = (OpenSearchTopologySignature) generated.Topology;
            var upperBound = generated.Replaces[^1];

            // Capture A: re-apply the historical migration range up to the
            // squash's upper bound against a fresh ephemeral cluster.
            var snapshotA = await osContext.CaptureSnapshotAsync(
                new SnapshotCaptureRequest(
                    Label: "verifier-snapshot-A",
                    UpToVersion: upperBound,
                    RequiredTopology: topology ),
                cancellationToken ).ConfigureAwait( false );

            // Capture B: apply the GENERATED squash to a fresh container,
            // then capture its post-apply snapshot.
            var snapshotB = await CaptureFromGeneratedAsync(
                generated.Content, topology, cancellationToken ).ConfigureAwait( false );

            // Canonicalize both before compare so transient JSON-ordering /
            // ephemeral-field differences don't false-positive.
            var canonA = _canonicalizer.Canonicalize( snapshotA.SnapshotBlob );
            var canonB = _canonicalizer.Canonicalize( snapshotB.SnapshotBlob );

            stopwatch.Stop();

            if ( string.Equals( canonA, canonB, StringComparison.Ordinal ) )
                return new VerificationResult.Success( topology, stopwatch.Elapsed );

            var diff = SummarizeDiff( canonA, canonB );
            return new VerificationResult.Failed(
                Detail:
                    "Squash verification failed: snapshot from re-applying historical migrations does NOT byte-equal " +
                    "snapshot from applying the generated squash. The generated squash and the historical sequence are not equivalent. " +
                    "Inspect the diff summary; possible causes: canonicalizer's ephemeral-strip catalog is incomplete for a server feature, " +
                    "an apply path in the test harness diverges from the historical apply, or the squash codegen lost a " +
                    "structural object (template / index / policy / pipeline).",
                DiffSummary: diff );
        }
        catch ( OperationCanceledException )
        {
            throw;
        }
        catch ( Exception ex )
        {
            return new VerificationResult.Failed(
                Detail: $"OpenSearchSquashVerifier.VerifyAsync threw: {ex.Message}",
                DiffSummary: "",
                Cause: ex );
        }
    }

    /// <summary>
    /// Produce a compact human-readable summary of the line-by-line diff
    /// between two canonical snapshots. Shows the first few lines that differ
    /// in either direction; full diff is too large for log output.
    /// </summary>
    internal static string SummarizeDiff( string canonA, string canonB )
    {
        const int maxLines = 20;

        var aLines = canonA.Split( '\n' );
        var bLines = canonB.Split( '\n' );

        var aSet = new HashSet<string>( aLines, StringComparer.Ordinal );
        var bSet = new HashSet<string>( bLines, StringComparer.Ordinal );

        var onlyInA = aLines.Where( l => !bSet.Contains( l ) ).Take( maxLines ).ToArray();
        var onlyInB = bLines.Where( l => !aSet.Contains( l ) ).Take( maxLines ).ToArray();

        var sb = new StringBuilder();
        sb.AppendLine( $"snapshot A: {aLines.Length} lines, snapshot B: {bLines.Length} lines" );
        sb.AppendLine( "-- only in snapshot A (historical) --" );
        foreach ( var line in onlyInA )
            sb.Append( "  - " ).AppendLine( line );
        if ( onlyInA.Length == maxLines )
            sb.AppendLine( "  ... (truncated)" );

        sb.AppendLine( "-- only in snapshot B (generated squash) --" );
        foreach ( var line in onlyInB )
            sb.Append( "  + " ).AppendLine( line );
        if ( onlyInB.Length == maxLines )
            sb.AppendLine( "  ... (truncated)" );

        return sb.ToString();
    }
}
