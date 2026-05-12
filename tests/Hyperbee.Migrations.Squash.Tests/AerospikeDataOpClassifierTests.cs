using FluentAssertions;
using Hyperbee.Migrations.Providers.Aerospike.Squash;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 1 (R-P1, R-P8): AerospikeDataOpClassifier unit coverage.
//
// Exercises both input shapes:
//   - statement form (per ADR-0022): CREATE INDEX, DROP INDEX, CREATE SET,
//     INSERT INTO, DELETE FROM
//   - .NET call-site form: _client.Put / Delete / Touch (write),
//     _client.Get / Exists / Query (read), Info.Request (structural),
//     _client.Operate (requires annotation)
//
// Plus the non-determinism scan for the .NET sources that vary per run.

[TestClass]
public class AerospikeDataOpClassifierTests
{
    private static readonly AerospikeDataOpClassifier Classifier = new();

    // statement-form

    [TestMethod]
    public void Statement_Insert_IsDataOp()
    {
        var c = Classifier.Classify( "INSERT INTO ns.set (key, name) VALUES ('k1', 'alice')" );

        c.IsDataOp.Should().BeTrue();
        c.RequiresPreservation.Should().BeTrue();
        c.IsUnclassified.Should().BeFalse();
    }

    [TestMethod]
    public void Statement_Delete_IsDataOp()
    {
        var c = Classifier.Classify( "DELETE FROM ns.set WHERE key = 'k1'" );

        c.IsDataOp.Should().BeTrue();
        c.RequiresPreservation.Should().BeTrue();
    }

    [TestMethod]
    public void Statement_CreateIndex_IsStructural()
    {
        var c = Classifier.Classify( "CREATE INDEX idx_name ON ns.set (name) NUMERIC" );

        c.IsDataOp.Should().BeFalse();
        c.IsUnclassified.Should().BeFalse();
        c.RequiresPreservation.Should().BeFalse();
    }

    [TestMethod]
    public void Statement_DropIndex_IsStructural()
    {
        var c = Classifier.Classify( "DROP INDEX ns.idx_name" );

        c.IsDataOp.Should().BeFalse();
        c.IsUnclassified.Should().BeFalse();
    }

    [TestMethod]
    public void Statement_CreateSet_IsStructural()
    {
        var c = Classifier.Classify( "CREATE SET ns.users" );

        c.IsDataOp.Should().BeFalse();
        c.IsUnclassified.Should().BeFalse();
    }

    // call-site form: writes

    [TestMethod]
    public void CallSite_ClientPut_IsDataOp()
    {
        var c = Classifier.Classify( "await _client.Put(policy, key, bin1, bin2)" );

        c.IsDataOp.Should().BeTrue();
        c.RequiresPreservation.Should().BeTrue();
    }

    [TestMethod]
    public void CallSite_ClientDelete_IsDataOp()
    {
        var c = Classifier.Classify( "await _client.Delete(policy, key)" );

        c.IsDataOp.Should().BeTrue();
    }

    [TestMethod]
    public void CallSite_ClientTouch_IsDataOp()
    {
        var c = Classifier.Classify( "await _client.Touch(policy, key)" );

        c.IsDataOp.Should().BeTrue();
    }

    // call-site form: reads

    [TestMethod]
    public void CallSite_ClientGet_NotDataOp()
    {
        var c = Classifier.Classify( "var record = await _client.Get(policy, key)" );

        c.IsDataOp.Should().BeFalse();
        c.IsUnclassified.Should().BeFalse();
    }

    [TestMethod]
    public void CallSite_ClientExists_NotDataOp()
    {
        var c = Classifier.Classify( "var exists = await _client.Exists(policy, key)" );

        c.IsDataOp.Should().BeFalse();
        c.IsUnclassified.Should().BeFalse();
    }

    [TestMethod]
    public void CallSite_ClientQuery_NotDataOp()
    {
        var c = Classifier.Classify( "_client.Query(policy, statement)" );

        c.IsDataOp.Should().BeFalse();
        c.IsUnclassified.Should().BeFalse();
    }

    // call-site form: structural management

    [TestMethod]
    public void CallSite_CreateIndex_IsStructural()
    {
        var c = Classifier.Classify( "_client.CreateIndex(null, ns, set, idx, bin, IndexType.STRING)" );

        c.IsDataOp.Should().BeFalse();
        c.IsUnclassified.Should().BeFalse();
    }

    [TestMethod]
    public void CallSite_InfoRequest_IsStructural()
    {
        var c = Classifier.Classify( "Info.Request(node, \"namespaces\")" );

        c.IsDataOp.Should().BeFalse();
        c.IsUnclassified.Should().BeFalse();
    }

    // call-site form: Operate requires annotation

    [TestMethod]
    public void CallSite_Operate_RequiresAnnotation()
    {
        var c = Classifier.Classify( "_client.Operate(policy, key, Operation.Put(bin), Operation.Get())" );

        c.IsUnclassified.Should().BeTrue();
        c.RequiresAnnotation.Should().BeTrue();
        c.EmissionHint.Should().Contain( "Operate" );
    }

    // default-deny

    [TestMethod]
    public void UnknownVerb_DefaultDeny()
    {
        var c = Classifier.Classify( "EXPLAIN ANALYZE foo" );

        c.IsUnclassified.Should().BeTrue();
        c.RequiresAnnotation.Should().BeTrue();
        c.IsDataOp.Should().BeFalse();
    }

    [TestMethod]
    public void EmptyInput_DefaultDeny()
    {
        var c = Classifier.Classify( "" );

        c.IsUnclassified.Should().BeTrue();
        c.RequiresAnnotation.Should().BeTrue();
    }

    // non-determinism

    [TestMethod]
    public void NonDeterminism_DateTimeUtcNow_Flagged()
    {
        var c = Classifier.Classify( "await _client.Put(policy, key, new Bin(\"ts\", DateTime.UtcNow))" );

        c.IsDataOp.Should().BeTrue();
        c.EmissionHint.Should().Contain( "DateTime.UtcNow" );
        c.RequiresAnnotation.Should().BeTrue();
    }

    [TestMethod]
    public void NonDeterminism_GuidNewGuid_Flagged()
    {
        var c = Classifier.Classify( "await _client.Put(policy, key, new Bin(\"id\", Guid.NewGuid()))" );

        c.IsDataOp.Should().BeTrue();
        c.EmissionHint.Should().Contain( "Guid.NewGuid" );
    }

    [TestMethod]
    public void NonDeterminism_NewRandomWithoutSeed_Flagged()
    {
        var c = Classifier.Classify( "var r = new Random(); await _client.Put(policy, key, new Bin(\"v\", r.Next()))" );

        c.IsDataOp.Should().BeTrue();
        c.EmissionHint.Should().Contain( "new Random" );
    }

    [TestMethod]
    public void NonDeterminism_NewRandomWithSeed_NotFlagged()
    {
        // Seeded Random is deterministic; the regex requires `new Random()`
        // with an empty argument list to fire.
        var c = Classifier.Classify( "var r = new Random(42); await _client.Put(policy, key, new Bin(\"v\", r.Next()))" );

        c.IsDataOp.Should().BeTrue();
        c.EmissionHint.Should().BeNull();
        c.RequiresAnnotation.Should().BeFalse();
    }

    [TestMethod]
    public void NonDeterminism_RandomShared_Flagged()
    {
        var c = Classifier.Classify( "await _client.Put(policy, key, new Bin(\"v\", Random.Shared.Next()))" );

        c.EmissionHint.Should().Contain( "Random.Shared" );
    }

    [TestMethod]
    public void NonDeterminism_EnvironmentTickCount_Flagged()
    {
        var c = Classifier.Classify( "await _client.Put(policy, key, new Bin(\"t\", Environment.TickCount))" );

        c.EmissionHint.Should().Contain( "Environment.TickCount" );
    }

    [TestMethod]
    public void NonDeterminism_OnStructural_StillFlagged()
    {
        // Non-determinism inside a structural call-site (e.g., an index name
        // computed at runtime) is also flagged -- the diagnostic surfaces in
        // EmissionHint regardless of the data-op verdict.
        var c = Classifier.Classify( "_client.CreateIndex(null, ns, set, $\"idx_{DateTime.UtcNow.Ticks}\", \"bin\", IndexType.STRING)" );

        c.IsDataOp.Should().BeFalse();
        c.EmissionHint.Should().Contain( "DateTime.UtcNow" );
    }

    [TestMethod]
    public void ScanNonDeterminism_MultipleHits_DedupedAndSorted()
    {
        var hits = AerospikeDataOpClassifier.ScanNonDeterminism(
            "DateTime.UtcNow; Guid.NewGuid(); DateTime.UtcNow; Random.Shared.Next()" );

        hits.Should().Equal( "DateTime.UtcNow", "Guid.NewGuid", "Random.Shared" );
    }

    [TestMethod]
    public void ScanNonDeterminism_EmptyInput_EmptyResult()
    {
        AerospikeDataOpClassifier.ScanNonDeterminism( "" ).Should().BeEmpty();
        AerospikeDataOpClassifier.ScanNonDeterminism( null! ).Should().BeEmpty();
    }
}
