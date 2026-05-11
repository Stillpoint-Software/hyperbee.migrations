using FluentAssertions;
using Hyperbee.Migrations.Providers.MongoDB.Squash;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 3 Task 3.2: MongoDBDataOpClassifier unit coverage.
//
// Same dual-shape pattern as Aerospike + OpenSearch:
//   - statement form: CREATE/DROP COLLECTION, CREATE/UNIQUE/DROP INDEX
//     (structural); INSERT INTO (data op)
//   - call-site form: dot-prefixed method names without receiver-name
//     anchor (MongoDB code routes through local `collection`/`db` vars,
//     not the client directly)
//
// False-positive trade-off documented in the classifier docstring: user
// classes with their own InsertOne method may match. The default-deny
// posture means operators annotate to suppress.

[TestClass]
public class MongoDBDataOpClassifierTests
{
    private static readonly MongoDBDataOpClassifier Classifier = new();

    // ---- statement-form: structural ----------------------------------------

    [TestMethod]
    public void Statement_CreateCollection_IsStructural()
    {
        var c = Classifier.Classify( "CREATE COLLECTION mydb.users" );
        c.IsDataOp.Should().BeFalse();
        c.IsUnclassified.Should().BeFalse();
    }

    [TestMethod]
    public void Statement_DropCollection_IsStructural()
    {
        var c = Classifier.Classify( "DROP COLLECTION mydb.users" );
        c.IsDataOp.Should().BeFalse();
    }

    [TestMethod]
    public void Statement_CreateIndex_IsStructural()
    {
        var c = Classifier.Classify( "CREATE INDEX idx_email ON mydb.users(email)" );
        c.IsDataOp.Should().BeFalse();
    }

    [TestMethod]
    public void Statement_CreateUniqueIndex_IsStructural()
    {
        var c = Classifier.Classify( "CREATE UNIQUE INDEX idx_email ON mydb.users(email)" );
        c.IsDataOp.Should().BeFalse();
        c.IsUnclassified.Should().BeFalse();
    }

    [TestMethod]
    public void Statement_DropIndex_IsStructural()
    {
        var c = Classifier.Classify( "DROP INDEX mydb.users.idx_email" );
        c.IsDataOp.Should().BeFalse();
    }

    // ---- statement-form: data ops ------------------------------------------

    [TestMethod]
    public void Statement_InsertInto_IsDataOp()
    {
        var c = Classifier.Classify( "INSERT INTO mydb.users VALUES ('alice')" );
        c.IsDataOp.Should().BeTrue();
        c.RequiresPreservation.Should().BeTrue();
    }

    // ---- call-site form: data ops ------------------------------------------

    [TestMethod]
    public void CallSite_InsertOneAsync_IsDataOp()
    {
        var c = Classifier.Classify( "await collection.InsertOneAsync(doc)" );
        c.IsDataOp.Should().BeTrue();
        c.RequiresPreservation.Should().BeTrue();
    }

    [TestMethod]
    public void CallSite_InsertManyAsync_IsDataOp()
    {
        var c = Classifier.Classify( "await collection.InsertManyAsync(docs)" );
        c.IsDataOp.Should().BeTrue();
    }

    [TestMethod]
    public void CallSite_InsertOne_Generic_IsDataOp()
    {
        // MongoDB.Driver methods are often generic; the MethodCallTail
        // pattern must allow optional generic type parameters before the
        // opening paren.
        var c = Classifier.Classify( "collection.InsertOne<UserDoc>(doc)" );
        c.IsDataOp.Should().BeTrue();
    }

    [TestMethod]
    public void CallSite_UpdateManyAsync_IsDataOp()
    {
        var c = Classifier.Classify( "await collection.UpdateManyAsync(filter, update)" );
        c.IsDataOp.Should().BeTrue();
    }

    [TestMethod]
    public void CallSite_ReplaceOneAsync_IsDataOp()
    {
        var c = Classifier.Classify( "await collection.ReplaceOneAsync(filter, doc)" );
        c.IsDataOp.Should().BeTrue();
    }

    [TestMethod]
    public void CallSite_DeleteManyAsync_IsDataOp()
    {
        var c = Classifier.Classify( "await collection.DeleteManyAsync(filter)" );
        c.IsDataOp.Should().BeTrue();
    }

    [TestMethod]
    public void CallSite_FindOneAndUpdateAsync_IsDataOp()
    {
        var c = Classifier.Classify( "await collection.FindOneAndUpdateAsync(filter, update)" );
        c.IsDataOp.Should().BeTrue();
    }

    [TestMethod]
    public void CallSite_BulkWriteAsync_IsDataOp()
    {
        var c = Classifier.Classify( "await collection.BulkWriteAsync(operations)" );
        c.IsDataOp.Should().BeTrue();
    }

    // ---- call-site form: reads ---------------------------------------------

    [TestMethod]
    public void CallSite_FindAsync_NotDataOp()
    {
        var c = Classifier.Classify( "var cursor = await collection.FindAsync(filter)" );
        c.IsDataOp.Should().BeFalse();
        c.IsUnclassified.Should().BeFalse();
    }

    [TestMethod]
    public void CallSite_CountDocumentsAsync_NotDataOp()
    {
        var c = Classifier.Classify( "var n = await collection.CountDocumentsAsync(filter)" );
        c.IsDataOp.Should().BeFalse();
        c.IsUnclassified.Should().BeFalse();
    }

    [TestMethod]
    public void CallSite_AggregateAsync_NotDataOp()
    {
        // Aggregate is mostly read; pipelines with $out / $merge stages
        // can be data ops, but the classifier defers that nuance to the
        // operator's annotation (per the default-deny posture, an
        // aggregate-with-$out pipeline that mutates state should be
        // annotated [DataMigration] explicitly).
        var c = Classifier.Classify( "var cursor = await collection.AggregateAsync(pipeline)" );
        c.IsDataOp.Should().BeFalse();
    }

    [TestMethod]
    public void CallSite_DistinctAsync_NotDataOp()
    {
        var c = Classifier.Classify( "var values = await collection.DistinctAsync<string>(field, filter)" );
        c.IsDataOp.Should().BeFalse();
    }

    [TestMethod]
    public void CallSite_WatchAsync_NotDataOp()
    {
        var c = Classifier.Classify( "var changes = await collection.WatchAsync(pipeline)" );
        c.IsDataOp.Should().BeFalse();
    }

    // ---- call-site form: structural ----------------------------------------

    [TestMethod]
    public void CallSite_CreateCollectionAsync_IsStructural()
    {
        var c = Classifier.Classify( "await db.CreateCollectionAsync(\"users\")" );
        c.IsDataOp.Should().BeFalse();
        c.IsUnclassified.Should().BeFalse();
    }

    [TestMethod]
    public void CallSite_DropCollectionAsync_IsStructural()
    {
        var c = Classifier.Classify( "await db.DropCollectionAsync(\"users\")" );
        c.IsDataOp.Should().BeFalse();
    }

    [TestMethod]
    public void CallSite_RenameCollectionAsync_IsStructural()
    {
        var c = Classifier.Classify( "await db.RenameCollectionAsync(\"users_v1\", \"users_v2\")" );
        c.IsDataOp.Should().BeFalse();
    }

    [TestMethod]
    public void CallSite_CreateViewAsync_IsStructural()
    {
        var c = Classifier.Classify( "await db.CreateViewAsync<User, ActiveUser>(\"active_users\", \"users\", pipeline)" );
        c.IsDataOp.Should().BeFalse();
    }

    [TestMethod]
    public void CallSite_IndexesCreateOneAsync_IsStructural()
    {
        // Sub-client `.Indexes.CreateOneAsync(...)` is structural index
        // management. The CallSiteStructural regex's sub-client branch
        // matches `.Indexes.<method>(...)`.
        var c = Classifier.Classify( "await collection.Indexes.CreateOneAsync(indexModel)" );
        c.IsDataOp.Should().BeFalse();
        c.IsUnclassified.Should().BeFalse();
    }

    [TestMethod]
    public void CallSite_IndexesDropAsync_IsStructural()
    {
        var c = Classifier.Classify( "await collection.Indexes.DropAsync(\"idx_email\")" );
        c.IsDataOp.Should().BeFalse();
    }

    [TestMethod]
    public void CallSite_DropDatabaseAsync_IsStructural()
    {
        var c = Classifier.Classify( "await _client.DropDatabaseAsync(\"appdb\")" );
        c.IsDataOp.Should().BeFalse();
    }

    [TestMethod]
    public void CallSite_RunCommandAsync_IsStructural()
    {
        // RunCommandAsync is the escape hatch for admin commands; treat
        // as structural by default. Data-bearing commands (eg. mapReduce
        // with output to a collection) should be annotated explicitly.
        var c = Classifier.Classify( "await db.RunCommandAsync<BsonDocument>(new BsonDocument(\"buildInfo\", 1))" );
        c.IsDataOp.Should().BeFalse();
    }

    // ---- default-deny ------------------------------------------------------

    [TestMethod]
    public void UnknownVerb_DefaultDeny()
    {
        var c = Classifier.Classify( "EXPLAIN ANALYZE foo" );
        c.IsUnclassified.Should().BeTrue();
        c.RequiresAnnotation.Should().BeTrue();
    }

    [TestMethod]
    public void EmptyInput_DefaultDeny()
    {
        var c = Classifier.Classify( "" );
        c.IsUnclassified.Should().BeTrue();
        c.RequiresAnnotation.Should().BeTrue();
    }

    // ---- non-determinism ---------------------------------------------------

    [TestMethod]
    public void NonDeterminism_DateTimeUtcNow_Flagged()
    {
        var c = Classifier.Classify( "await collection.InsertOneAsync(new BsonDocument { { \"ts\", DateTime.UtcNow } })" );
        c.IsDataOp.Should().BeTrue();
        c.EmissionHint.Should().Contain( "DateTime.UtcNow" );
        c.RequiresAnnotation.Should().BeTrue();
    }

    [TestMethod]
    public void NonDeterminism_GuidNewGuid_Flagged()
    {
        var c = Classifier.Classify( "await collection.InsertOneAsync(new { id = Guid.NewGuid() })" );
        c.EmissionHint.Should().Contain( "Guid.NewGuid" );
    }

    [TestMethod]
    public void NonDeterminism_NewRandomWithoutSeed_Flagged()
    {
        var c = Classifier.Classify( "var r = new Random(); await collection.InsertOneAsync(new { v = r.Next() })" );
        c.EmissionHint.Should().Contain( "new Random" );
    }

    [TestMethod]
    public void NonDeterminism_NewRandomWithSeed_NotFlagged()
    {
        var c = Classifier.Classify( "var r = new Random(42); await collection.InsertOneAsync(new { v = r.Next() })" );
        c.EmissionHint.Should().BeNull();
        c.RequiresAnnotation.Should().BeFalse();
    }

    [TestMethod]
    public void NonDeterminism_RandomShared_Flagged()
    {
        var c = Classifier.Classify( "await collection.InsertOneAsync(new { v = Random.Shared.Next() })" );
        c.EmissionHint.Should().Contain( "Random.Shared" );
    }

    [TestMethod]
    public void NonDeterminism_OnStructural_StillFlagged()
    {
        // Non-determinism inside a structural call-site (e.g., a collection
        // name computed at runtime) is also flagged via EmissionHint
        // regardless of the data-op verdict.
        var c = Classifier.Classify( "await db.CreateCollectionAsync($\"users_{DateTime.UtcNow:yyyyMMdd}\")" );
        c.IsDataOp.Should().BeFalse();
        c.EmissionHint.Should().Contain( "DateTime.UtcNow" );
    }

    [TestMethod]
    public void ScanNonDeterminism_MultipleHits_DedupedAndSorted()
    {
        var hits = MongoDBDataOpClassifier.ScanNonDeterminism(
            "DateTime.UtcNow; Guid.NewGuid(); DateTime.UtcNow; Random.Shared.Next()" );

        hits.Should().Equal( "DateTime.UtcNow", "Guid.NewGuid", "Random.Shared" );
    }

    [TestMethod]
    public void ScanNonDeterminism_NullOrEmpty_ReturnsEmpty()
    {
        MongoDBDataOpClassifier.ScanNonDeterminism( null ).Should().BeEmpty();
        MongoDBDataOpClassifier.ScanNonDeterminism( "" ).Should().BeEmpty();
    }
}
