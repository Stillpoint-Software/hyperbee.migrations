using FluentAssertions;
using Hyperbee.Migrations;
using Hyperbee.Migrations.Squash;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hyperbee.Migrations.Squash.Tests;

// RB-2: persisted mid-range recovery acknowledgement row.
//
// RecoveryRecord builds the row + verifies it later. The runner-side
// detection that consumes the row is covered in ReconciliationTests
// (RB2_ValidRecoveryRow_AutoMarksTheSquash); these tests are scoped to
// the helper contract: id derivation, Build validation, and IsValidFor's
// rejection of stale rows.

[TestClass]
public class RecoveryRecordTests
{
    [TestMethod]
    public void IdFor_IsDeterministic()
    {
        var a = RecoveryRecord.IdFor( 2099L, "prod-eu-1" );
        var b = RecoveryRecord.IdFor( 2099L, "prod-eu-1" );
        a.Should().Be( b );
    }

    [TestMethod]
    public void IdFor_SlugsNonAlphanumericEnvName()
    {
        var id = RecoveryRecord.IdFor( 2099L, "Prod EU-1 / Region A" );
        id.Should().Be( "recovery.from-mid-range.2099.prod_eu_1___region_a" );
    }

    [TestMethod]
    public void IdFor_DifferentEnvsProduceDifferentIds()
    {
        var prod = RecoveryRecord.IdFor( 2099L, "prod" );
        var staging = RecoveryRecord.IdFor( 2099L, "staging" );
        prod.Should().NotBe( staging );
    }

    [TestMethod]
    public void Build_HappyPath_ProducesRowWithKindRecovery()
    {
        var row = RecoveryRecord.Build( 2099L, "prod", new[] { 2003L, 2001L, 2002L } );

        row.Id.Should().Be( "recovery.from-mid-range.2099.prod" );
        row.Kind.Should().Be( MigrationRecordKind.Recovery );
        row.Replaces.Should().BeEquivalentTo( new[] { 2001L, 2002L, 2003L },
            opts => opts.WithStrictOrdering(),
            "Build sorts and dedupes the missing-versions list" );
        row.Checksum.Should().HaveLength( 12 );
    }

    [TestMethod]
    public void Build_TokenMatchesRecoveryAcknowledgement_ComputeToken()
    {
        var row = RecoveryRecord.Build( 2099L, "prod", new[] { 2001L, 2002L, 2003L } );

        var expected = RecoveryAcknowledgement.ComputeToken( "prod", 2099L, new[] { 2001L, 2002L, 2003L } );
        row.Checksum.Should().Be( expected );
    }

    [TestMethod]
    public void Build_EnsureLedgerIntegrity_PassesOnRecoveryRow()
    {
        var row = RecoveryRecord.Build( 2099L, "prod", new[] { 2003L } );
        Action act = () => row.EnsureLedgerIntegrity();
        act.Should().NotThrow();
    }

    [TestMethod]
    public void Build_NullEnvironment_Throws()
    {
        Action act = () => RecoveryRecord.Build( 2099L, null!, new[] { 2003L } );
        act.Should().Throw<ArgumentException>().WithMessage( "*environmentName*" );
    }

    [TestMethod]
    public void Build_WhitespaceEnvironment_Throws()
    {
        Action act = () => RecoveryRecord.Build( 2099L, "   ", new[] { 2003L } );
        act.Should().Throw<ArgumentException>().WithMessage( "*environmentName*" );
    }

    [TestMethod]
    public void Build_EmptyMissing_Throws()
    {
        // Integrity rule rejects Recovery rows with empty Replaces.
        Action act = () => RecoveryRecord.Build( 2099L, "prod", Array.Empty<long>() );
        act.Should().Throw<ArgumentException>().WithMessage( "*at least one version*" );
    }

    [TestMethod]
    public void IsValidFor_ExactMatch_True()
    {
        var row = RecoveryRecord.Build( 2099L, "prod", new[] { 2001L, 2002L, 2003L } );
        var ok = RecoveryRecord.IsValidFor( row, 2099L, "prod", new[] { 2001L, 2002L, 2003L } );
        ok.Should().BeTrue();
    }

    [TestMethod]
    public void IsValidFor_OrderIndependent_True()
    {
        // Set-equality: order in the missing list doesn't change validity.
        var row = RecoveryRecord.Build( 2099L, "prod", new[] { 2001L, 2002L, 2003L } );
        var ok = RecoveryRecord.IsValidFor( row, 2099L, "prod", new[] { 2003L, 2002L, 2001L } );
        ok.Should().BeTrue();
    }

    [TestMethod]
    public void IsValidFor_EnvMismatch_False()
    {
        var row = RecoveryRecord.Build( 2099L, "prod", new[] { 2001L, 2002L, 2003L } );
        // Persisted row was for prod; verifying against staging recomputes a
        // different token.
        var ok = RecoveryRecord.IsValidFor( row, 2099L, "staging", new[] { 2001L, 2002L, 2003L } );
        ok.Should().BeFalse();
    }

    [TestMethod]
    public void IsValidFor_MissingSetDrift_False()
    {
        // The set of missing versions has grown (a non-journaled migration
        // was retroactively added). The persisted token doesn't match the
        // new set, so the row is no longer valid.
        var row = RecoveryRecord.Build( 2099L, "prod", new[] { 2001L, 2002L, 2003L } );
        var ok = RecoveryRecord.IsValidFor( row, 2099L, "prod", new[] { 2001L, 2002L, 2003L, 2004L } );
        ok.Should().BeFalse();
    }

    [TestMethod]
    public void IsValidFor_SquashVersionMismatch_False()
    {
        var row = RecoveryRecord.Build( 2099L, "prod", new[] { 2001L, 2002L, 2003L } );
        var ok = RecoveryRecord.IsValidFor( row, 2199L, "prod", new[] { 2001L, 2002L, 2003L } );
        ok.Should().BeFalse();
    }

    [TestMethod]
    public void IsValidFor_NonRecoveryKind_False()
    {
        // Defensive: a row at the recovery id with the wrong Kind is not
        // a valid recovery acknowledgement (the runner refuses to consume
        // it as one).
        var fakeRow = new MigrationRecord
        {
            Id = RecoveryRecord.IdFor( 2099L, "prod" ),
            Kind = MigrationRecordKind.Squash,
            Replaces = new[] { 2001L, 2002L, 2003L },
            Checksum = "deadbeefcafe"
        };
        var ok = RecoveryRecord.IsValidFor( fakeRow, 2099L, "prod", new[] { 2001L, 2002L, 2003L } );
        ok.Should().BeFalse();
    }

    [TestMethod]
    public void IsValidFor_NullRow_False()
    {
        var ok = RecoveryRecord.IsValidFor( null!, 2099L, "prod", new[] { 2001L, 2002L, 2003L } );
        ok.Should().BeFalse();
    }
}
