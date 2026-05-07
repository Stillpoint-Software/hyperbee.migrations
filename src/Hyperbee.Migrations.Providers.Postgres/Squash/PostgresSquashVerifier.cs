using System.Diagnostics;
using Hyperbee.Migrations.Squash;

namespace Hyperbee.Migrations.Providers.Postgres.Squash;

/// <summary>
/// Postgres squash verifier per ADR-0019 + amendment A4 (snapshot caching +
/// parallel A/B capture) and A18 (container lifecycle on failure).
/// </summary>
/// <remarks>
/// <para>
/// V1 verification round shape:
/// <list type="number">
///   <item>Capture snapshot A by applying migrations through the upper bound.
///         (This IS snapshot B from the generator's perspective; the
///         verifier re-applies to a fresh container to simulate the
///         "reproduction" path.)</item>
///   <item>Capture snapshot B by applying the GENERATED squash content to a
///         fresh container.</item>
///   <item>Canonicalize both via <see cref="PostgresSnapshotCanonicalizer"/>.</item>
///   <item>Byte-compare. Mismatch surfaces a <see cref="VerificationResult.Failed"/>
///         with a unified-diff-style summary; per A18 the failed container is
///         retained only when caller policy opts in.</item>
/// </list>
/// </para>
/// <para>
/// The actual container lifecycle (spin / tear down / retain on failure) is
/// driven by the injected
/// <see cref="PostgresSquashGenerationContext.CaptureSnapshotAsync"/>
/// delegate; the verifier is policy-only.
/// </para>
/// </remarks>
public sealed class PostgresSquashVerifier : ISquashVerifier
{
    public string ProviderId => PostgresTopologySignature.ProviderIdValue;

    private readonly PostgresSnapshotCanonicalizer _canonicalizer;

    /// <summary>
    /// Optional: capture snapshot from generated content rather than from
    /// re-applying the historical migrations. The verifier requires this
    /// shape because the squash content itself is what's being verified.
    /// Tests inject a fake; production wires Testcontainers.
    /// </summary>
    public Func<string, PostgresTopologySignature, CancellationToken, Task<SnapshotCaptureResult>> CaptureFromGeneratedAsync { get; init; }

    public PostgresSquashVerifier( PostgresSnapshotCanonicalizer canonicalizer )
    {
        _canonicalizer = canonicalizer ?? throw new ArgumentNullException( nameof( canonicalizer ) );
    }

    public async Task<VerificationResult> VerifyAsync(
        ISquashGenerationContext context,
        SquashGenerationResult.Generated generated,
        CancellationToken cancellationToken = default )
    {
        if ( context is not PostgresSquashGenerationContext pgContext )
            return new VerificationResult.Failed(
                $"PostgresSquashVerifier requires PostgresSquashGenerationContext (received `{context?.GetType().Name ?? "<null>"}`).",
                DiffSummary: "" );

        if ( CaptureFromGeneratedAsync == null )
            return new VerificationResult.Failed(
                "PostgresSquashVerifier requires CaptureFromGeneratedAsync to be wired by the caller. " +
                "Production: Testcontainers + docker exec pg_dump. Tests: synthetic capture.",
                DiffSummary: "" );

        var stopwatch = Stopwatch.StartNew();

        try
        {
            var topology = (PostgresTopologySignature) generated.Topology;

            // Capture A: re-apply the historical migration range (snapshot B
            // from the generator's view — historic state at the upper bound).
            var snapshotA = await pgContext.CaptureSnapshotAsync(
                new SnapshotCaptureRequest(
                    Label: "verifier-snapshot-A",
                    UpToVersion: generated.Replaces[^1],
                    RequiredTopology: topology ),
                cancellationToken ).ConfigureAwait( false );

            // Capture B: apply the GENERATED squash to a fresh container.
            var snapshotB = await CaptureFromGeneratedAsync(
                generated.Content, topology, cancellationToken ).ConfigureAwait( false );

            // Canonicalize both before compare so transient pg_dump
            // differences (preamble, comments) don't false-positive.
            var canonA = _canonicalizer.Canonicalize( snapshotA.DumpText );
            var canonB = _canonicalizer.Canonicalize( snapshotB.DumpText );

            stopwatch.Stop();

            if ( string.Equals( canonA, canonB, StringComparison.Ordinal ) )
            {
                return new VerificationResult.Success( topology, stopwatch.Elapsed );
            }

            var diff = SummarizeDiff( canonA, canonB );
            return new VerificationResult.Failed(
                Detail:
                    "Squash verification failed: snapshot from re-applying historical migrations does NOT byte-equal " +
                    "snapshot from applying the generated squash. The generated squash and the historical sequence are not equivalent. " +
                    "Inspect the diff summary; possible causes: classifier missed a statement, canonicalizer normalized differently, " +
                    "or the squash codegen lost a structural object.",
                DiffSummary: diff );
        }
        catch ( OperationCanceledException )
        {
            throw;
        }
        catch ( Exception ex )
        {
            return new VerificationResult.Failed(
                Detail: $"PostgresSquashVerifier.VerifyAsync threw: {ex.Message}",
                DiffSummary: "",
                Cause: ex );
        }
    }

    /// <summary>
    /// Produce a compact human-readable summary of the line-by-line diff
    /// between two canonical snapshots. Shows the first few lines that differ
    /// in either direction; full diff is too large for log output.
    /// </summary>
    private static string SummarizeDiff( string canonA, string canonB )
    {
        const int maxLines = 20;

        var aLines = canonA.Split( '\n' );
        var bLines = canonB.Split( '\n' );

        var aSet = new HashSet<string>( aLines, StringComparer.Ordinal );
        var bSet = new HashSet<string>( bLines, StringComparer.Ordinal );

        var onlyInA = aLines.Where( l => !bSet.Contains( l ) ).Take( maxLines ).ToArray();
        var onlyInB = bLines.Where( l => !aSet.Contains( l ) ).Take( maxLines ).ToArray();

        var sb = new System.Text.StringBuilder();
        sb.AppendLine( $"snapshot A: {aLines.Length} lines, snapshot B: {bLines.Length} lines" );
        sb.AppendLine( "-- only in snapshot A (historical) --" );
        foreach ( var line in onlyInA ) sb.Append( "  - " ).AppendLine( line );
        if ( onlyInA.Length == maxLines )
            sb.AppendLine( "  ... (truncated)" );

        sb.AppendLine( "-- only in snapshot B (generated squash) --" );
        foreach ( var line in onlyInB ) sb.Append( "  + " ).AppendLine( line );
        if ( onlyInB.Length == maxLines )
            sb.AppendLine( "  ... (truncated)" );

        return sb.ToString();
    }
}
