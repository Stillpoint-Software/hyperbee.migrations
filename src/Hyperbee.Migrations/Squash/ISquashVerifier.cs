namespace Hyperbee.Migrations.Squash;

/// <summary>
/// Provider-supplied verifier that runs the squash verification round per
/// ADR-0019 (especially A4 — snapshot caching + parallel A/B capture, A18 —
/// container lifecycle on failure).
/// </summary>
/// <remarks>
/// The v1 verifier shape: spin two ephemeral provider containers; apply the
/// residual-head migrations to A, apply the squash to B; capture canonical
/// snapshots from both via the <see cref="ISnapshotCanonicalizer"/>; assert
/// byte-equality. On mismatch the verifier surfaces a
/// <see cref="VerificationResult.Failed"/> with the diff and (per A18)
/// preserves the failed container only when <c>--keep-failed-container</c>
/// is set.
/// </remarks>
public interface ISquashVerifier
{
    /// <summary>Provider id matching the strategy's topology signature.</summary>
    string ProviderId { get; }

    /// <summary>
    /// Verify the supplied <paramref name="generated"/> squash by running the
    /// snapshot A vs snapshot B equality check.
    /// </summary>
    Task<VerificationResult> VerifyAsync(
        ISquashGenerationContext context,
        SquashGenerationResult.Generated generated,
        CancellationToken cancellationToken = default );
}

/// <summary>Outcome of a verification round.</summary>
public abstract record VerificationResult
{
    public sealed record Success( ITopologySignature Topology, TimeSpan Elapsed ) : VerificationResult;
    public sealed record Failed( string Detail, string DiffSummary, Exception Cause = null ) : VerificationResult;
}
