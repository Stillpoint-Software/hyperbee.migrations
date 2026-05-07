namespace Hyperbee.Migrations.Squash;

/// <summary>
/// Outcome of an <see cref="ISquashStrategy.GenerateAsync"/> invocation.
/// Per ADR-0019 (A11) only two variants exist — <see cref="Generated"/> on
/// success, <see cref="Failed"/> on refusal. The earlier <c>Unsupported</c>
/// "hand-author" variant was removed in the destructive-model rework
/// (Assessment 0007 P0-11) because it papered over real provider gaps.
/// Providers that lack v1 codegen ship a <see cref="NullSquashStrategy"/>
/// returning <see cref="Failed"/> with a roadmap-pointing message.
/// </summary>
public abstract record SquashGenerationResult
{
    /// <summary>Successful squash generation.</summary>
    /// <param name="Content">Generated content (SQL, C#, JSON, or opaque bytes).</param>
    /// <param name="Kind">The shape of <see cref="Content"/>.</param>
    /// <param name="Encoding">Byte encoding for serialization (per ADR-0019 C12 determinism).</param>
    /// <param name="Replaces">Versions this generated squash subsumes; serialized into the squash row's <c>Replaces</c> array per ADR-0019.</param>
    /// <param name="Diagnostics">Per-statement diagnostics (warnings about non-determinism, classifier ambiguity, etc.).</param>
    /// <param name="Topology">Topology signature captured at generation time (per ADR-0019 A14 — bundled into the verification round's cache key).</param>
    public sealed record Generated(
        string Content,
        ContentKind Kind,
        ContentEncoding Encoding,
        IReadOnlyList<long> Replaces,
        IReadOnlyList<string> Diagnostics,
        ITopologySignature Topology
    ) : SquashGenerationResult;

    /// <summary>Generation refused or failed. Operators must read <see cref="Detail"/> to remediate.</summary>
    /// <param name="Detail">Human-readable refusal message — should name the specific provider, version, or condition.</param>
    /// <param name="Cause">Optional underlying exception, when the failure was triggered by an unhandled error.</param>
    public sealed record Failed( string Detail, Exception Cause = null ) : SquashGenerationResult;
}
