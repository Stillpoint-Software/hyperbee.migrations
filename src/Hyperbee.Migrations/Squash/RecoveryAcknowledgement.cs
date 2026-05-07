using System.Globalization;
using System.Security.Cryptography;
using System.Text;

namespace Hyperbee.Migrations.Squash;

/// <summary>
/// Deterministic acknowledgement token for the
/// <c>recover from-mid-range</c> escape hatch (per ADR-0019 A3).
/// </summary>
/// <remarks>
/// <para>
/// The mid-range recovery path is the third recovery option named in
/// <see cref="MidRangeSquashException"/> — used only when the operator has
/// externally verified that the live data state already matches the squashed
/// schema. To prevent accidental invocation, the CLI requires the operator
/// to compute and supply a deterministic token derived from
/// <c>(env-name, squash-version, missing-versions-set)</c>; the runner
/// recomputes it and refuses unless they match.
/// </para>
/// <para>
/// Deterministic per (env, squash, gap) means: retrying the recovery against
/// the same inputs reproduces the same token, so the operator can re-run
/// safely without regenerating it. Different envs / different missing-version
/// sets produce different tokens, so an accidental copy-paste from a sibling
/// env is rejected.
/// </para>
/// </remarks>
public static class RecoveryAcknowledgement
{
    /// <summary>
    /// Computes the recovery token: SHA-256 over
    /// <c>env-name '|' squash-version '|' sorted-missing-versions</c>,
    /// truncated to 12 hex chars (48 bits). Lowercase output.
    /// </summary>
    public static string ComputeToken(
        string environmentName,
        long squashVersion,
        IEnumerable<long> missingVersions )
    {
        if ( string.IsNullOrWhiteSpace( environmentName ) )
            throw new ArgumentException( "environmentName is required.", nameof( environmentName ) );
        if ( missingVersions == null )
            throw new ArgumentNullException( nameof( missingVersions ) );

        var sortedMissing = missingVersions
            .Distinct()
            .OrderBy( v => v )
            .Select( v => v.ToString( CultureInfo.InvariantCulture ) );

        var canonical =
            environmentName + "|" +
            squashVersion.ToString( CultureInfo.InvariantCulture ) + "|" +
            string.Join( ",", sortedMissing );

        var bytes = Encoding.UTF8.GetBytes( canonical );
        var digest = SHA256.HashData( bytes );

        // 6 bytes = 12 hex chars = 48 bits of entropy. Enough to defeat
        // accidental copy-paste from a sibling env; not a secret.
        return Convert.ToHexString( digest, 0, 6 ).ToLowerInvariant();
    }

    /// <summary>
    /// Verifies that <paramref name="suppliedToken"/> matches the token derived
    /// from the supplied (env, squash, missing-versions). Returns true on
    /// match. Comparison is case-insensitive ordinal — operators may type the
    /// token from a printed copy.
    /// </summary>
    public static bool Verify(
        string environmentName,
        long squashVersion,
        IEnumerable<long> missingVersions,
        string suppliedToken )
    {
        if ( string.IsNullOrWhiteSpace( suppliedToken ) )
            return false;

        var expected = ComputeToken( environmentName, squashVersion, missingVersions );
        return string.Equals( expected, suppliedToken.Trim(), StringComparison.OrdinalIgnoreCase );
    }
}
