using FluentAssertions;
using Hyperbee.Migrations.Providers.Couchbase.Squash;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 4 Task 4.2: CouchbaseDataOpClassifier unit coverage.
//
// Dual-shape pattern (matches Aerospike/OpenSearch/MongoDB precedent):
//   - statement form: CREATE/DROP {BUCKET,SCOPE,COLLECTION,INDEX,PRIMARY INDEX},
//     BUILD INDEX, ALTER INDEX -> structural; INSERT INTO / UPSERT INTO /
//     UPDATE / DELETE FROM / MERGE INTO -> data op.
//   - call-site form: dot-prefixed method names without receiver-name anchor
//     (Couchbase code routes through local collection/bucket/scope/cluster
//     vars). Default-deny captures .QueryAsync / .AnalyticsQueryAsync per
//     R-P3 OQ resolution.
//
// False-positive trade-off documented in the classifier docstring: a user
// class with its own UpsertAsync may match; [StructuralOnly] suppresses.

[TestClass]
public class CouchbaseDataOpClassifierTests
{
    private static readonly CouchbaseDataOpClassifier Classifier = new();

    // ---- statement-form: structural ----------------------------------------

    [TestMethod]
    public void Statement_CreateBucket_IsStructural()
    {
        var c = Classifier.Classify( "CREATE BUCKET myapp" );
        c.IsDataOp.Should().BeFalse();
        c.IsUnclassified.Should().BeFalse();
    }

    [TestMethod]
    public void Statement_CreateScope_IsStructural()
    {
        var c = Classifier.Classify( "CREATE SCOPE myapp.tenant1" );
        c.IsDataOp.Should().BeFalse();
    }

    [TestMethod]
    public void Statement_CreateCollection_IsStructural()
    {
        var c = Classifier.Classify( "CREATE COLLECTION myapp.tenant1.users" );
        c.IsDataOp.Should().BeFalse();
    }

    [TestMethod]
    public void Statement_CreatePrimaryIndex_IsStructural()
    {
        var c = Classifier.Classify( "CREATE PRIMARY INDEX ON myapp" );
        c.IsDataOp.Should().BeFalse();
    }

    [TestMethod]
    public void Statement_CreateIndex_IsStructural()
    {
        var c = Classifier.Classify( "CREATE INDEX idx_email ON myapp(email)" );
        c.IsDataOp.Should().BeFalse();
    }

    [TestMethod]
    public void Statement_DropBucket_IsStructural()
    {
        var c = Classifier.Classify( "DROP BUCKET myapp" );
        c.IsDataOp.Should().BeFalse();
    }

    [TestMethod]
    public void Statement_DropScope_IsStructural()
    {
        var c = Classifier.Classify( "DROP SCOPE myapp.tenant1" );
        c.IsDataOp.Should().BeFalse();
    }

    [TestMethod]
    public void Statement_DropCollection_IsStructural()
    {
        var c = Classifier.Classify( "DROP COLLECTION myapp.tenant1.users" );
        c.IsDataOp.Should().BeFalse();
    }

    [TestMethod]
    public void Statement_DropIndex_IsStructural()
    {
        var c = Classifier.Classify( "DROP INDEX myapp.idx_email" );
        c.IsDataOp.Should().BeFalse();
    }

    [TestMethod]
    public void Statement_DropPrimaryIndex_IsStructural()
    {
        var c = Classifier.Classify( "DROP PRIMARY INDEX ON myapp" );
        c.IsDataOp.Should().BeFalse();
    }

    [TestMethod]
    public void Statement_BuildIndex_IsStructural()
    {
        var c = Classifier.Classify( "BUILD INDEX ON myapp(idx_email)" );
        c.IsDataOp.Should().BeFalse();
    }

    [TestMethod]
    public void Statement_AlterIndex_IsStructural()
    {
        var c = Classifier.Classify( "ALTER INDEX myapp.idx_email WITH {\"action\":\"replica_count\",\"num_replica\":2}" );
        c.IsDataOp.Should().BeFalse();
    }

    [TestMethod]
    public void Statement_LeadingWhitespace_IsStructural()
    {
        var c = Classifier.Classify( "   CREATE INDEX idx_email ON myapp(email)" );
        c.IsDataOp.Should().BeFalse();
    }

    // ---- statement-form: data ops ------------------------------------------

    [TestMethod]
    public void Statement_InsertInto_IsDataOp()
    {
        var c = Classifier.Classify( "INSERT INTO myapp (KEY, VALUE) VALUES ('k1', {})" );
        c.IsDataOp.Should().BeTrue();
        c.RequiresPreservation.Should().BeTrue();
    }

    [TestMethod]
    public void Statement_UpsertInto_IsDataOp()
    {
        var c = Classifier.Classify( "UPSERT INTO myapp (KEY, VALUE) VALUES ('k1', {})" );
        c.IsDataOp.Should().BeTrue();
    }

    [TestMethod]
    public void Statement_Update_IsDataOp()
    {
        var c = Classifier.Classify( "UPDATE myapp SET status = 'active' WHERE type = 'user'" );
        c.IsDataOp.Should().BeTrue();
    }

    [TestMethod]
    public void Statement_DeleteFrom_IsDataOp()
    {
        var c = Classifier.Classify( "DELETE FROM myapp WHERE type = 'tombstone'" );
        c.IsDataOp.Should().BeTrue();
    }

    [TestMethod]
    public void Statement_MergeInto_IsDataOp()
    {
        var c = Classifier.Classify( "MERGE INTO myapp t USING source s ON KEY s.id WHEN MATCHED THEN UPDATE SET t.x = s.x" );
        c.IsDataOp.Should().BeTrue();
    }

    // ---- call-site: writes (data ops) --------------------------------------

    [TestMethod]
    public void CallSite_UpsertAsync_IsDataOp()
    {
        var c = Classifier.Classify( "await collection.UpsertAsync(\"id1\", doc)" );
        c.IsDataOp.Should().BeTrue();
    }

    [TestMethod]
    public void CallSite_InsertAsync_IsDataOp()
    {
        var c = Classifier.Classify( "await collection.InsertAsync(\"id1\", doc)" );
        c.IsDataOp.Should().BeTrue();
    }

    [TestMethod]
    public void CallSite_ReplaceAsync_IsDataOp()
    {
        var c = Classifier.Classify( "await collection.ReplaceAsync(\"id1\", doc)" );
        c.IsDataOp.Should().BeTrue();
    }

    [TestMethod]
    public void CallSite_RemoveAsync_IsDataOp()
    {
        var c = Classifier.Classify( "await collection.RemoveAsync(\"id1\")" );
        c.IsDataOp.Should().BeTrue();
    }

    [TestMethod]
    public void CallSite_MutateInAsync_IsDataOp()
    {
        var c = Classifier.Classify( "await collection.MutateInAsync(\"id1\", specs)" );
        c.IsDataOp.Should().BeTrue();
    }

    [TestMethod]
    public void CallSite_AppendAsync_IsDataOp()
    {
        var c = Classifier.Classify( "await collection.Binary.AppendAsync(\"id1\", bytes)" );
        c.IsDataOp.Should().BeTrue();
    }

    [TestMethod]
    public void CallSite_IncrementAsync_IsDataOp()
    {
        var c = Classifier.Classify( "await collection.Binary.IncrementAsync(\"counter1\")" );
        c.IsDataOp.Should().BeTrue();
    }

    [TestMethod]
    public void CallSite_GenericUpsertAsync_IsDataOp()
    {
        // The MethodCallTail allows an optional generic type-parameter list
        // before the opening paren. Couchbase SDK methods are typically
        // generic: `collection.UpsertAsync<MyDoc>(id, doc)`.
        var c = Classifier.Classify( "await collection.UpsertAsync<MyDoc>(\"id1\", doc)" );
        c.IsDataOp.Should().BeTrue();
    }

    // ---- call-site: reads --------------------------------------------------

    [TestMethod]
    public void CallSite_GetAsync_IsRead()
    {
        var c = Classifier.Classify( "var r = await collection.GetAsync(\"id1\")" );
        c.IsDataOp.Should().BeFalse();
        c.IsUnclassified.Should().BeFalse();
    }

    [TestMethod]
    public void CallSite_ExistsAsync_IsRead()
    {
        var c = Classifier.Classify( "var r = await collection.ExistsAsync(\"id1\")" );
        c.IsDataOp.Should().BeFalse();
        c.IsUnclassified.Should().BeFalse();
    }

    [TestMethod]
    public void CallSite_LookupInAsync_IsRead()
    {
        var c = Classifier.Classify( "var r = await collection.LookupInAsync(\"id1\", specs)" );
        c.IsDataOp.Should().BeFalse();
        c.IsUnclassified.Should().BeFalse();
    }

    [TestMethod]
    public void CallSite_GetAndLockAsync_IsRead()
    {
        var c = Classifier.Classify( "var r = await collection.GetAndLockAsync(\"id1\", TimeSpan.FromSeconds(1))" );
        c.IsDataOp.Should().BeFalse();
        c.IsUnclassified.Should().BeFalse();
    }

    [TestMethod]
    public void CallSite_FtsSearchAsync_IsRead()
    {
        var c = Classifier.Classify( "var r = await cluster.SearchAsync(\"index\", query)" );
        c.IsDataOp.Should().BeFalse();
        c.IsUnclassified.Should().BeFalse();
    }

    // ---- call-site: structural sub-client paths ----------------------------

    [TestMethod]
    public void CallSite_QueryIndexesCreate_IsStructural()
    {
        var c = Classifier.Classify( "await cluster.QueryIndexes.CreateIndexAsync(\"myapp\", \"idx\", new[]{\"email\"})" );
        c.IsDataOp.Should().BeFalse();
        c.IsUnclassified.Should().BeFalse();
    }

    [TestMethod]
    public void CallSite_BucketsCreate_IsStructural()
    {
        var c = Classifier.Classify( "await cluster.Buckets.CreateBucketAsync(settings)" );
        c.IsDataOp.Should().BeFalse();
        c.IsUnclassified.Should().BeFalse();
    }

    [TestMethod]
    public void CallSite_CollectionsCreate_IsStructural()
    {
        var c = Classifier.Classify( "await bucket.Collections.CreateCollectionAsync(spec)" );
        c.IsDataOp.Should().BeFalse();
        c.IsUnclassified.Should().BeFalse();
    }

    [TestMethod]
    public void CallSite_ScopesCreate_IsStructural()
    {
        var c = Classifier.Classify( "await bucket.Collections.CreateScopeAsync(\"tenant1\")" );
        c.IsDataOp.Should().BeFalse();
        c.IsUnclassified.Should().BeFalse();
    }

    [TestMethod]
    public void CallSite_SearchIndexesUpsert_IsStructural()
    {
        var c = Classifier.Classify( "await cluster.SearchIndexes.UpsertIndexAsync(definition)" );
        c.IsDataOp.Should().BeFalse();
        c.IsUnclassified.Should().BeFalse();
    }

    [TestMethod]
    public void CallSite_UsersUpsert_IsStructural()
    {
        var c = Classifier.Classify( "await cluster.Users.UpsertUserAsync(user)" );
        c.IsDataOp.Should().BeFalse();
        c.IsUnclassified.Should().BeFalse();
    }

    [TestMethod]
    public void CallSite_EventingFunctionsUpsert_IsStructural()
    {
        var c = Classifier.Classify( "await cluster.EventingFunctions.UpsertFunctionAsync(func)" );
        c.IsDataOp.Should().BeFalse();
        c.IsUnclassified.Should().BeFalse();
    }

    [TestMethod]
    public void CallSite_AnalyticsIndexesCreate_IsStructural()
    {
        var c = Classifier.Classify( "await cluster.AnalyticsIndexes.CreateDataverseAsync(\"my_ds\")" );
        c.IsDataOp.Should().BeFalse();
        c.IsUnclassified.Should().BeFalse();
    }

    // ---- R-P3 OQ resolution: parameterized N1QL default-deny --------------

    [TestMethod]
    public void CallSite_QueryAsync_IsDefaultDeny()
    {
        // Per R-P3 OQ resolution: .QueryAsync source-only inspection cannot
        // resolve the SQL value reliably. Falls through to default-deny so
        // the operator MUST annotate.
        var c = Classifier.Classify( "var r = await cluster.QueryAsync<MyDoc>(\"SELECT meta().id FROM myapp\")" );
        c.IsDataOp.Should().BeFalse();
        c.IsUnclassified.Should().BeTrue();
        c.RequiresAnnotation.Should().BeTrue();
    }

    [TestMethod]
    public void CallSite_AnalyticsQueryAsync_IsDefaultDeny()
    {
        var c = Classifier.Classify( "var r = await cluster.AnalyticsQueryAsync<MyDoc>(\"SELECT * FROM ds\")" );
        c.IsDataOp.Should().BeFalse();
        c.IsUnclassified.Should().BeTrue();
        c.RequiresAnnotation.Should().BeTrue();
    }

    // ---- default-deny + RequiresAnnotation ---------------------------------

    [TestMethod]
    public void Unknown_DefaultDeny_RequiresAnnotation()
    {
        var c = Classifier.Classify( "var x = 1 + 2;" );
        c.IsUnclassified.Should().BeTrue();
        c.RequiresAnnotation.Should().BeTrue();
        c.IsDataOp.Should().BeFalse();
    }

    [TestMethod]
    public void Empty_DefaultDeny()
    {
        var c = Classifier.Classify( "" );
        c.IsUnclassified.Should().BeTrue();
        c.RequiresAnnotation.Should().BeTrue();
    }

    [TestMethod]
    public void Null_Throws()
    {
        Action act = () => Classifier.Classify( null! );
        act.Should().Throw<ArgumentNullException>();
    }

    // ---- precedence: structural sub-client paths beat write verbs ----------

    [TestMethod]
    public void Precedence_QueryIndexesUpsertIndex_TreatedAsStructural()
    {
        // QueryIndexes.UpsertIndexAsync would match BOTH CallSiteStructural
        // AND CallSiteWrite (UpsertIndexAsync starts with "Upsert"). The
        // classifier evaluates Structural first per the documented order so
        // this is treated correctly.
        var c = Classifier.Classify( "await cluster.QueryIndexes.UpsertIndexAsync(\"myapp\", spec)" );
        c.IsDataOp.Should().BeFalse();
        c.IsUnclassified.Should().BeFalse();
    }

    // ---- non-determinism scan ---------------------------------------------

    [TestMethod]
    public void NonDeterminism_DateTimeNow_FlagsHint()
    {
        var c = Classifier.Classify( "await collection.UpsertAsync(\"k\", new { ts = DateTime.Now })" );
        c.IsDataOp.Should().BeTrue();
        c.RequiresAnnotation.Should().BeTrue();
        c.EmissionHint.Should().Contain( "DateTime.Now" );
    }

    [TestMethod]
    public void NonDeterminism_GuidNewGuid_FlagsHint()
    {
        var c = Classifier.Classify( "var id = Guid.NewGuid();" );
        c.EmissionHint.Should().Contain( "Guid.NewGuid" );
    }

    [TestMethod]
    public void NonDeterminism_NewRandomWithoutSeed_FlagsHint()
    {
        var c = Classifier.Classify( "var r = new Random();" );
        c.EmissionHint.Should().Contain( "Random" );
    }

    [TestMethod]
    public void NonDeterminism_RandomShared_FlagsHint()
    {
        var c = Classifier.Classify( "var n = Random.Shared.Next();" );
        c.EmissionHint.Should().Contain( "Random.Shared" );
    }

    [TestMethod]
    public void NonDeterminism_EnvironmentTickCount_FlagsHint()
    {
        var c = Classifier.Classify( "var t = Environment.TickCount64;" );
        c.EmissionHint.Should().Contain( "Environment.TickCount64" );
    }

    [TestMethod]
    public void NonDeterminism_StopwatchGetTimestamp_FlagsHint()
    {
        var c = Classifier.Classify( "var s = Stopwatch.GetTimestamp();" );
        c.EmissionHint.Should().Contain( "Stopwatch.GetTimestamp" );
    }

    [TestMethod]
    public void NonDeterminism_None_NoHint()
    {
        var c = Classifier.Classify( "CREATE INDEX idx ON myapp(email)" );
        c.EmissionHint.Should().BeNull();
        c.RequiresAnnotation.Should().BeFalse();
    }

    [TestMethod]
    public void ScanNonDeterminism_Helper_FindsMultipleHits()
    {
        var hits = CouchbaseDataOpClassifier.ScanNonDeterminism(
            "var x = DateTime.Now; var y = Guid.NewGuid();" );
        hits.Should().Contain( "DateTime.Now" );
        hits.Should().Contain( "Guid.NewGuid" );
    }

    [TestMethod]
    public void ScanNonDeterminism_Helper_EmptyInput_ReturnsEmpty()
    {
        CouchbaseDataOpClassifier.ScanNonDeterminism( "" ).Should().BeEmpty();
        CouchbaseDataOpClassifier.ScanNonDeterminism( null ).Should().BeEmpty();
    }
}
