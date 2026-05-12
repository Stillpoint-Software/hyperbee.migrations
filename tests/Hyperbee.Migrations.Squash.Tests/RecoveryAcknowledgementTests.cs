using FluentAssertions;
using Hyperbee.Migrations.Squash;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hyperbee.Migrations.Squash.Tests;

// Coverage for the recovery-token escape hatch (ADR-0019 A3). The token is
// the gate on the `recover from-mid-range` CLI verb -- the runner refuses
// to bypass MidRangeSquashException unless the operator-supplied token
// matches a recomputed (env, squash, missing-versions) hash. These tests
// pin the contract: determinism, sibling-env rejection, missing-version-
// set sensitivity, case-insensitive ordinal compare, null/empty rejection.

[TestClass]
public class RecoveryAcknowledgementTests
{
    [TestMethod]
    public void Compute_SameInputs_ProducesSameToken()
    {
        var t1 = RecoveryAcknowledgement.ComputeToken( "prod", 2000, new long[] { 1100, 1200, 1300 } );
        var t2 = RecoveryAcknowledgement.ComputeToken( "prod", 2000, new long[] { 1100, 1200, 1300 } );
        t1.Should().Be( t2 );
    }

    [TestMethod]
    public void Compute_Token_Is_12_LowerHex_Chars()
    {
        var token = RecoveryAcknowledgement.ComputeToken( "prod", 2000, new long[] { 1100 } );
        token.Should().HaveLength( 12 );
        token.Should().MatchRegex( "^[a-f0-9]{12}$" );
    }

    [TestMethod]
    public void Compute_MissingVersionsOrder_DoesNotChangeToken()
    {
        // Order of input missing-versions must not affect output: the
        // implementation sorts before hashing.
        var t1 = RecoveryAcknowledgement.ComputeToken( "prod", 2000, new long[] { 1300, 1100, 1200 } );
        var t2 = RecoveryAcknowledgement.ComputeToken( "prod", 2000, new long[] { 1100, 1200, 1300 } );
        t1.Should().Be( t2 );
    }

    [TestMethod]
    public void Compute_MissingVersionDuplicates_AreCollapsed()
    {
        var t1 = RecoveryAcknowledgement.ComputeToken( "prod", 2000, new long[] { 1100, 1100, 1200, 1200 } );
        var t2 = RecoveryAcknowledgement.ComputeToken( "prod", 2000, new long[] { 1100, 1200 } );
        t1.Should().Be( t2 );
    }

    [TestMethod]
    public void Compute_DifferentEnv_ProducesDifferentToken()
    {
        // The whole point: a token computed for `staging` must not validate
        // against `prod` (prevents accidental copy-paste from a sibling env).
        var prod = RecoveryAcknowledgement.ComputeToken( "prod", 2000, new long[] { 1100 } );
        var staging = RecoveryAcknowledgement.ComputeToken( "staging", 2000, new long[] { 1100 } );
        prod.Should().NotBe( staging );
    }

    [TestMethod]
    public void Compute_DifferentSquashVersion_ProducesDifferentToken()
    {
        var v2000 = RecoveryAcknowledgement.ComputeToken( "prod", 2000, new long[] { 1100 } );
        var v3000 = RecoveryAcknowledgement.ComputeToken( "prod", 3000, new long[] { 1100 } );
        v2000.Should().NotBe( v3000 );
    }

    [TestMethod]
    public void Compute_DifferentMissingVersions_ProducesDifferentToken()
    {
        var a = RecoveryAcknowledgement.ComputeToken( "prod", 2000, new long[] { 1100 } );
        var b = RecoveryAcknowledgement.ComputeToken( "prod", 2000, new long[] { 1200 } );
        a.Should().NotBe( b );
    }

    [TestMethod]
    public void Compute_EmptyMissingVersions_StillProducesToken()
    {
        // A squash with an empty missing-versions set is degenerate but
        // legal; the token should still compute (rather than throw).
        var token = RecoveryAcknowledgement.ComputeToken( "prod", 2000, Array.Empty<long>() );
        token.Should().HaveLength( 12 );
    }

    [TestMethod]
    public void Compute_EmptyEnv_Throws()
    {
        Action act = () => RecoveryAcknowledgement.ComputeToken( "", 2000, new long[] { 1100 } );
        act.Should().Throw<ArgumentException>().WithParameterName( "environmentName" );
    }

    [TestMethod]
    public void Compute_NullEnv_Throws()
    {
        Action act = () => RecoveryAcknowledgement.ComputeToken( null!, 2000, new long[] { 1100 } );
        act.Should().Throw<ArgumentException>().WithParameterName( "environmentName" );
    }

    [TestMethod]
    public void Compute_NullMissingVersions_Throws()
    {
        Action act = () => RecoveryAcknowledgement.ComputeToken( "prod", 2000, null! );
        act.Should().Throw<ArgumentNullException>().WithParameterName( "missingVersions" );
    }

    // ---- Verify ------------------------------------------------------------

    [TestMethod]
    public void Verify_MatchingToken_ReturnsTrue()
    {
        var token = RecoveryAcknowledgement.ComputeToken( "prod", 2000, new long[] { 1100, 1200 } );
        RecoveryAcknowledgement.Verify( "prod", 2000, new long[] { 1100, 1200 }, token ).Should().BeTrue();
    }

    [TestMethod]
    public void Verify_MismatchedToken_ReturnsFalse()
    {
        RecoveryAcknowledgement.Verify( "prod", 2000, new long[] { 1100 }, "deadbeefcafe" ).Should().BeFalse();
    }

    [TestMethod]
    public void Verify_NullToken_ReturnsFalse()
    {
        RecoveryAcknowledgement.Verify( "prod", 2000, new long[] { 1100 }, null! ).Should().BeFalse();
    }

    [TestMethod]
    public void Verify_EmptyToken_ReturnsFalse()
    {
        RecoveryAcknowledgement.Verify( "prod", 2000, new long[] { 1100 }, "" ).Should().BeFalse();
    }

    [TestMethod]
    public void Verify_WhitespaceToken_ReturnsFalse()
    {
        RecoveryAcknowledgement.Verify( "prod", 2000, new long[] { 1100 }, "   " ).Should().BeFalse();
    }

    [TestMethod]
    public void Verify_TokenWithSurroundingWhitespace_TrimmedAndAccepted()
    {
        // Operators may copy the token with trailing newline/whitespace
        // from a CLI message. Trim before compare.
        var token = RecoveryAcknowledgement.ComputeToken( "prod", 2000, new long[] { 1100 } );
        RecoveryAcknowledgement.Verify( "prod", 2000, new long[] { 1100 }, "  " + token + "\n" ).Should().BeTrue();
    }

    [TestMethod]
    public void Verify_TokenCaseInsensitive_Accepted()
    {
        // Operators may type the token from a printed copy with arbitrary
        // casing. Case-insensitive ordinal compare per the docstring.
        var token = RecoveryAcknowledgement.ComputeToken( "prod", 2000, new long[] { 1100 } );
        RecoveryAcknowledgement.Verify( "prod", 2000, new long[] { 1100 }, token.ToUpperInvariant() ).Should().BeTrue();
    }

    [TestMethod]
    public void Verify_TokenFromSiblingEnv_Rejected()
    {
        // The defense-in-depth payoff: token from staging must NOT validate
        // against prod even if everything else matches.
        var stagingToken = RecoveryAcknowledgement.ComputeToken( "staging", 2000, new long[] { 1100 } );
        RecoveryAcknowledgement.Verify( "prod", 2000, new long[] { 1100 }, stagingToken ).Should().BeFalse();
    }

    [TestMethod]
    public void Verify_TokenForDifferentMissingVersionSet_Rejected()
    {
        // Even with same env + squash, a different missing-versions set
        // produces a different token. The operator must compute it for the
        // specific gap they intend to bypass.
        var tokenForGap1 = RecoveryAcknowledgement.ComputeToken( "prod", 2000, new long[] { 1100 } );
        RecoveryAcknowledgement.Verify( "prod", 2000, new long[] { 1200 }, tokenForGap1 ).Should().BeFalse();
    }
}
