using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 2 — MigrationAttribute + Discovery
//
// Tests for [Migration(version, Replaces=long[], ReplacesRange=string)]:
//   - Replaces array discovery
//   - ReplacesRange parsing ("1000-1500", "1000-1199, 1300, 1400-1450")
//   - Resolution against assembly's actual [Migration] versions in inclusive range
//   - Load-time validation: non-existent version, self-reference, duplicates
//   - MigrationApplyMode enum + MigrationContext.IsFreshInstall back-compat sugar
//
// See plan: docs/plans/active/migration-squashing-v1.md, Phase 2 (Tasks 2.1-2.4).
//
// ADR compliance: ADR-0019 (Replaces graph), ADR-0004 (reflection discovery).

[TestClass]
public class MigrationAttributeReplacesTests
{
    [TestMethod]
    public void Phase2_Pending_ReplacesArrayDiscovery()
    {
        // TODO Phase 2 Task 2.1: [Migration(2000, Replaces = new long[] { 1000, 1010 })]
        // discovers Replaces=[1000, 1010] in MigrationDescriptor.
        Assert.Inconclusive( "Phase 2 implementation pending" );
    }

    [TestMethod]
    public void Phase2_Pending_ReplacesRangeStringParsing()
    {
        // TODO Phase 2 Task 2.2: "1000-1500" -> [1000..1500] resolved against
        // assembly's actual [Migration] versions in that inclusive range.
        // Mixed: "1000-1199, 1300, 1400-1450".
        Assert.Inconclusive( "Phase 2 implementation pending" );
    }

    [TestMethod]
    public void Phase2_Pending_NonExistentVersionRaisesLoadException()
    {
        // TODO Phase 2 Task 2.3: Replaces=[9999] where 9999 is not a discovered
        // [Migration] -> MigrationLoadException at discovery.
        Assert.Inconclusive( "Phase 2 implementation pending" );
    }

    [TestMethod]
    public void Phase2_Pending_SelfReferenceRaisesLoadException()
    {
        // TODO Phase 2 Task 2.3: [Migration(2000, Replaces=new[]{2000,1000})] -> error
        Assert.Inconclusive( "Phase 2 implementation pending" );
    }

    [TestMethod]
    public void Phase2_Pending_MigrationApplyModeWiredToContext()
    {
        // TODO Phase 2 Task 2.4: MigrationContext.ApplyMode set before UpAsync
        // based on reconciliation classification (Fresh / PartialCatchUp).
        Assert.Inconclusive( "Phase 2 implementation pending" );
    }
}
