namespace Hyperbee.Migrations.Squash;

/// <summary>
/// Caller-supplied options for an <see cref="ISquashStrategy.GenerateAsync"/>
/// invocation. Subset of CLI flags (per Phase 8 plan) that are relevant at
/// the strategy boundary; CLI-only knobs like output paths live elsewhere.
/// </summary>
public sealed record SquashGenerationOptions
{
    /// <summary>
    /// Optional inclusive lower bound. When set, the squash subsumes versions
    /// <c>&gt;= LowerBound</c> only. <c>null</c> means start at the earliest
    /// version in the supplied descriptor list.
    /// </summary>
    public long? LowerBound { get; init; }

    /// <summary>
    /// Optional inclusive upper bound. When set, the squash subsumes versions
    /// <c>&lt;= UpperBound</c> only. <c>null</c> means stop at the latest
    /// version in the supplied descriptor list.
    /// </summary>
    public long? UpperBound { get; init; }

    /// <summary>
    /// Stranding-acknowledgement: caller has externally verified that any
    /// migrations not subsumed (because they fall outside the bounds, fail
    /// classification, or otherwise can't be safely included) are intended
    /// to remain. Strategy-specific; consult per-provider docs.
    /// </summary>
    public IReadOnlyList<string> AcceptStranding { get; init; } = Array.Empty<string>();

    /// <summary>
    /// When true, the strategy is allowed to skip the verification round
    /// (per Phase 7). Default false. Per ADR-0019 amendment A1
    /// (--skip-verify deletion) the v1 squash CLI does NOT expose this
    /// flag; it remains on the strategy contract for testing harnesses
    /// only and is ignored by Postgres v1 production code.
    /// </summary>
    public bool SkipVerifyForTestingOnly { get; init; }
}
