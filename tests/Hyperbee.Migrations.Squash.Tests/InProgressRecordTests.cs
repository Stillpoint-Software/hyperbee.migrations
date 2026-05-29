using FluentAssertions;
using Hyperbee.Migrations;
using Hyperbee.Migrations.Squash;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hyperbee.Migrations.Squash.Tests;

// ADR-0027: in-flight sentinel row helper.
//
// InProgressRecord derives the sentinel id from a migration record id and
// builds the sentinel row. Runner-side write/delete/pre-scan behavior is
// covered in RunnerTests; these tests pin the helper contract: deterministic
// id derivation, non-collision with real and recovery row ids, and Build
// producing a row that passes EnsureLedgerIntegrity.

[TestClass]
public class InProgressRecordTests
{
    [TestMethod]
    public void IdFor_IsDeterministic()
    {
        var a = InProgressRecord.IdFor( "1000.create_users" );
        var b = InProgressRecord.IdFor( "1000.create_users" );
        a.Should().Be( b );
    }

    [TestMethod]
    public void IdFor_DifferentRecordIdsProduceDifferentIds()
    {
        var one = InProgressRecord.IdFor( "1000.create_users" );
        var two = InProgressRecord.IdFor( "1010.seed_users" );
        one.Should().NotBe( two );
    }

    [TestMethod]
    public void IdFor_DoesNotCollideWithRealOrRecoveryIds()
    {
        // Real migration ids begin with the numeric version (ADR-0009); recovery
        // ids begin with "recovery." (ADR-0019). The sentinel prefix must avoid
        // both so a sentinel can never be mistaken for either.
        const string realId = "1000.create_users";
        var sentinelId = InProgressRecord.IdFor( realId );
        var recoveryId = RecoveryRecord.IdFor( 1000L, "prod" );

        sentinelId.Should().NotBe( realId );
        sentinelId.Should().NotBe( recoveryId );
        sentinelId.Should().NotStartWith( "recovery." );
        char.IsDigit( sentinelId[0] ).Should().BeFalse();
    }

    [TestMethod]
    public void IdFor_NullOrEmpty_Throws()
    {
        var actNull = () => InProgressRecord.IdFor( null! );
        var actEmpty = () => InProgressRecord.IdFor( "" );
        actNull.Should().Throw<ArgumentException>();
        actEmpty.Should().Throw<ArgumentException>();
    }

    [TestMethod]
    public void Build_ProducesRowWithKindInProgress_EmptyReplaces()
    {
        var row = InProgressRecord.Build( "1000.create_users" );

        row.Id.Should().Be( InProgressRecord.IdFor( "1000.create_users" ) );
        row.Kind.Should().Be( MigrationRecordKind.InProgress );
        row.Replaces.Should().BeEmpty();
    }

    [TestMethod]
    public void Build_RowPassesLedgerIntegrity()
    {
        var row = InProgressRecord.Build( "1000.create_users" );

        var act = () => row.EnsureLedgerIntegrity();
        act.Should().NotThrow();
    }
}
