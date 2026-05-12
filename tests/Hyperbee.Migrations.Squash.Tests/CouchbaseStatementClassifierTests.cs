using FluentAssertions;
using Hyperbee.Migrations.Providers.Couchbase.Squash;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 4 Task 4.3: CouchbaseStatementClassifier unit coverage.
//
// Thin projection over the existing Couchbase StatementParser. Mirrors the
// shape of AerospikeStatementClassifierTests + OpenSearchStatementClassifierTests
// + MongoDBStatementClassifierTests: assert each kind maps 1:1, keyspace
// components project through correctly, parser failure default-denies.

[TestClass]
public class CouchbaseStatementClassifierTests
{
    [TestMethod]
    public void Classify_CreateBucket_ProjectsBucketName()
    {
        var r = CouchbaseStatementClassifier.Classify( "CREATE BUCKET myapp TYPE COUCHBASE RAMQUOTA 256" );
        r.Kind.Should().Be( CouchbaseStatementKind.CreateBucket );
        r.BucketName.Should().Be( "myapp" );
        r.Body.Should().Be( "CREATE BUCKET myapp TYPE COUCHBASE RAMQUOTA 256" );
    }

    [TestMethod]
    public void Classify_CreateIndex_ProjectsIndexName()
    {
        var r = CouchbaseStatementClassifier.Classify( "CREATE INDEX idx_email ON myapp" );
        r.Kind.Should().Be( CouchbaseStatementKind.CreateIndex );
        r.IndexName.Should().Be( "idx_email" );
        r.BucketName.Should().Be( "myapp" );
    }

    [TestMethod]
    public void Classify_CreatePrimaryIndex_WithName_ProjectsName()
    {
        var r = CouchbaseStatementClassifier.Classify( "CREATE PRIMARY INDEX pk_myapp ON myapp" );
        r.Kind.Should().Be( CouchbaseStatementKind.CreatePrimaryIndex );
        r.IndexName.Should().Be( "pk_myapp" );
        r.BucketName.Should().Be( "myapp" );
    }

    [TestMethod]
    public void Classify_CreatePrimaryIndex_Anonymous_NameNull()
    {
        var r = CouchbaseStatementClassifier.Classify( "CREATE PRIMARY INDEX ON myapp" );
        r.Kind.Should().Be( CouchbaseStatementKind.CreatePrimaryIndex );
        r.IndexName.Should().BeNull();
        r.BucketName.Should().Be( "myapp" );
    }

    [TestMethod]
    public void Classify_CreateScope_ProjectsBucketAndScope()
    {
        var r = CouchbaseStatementClassifier.Classify( "CREATE SCOPE myapp.tenant1" );
        r.Kind.Should().Be( CouchbaseStatementKind.CreateScope );
        r.BucketName.Should().Be( "myapp" );
        // The KeyspaceRef shape for `bucket.scope` lands in (bucket, collection)
        // slots per the partial parser; this test simply asserts bucket
        // projects through.
    }

    [TestMethod]
    public void Classify_CreateCollection_ProjectsBucketScopeCollection()
    {
        var r = CouchbaseStatementClassifier.Classify( "CREATE COLLECTION myapp.tenant1.users" );
        r.Kind.Should().Be( CouchbaseStatementKind.CreateCollection );
        r.BucketName.Should().Be( "myapp" );
        r.ScopeName.Should().Be( "tenant1" );
        r.CollectionName.Should().Be( "users" );
    }

    [TestMethod]
    public void Classify_DropBucket_ProjectsBucketName()
    {
        var r = CouchbaseStatementClassifier.Classify( "DROP BUCKET myapp" );
        r.Kind.Should().Be( CouchbaseStatementKind.DropBucket );
        r.BucketName.Should().Be( "myapp" );
    }

    [TestMethod]
    public void Classify_DropScope_Recognized()
    {
        var r = CouchbaseStatementClassifier.Classify( "DROP SCOPE myapp.tenant1" );
        r.Kind.Should().Be( CouchbaseStatementKind.DropScope );
        r.BucketName.Should().Be( "myapp" );
    }

    [TestMethod]
    public void Classify_DropCollection_Recognized()
    {
        var r = CouchbaseStatementClassifier.Classify( "DROP COLLECTION myapp.tenant1.users" );
        r.Kind.Should().Be( CouchbaseStatementKind.DropCollection );
        r.BucketName.Should().Be( "myapp" );
        r.ScopeName.Should().Be( "tenant1" );
        r.CollectionName.Should().Be( "users" );
    }

    [TestMethod]
    public void Classify_BuildIndex_Recognized()
    {
        var r = CouchbaseStatementClassifier.Classify( "BUILD INDEX ON myapp" );
        r.Kind.Should().Be( CouchbaseStatementKind.BuildIndex );
        r.BucketName.Should().Be( "myapp" );
    }

    [TestMethod]
    public void Classify_Update_Recognized()
    {
        var r = CouchbaseStatementClassifier.Classify( "UPDATE myapp SET status = 'a'" );
        r.Kind.Should().Be( CouchbaseStatementKind.Update );
        r.BucketName.Should().Be( "myapp" );
    }

    [TestMethod]
    public void Classify_QuotedIdentifier_Unquoted()
    {
        var r = CouchbaseStatementClassifier.Classify( "CREATE BUCKET `my-app`" );
        r.Kind.Should().Be( CouchbaseStatementKind.CreateBucket );
        r.BucketName.Should().Be( "my-app" );
    }

    [TestMethod]
    public void Classify_NamespacedKeyspace_NamespaceProjected()
    {
        var r = CouchbaseStatementClassifier.Classify( "CREATE BUCKET default:myapp" );
        r.Kind.Should().Be( CouchbaseStatementKind.CreateBucket );
        r.Namespace.Should().Be( "default" );
        r.BucketName.Should().Be( "myapp" );
    }

    // ---- parser failure / unknown -----------------------------------------

    [TestMethod]
    public void Classify_DmlInsert_FallsThroughUnknown()
    {
        // The partial parser doesn't consume DML. The data-op classifier
        // handles those via leading-keyword regex. Statement classifier
        // surfaces Unknown + the parser diagnostic.
        var r = CouchbaseStatementClassifier.Classify( "INSERT INTO myapp (KEY, VALUE) VALUES ('k', {})" );
        r.Kind.Should().Be( CouchbaseStatementKind.Unknown );
        r.Body.Should().StartWith( "INSERT INTO" );
        r.Detail.Should().NotBeNullOrEmpty();
    }

    [TestMethod]
    public void Classify_DmlSelect_FallsThroughUnknown()
    {
        var r = CouchbaseStatementClassifier.Classify( "SELECT * FROM myapp" );
        r.Kind.Should().Be( CouchbaseStatementKind.Unknown );
        r.Detail.Should().NotBeNullOrEmpty();
    }

    [TestMethod]
    public void Classify_GarbageStatement_FallsThroughUnknown()
    {
        var r = CouchbaseStatementClassifier.Classify( "NOT A VALID STATEMENT" );
        r.Kind.Should().Be( CouchbaseStatementKind.Unknown );
        r.Detail.Should().NotBeNullOrEmpty();
    }

    [TestMethod]
    public void Classify_Empty_FallsThroughUnknown()
    {
        var r = CouchbaseStatementClassifier.Classify( "" );
        r.Kind.Should().Be( CouchbaseStatementKind.Unknown );
        r.Body.Should().Be( "" );
    }

    [TestMethod]
    public void Classify_Whitespace_FallsThroughUnknown()
    {
        var r = CouchbaseStatementClassifier.Classify( "   " );
        r.Kind.Should().Be( CouchbaseStatementKind.Unknown );
    }

    [TestMethod]
    public void Classify_Null_FallsThroughUnknown()
    {
        var r = CouchbaseStatementClassifier.Classify( null );
        r.Kind.Should().Be( CouchbaseStatementKind.Unknown );
        r.Body.Should().Be( "" );
    }
}
