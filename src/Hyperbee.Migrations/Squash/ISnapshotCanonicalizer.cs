namespace Hyperbee.Migrations.Squash;

/// <summary>
/// Provider-supplied canonicalizer that normalizes a snapshot's bytes into a
/// deterministic form for the squash codegen + verification round (per
/// ADR-0019 + consensus C2/C12).
/// </summary>
/// <remarks>
/// <para>
/// The canonicalizer's role is twofold: (a) at generation time, normalize
/// the captured snapshot so the emitted squash is byte-stable across runs;
/// (b) at verification time, normalize both snapshot A (residual head) and
/// snapshot B (squash applied) so a byte-compare is a meaningful test of
/// equivalence.
/// </para>
/// <para>
/// Postgres v1 implementation strips the <c>SET</c> preamble, normalizes
/// dollar-tag function bodies, extracts <c>CREATE EXTENSION</c> to a
/// prerequisites file, normalizes line endings, etc.
/// </para>
/// </remarks>
public interface ISnapshotCanonicalizer
{
    /// <summary>Provider id matching the strategy's topology signature.</summary>
    string ProviderId { get; }

    /// <summary>
    /// Canonicalize the supplied snapshot bytes. The result must be
    /// deterministic: <c>Canonicalize(b) == Canonicalize(Canonicalize(b))</c>.
    /// </summary>
    string Canonicalize( string snapshot );

    /// <summary>
    /// Emit canonical script-form output of the squash content for embedding
    /// into the generated migration's <c>.pql</c> / <c>.sql</c> resource
    /// file. Must be byte-stable for the determinism gate.
    /// </summary>
    string EmitScript( string canonicalContent );
}
