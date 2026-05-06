using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 1 — Universal Ledger Scaffolding (provider-independent integrity tests)
//
// Cross-provider tests for MigrationRecord.Checksum + MigrationRecordKind:
//   - Pre-checksum-era rows (null Checksum, Kind=Migration default) read clean
//   - Kind=Squash with empty Replaces -> MigrationLedgerIntegrityException
//   - Kind=Migration with non-empty Replaces -> MigrationLedgerIntegrityException
//   - Default checksum strategy hashes definition deterministically
//
// Per-provider read+write integrity is exercised by each provider's integration
// test suite (Postgres, Aerospike, Couchbase, MongoDB, OpenSearch).
//
// ADR compliance: ADR-0021 + amendment A1 (Kind/Replaces consistency at write+read).

[TestClass]
public class LedgerIntegrityTests
{
    [TestMethod]
    public void PreChecksumEra_DefaultRow_ReadsClean()
    {
        // Pre-checksum-era row: only Id + RunOn populated. Defaults must be
        // Checksum=null, Kind=Migration, Replaces=empty. EnsureLedgerIntegrity
        // must accept it.
        var row = new MigrationRecord { Id = "Migration_1000" };

        row.Checksum.Should().BeNull();
        row.Kind.Should().Be( MigrationRecordKind.Migration );
        row.Replaces.Should().BeEmpty();

        var act = () => row.EnsureLedgerIntegrity();
        act.Should().NotThrow();
    }

    [TestMethod]
    public void KindSquash_WithEmptyReplaces_Throws()
    {
        var row = new MigrationRecord
        {
            Id = "Squash_2000",
            Kind = MigrationRecordKind.Squash,
            Replaces = []
        };

        var act = () => row.EnsureLedgerIntegrity();
        act.Should().Throw<MigrationLedgerIntegrityException>()
            .Where( ex => ex.RecordId == "Squash_2000" )
            .WithMessage( "*Kind=Squash*empty*" );
    }

    [TestMethod]
    public void KindMigration_WithNonEmptyReplaces_Throws()
    {
        var row = new MigrationRecord
        {
            Id = "Migration_1500",
            Kind = MigrationRecordKind.Migration,
            Replaces = [1000, 1010, 1020]
        };

        var act = () => row.EnsureLedgerIntegrity();
        act.Should().Throw<MigrationLedgerIntegrityException>()
            .Where( ex => ex.RecordId == "Migration_1500" )
            .WithMessage( "*Kind=Migration*non-empty*3 entries*" );
    }

    [TestMethod]
    public void KindSquash_WithReplaces_AcceptsClean()
    {
        var row = new MigrationRecord
        {
            Id = "Squash_2000",
            Kind = MigrationRecordKind.Squash,
            Replaces = [1000, 1010, 1020],
            Checksum = "abc123"
        };

        var act = () => row.EnsureLedgerIntegrity();
        act.Should().NotThrow();
    }

    [TestMethod]
    public void DefaultChecksumStrategy_IsDeterministic()
    {
        // Two computations against the same migration definition must produce
        // the same digest. C12 generation determinism gate per ADR-0019 depends
        // on this property holding at the strategy level.
        var strategy = new DefaultChecksumStrategy();
        var migration = new SampleMigration();
        var attr = new MigrationAttribute( 1234L );

        var hash1 = strategy.ComputeAsync( migration, attr ).GetAwaiter().GetResult();
        var hash2 = strategy.ComputeAsync( migration, attr ).GetAwaiter().GetResult();

        hash1.Should().NotBeNullOrWhiteSpace();
        hash1.Should().HaveLength( 64 ); // SHA-256 hex
        hash1.Should().Be( hash2 );
    }

    [TestMethod]
    public void DefaultChecksumStrategy_VersionAffectsDigest()
    {
        var strategy = new DefaultChecksumStrategy();
        var migration = new SampleMigration();

        var hashAt1000 = strategy.ComputeAsync( migration, new MigrationAttribute( 1000L ) ).GetAwaiter().GetResult();
        var hashAt2000 = strategy.ComputeAsync( migration, new MigrationAttribute( 2000L ) ).GetAwaiter().GetResult();

        hashAt1000.Should().NotBe( hashAt2000 );
    }

    private class SampleMigration : Migration
    {
        public override Task UpAsync( CancellationToken cancellationToken = default ) => Task.CompletedTask;
    }
}
