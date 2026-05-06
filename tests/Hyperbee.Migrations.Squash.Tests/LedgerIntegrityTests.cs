using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 1 — Universal Ledger Scaffolding
//
// Cross-provider tests for MigrationRecord.Checksum + MigrationRecordKind:
//   - Pre-checksum-era rows (null Checksum, Kind=Migration default) read clean
//   - Kind=Squash with empty Replaces -> MigrationLedgerIntegrityException
//   - Kind=Migration with non-empty Replaces -> MigrationLedgerIntegrityException
//   - Default checksum strategy hashes resource bytes deterministically
//
// See plan: docs/plans/active/migration-squashing-v1.md, Phase 1 (Tasks 1.1-1.7).
//
// ADR compliance: ADR-0021 + amendment A1 (Kind/Replaces consistency at write+read).

[TestClass]
public class LedgerIntegrityTests
{
    [TestMethod]
    public void Phase1_Pending_PreChecksumEraRowsReadClean()
    {
        // TODO Phase 1 Task 1.2-1.6: per-provider record stores extended with Checksum + Kind
        // Pre-existing rows (null Checksum, Kind=Migration default) must continue to read.
        Assert.Inconclusive( "Phase 1 implementation pending — per-provider record store updates" );
    }

    [TestMethod]
    public void Phase1_Pending_KindSquashWithEmptyReplacesThrows()
    {
        // TODO Phase 1 Task 1.1: MigrationLedgerIntegrityException raised by record store
        // on write AND read when Kind=Squash but Replaces is null/empty.
        Assert.Inconclusive( "Phase 1 implementation pending — MigrationLedgerIntegrityException" );
    }

    [TestMethod]
    public void Phase1_Pending_KindMigrationWithNonEmptyReplacesThrows()
    {
        // TODO Phase 1 Task 1.1: same exception in the inverse case.
        Assert.Inconclusive( "Phase 1 implementation pending — MigrationLedgerIntegrityException" );
    }

    [TestMethod]
    public void Phase1_Pending_DefaultChecksumStrategyIsDeterministic()
    {
        // TODO Phase 1 Task 1.7: IChecksumStrategy<TMigration> default impl
        // hashes (sorted-by-name resource bytes) for resource migrations,
        // (type-name + version) for code-only migrations.
        Assert.Inconclusive( "Phase 1 implementation pending — IChecksumStrategy<TMigration>" );
    }
}
