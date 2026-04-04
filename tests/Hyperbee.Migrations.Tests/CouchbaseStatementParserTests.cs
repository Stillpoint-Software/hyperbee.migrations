using System;
using Couchbase.Management.Buckets;
using Hyperbee.Migrations.Providers.Couchbase.Parsers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class CouchbaseStatementParserTests
{
    private readonly StatementParser _parser = new();

    // CREATE INDEX

    [TestMethod]
    public void Should_parse_create_index()
    {
        var result = _parser.ParseStatement( "CREATE INDEX idx_email ON bucket.scope.collection(email)" );

        Assert.AreEqual( StatementType.CreateIndex, result.StatementType );
        Assert.AreEqual( "idx_email", result.Name );
        Assert.AreEqual( "bucket", result.Keyspace.BucketName );
        Assert.AreEqual( "scope", result.Keyspace.ScopeName );
        Assert.AreEqual( "collection", result.Keyspace.CollectionName );
    }

    [TestMethod]
    public void Should_parse_create_index_with_namespace()
    {
        var result = _parser.ParseStatement( "CREATE INDEX idx_test ON default:bucket.scope.collection(field)" );

        Assert.AreEqual( StatementType.CreateIndex, result.StatementType );
        Assert.AreEqual( "idx_test", result.Name );
        Assert.AreEqual( "default", result.Keyspace.Namespace );
        Assert.AreEqual( "bucket", result.Keyspace.BucketName );
    }

    [TestMethod]
    public void Should_parse_create_index_backtick_quoted()
    {
        var result = _parser.ParseStatement( "CREATE INDEX `idx-email` ON `my-bucket`.`my-scope`.`my-collection`(email)" );

        Assert.AreEqual( StatementType.CreateIndex, result.StatementType );
        Assert.AreEqual( "idx-email", result.Name );
        Assert.AreEqual( "my-bucket", result.Keyspace.BucketName );
        Assert.AreEqual( "my-scope", result.Keyspace.ScopeName );
        Assert.AreEqual( "my-collection", result.Keyspace.CollectionName );
    }

    // CREATE PRIMARY INDEX

    [TestMethod]
    public void Should_parse_create_primary_index_with_name()
    {
        var result = _parser.ParseStatement( "CREATE PRIMARY INDEX idx_primary ON bucket.scope.collection" );

        Assert.AreEqual( StatementType.CreatePrimaryIndex, result.StatementType );
        Assert.AreEqual( "idx_primary", result.Name );
        Assert.AreEqual( "bucket", result.Keyspace.BucketName );
    }

    [TestMethod]
    public void Should_parse_create_primary_index_without_name()
    {
        var result = _parser.ParseStatement( "CREATE PRIMARY INDEX ON bucket.scope.collection" );

        Assert.AreEqual( StatementType.CreatePrimaryIndex, result.StatementType );
        Assert.IsNull( result.Name );
        Assert.AreEqual( "bucket", result.Keyspace.BucketName );
    }

    // CREATE BUCKET

    [TestMethod]
    public void Should_parse_create_bucket()
    {
        var result = _parser.ParseStatement( "CREATE BUCKET `migrationbucket` TYPE Couchbase RAMQUOTA 100 FLUSH ENABLED" );

        Assert.AreEqual( StatementType.CreateBucket, result.StatementType );
        Assert.AreEqual( "migrationbucket", result.Keyspace.BucketName );
        Assert.IsNotNull( result.BucketSettings );
        Assert.AreEqual( "migrationbucket", result.BucketSettings.Name );
        Assert.AreEqual( BucketType.Couchbase, result.BucketSettings.BucketType );
        Assert.AreEqual( 100, result.BucketSettings.RamQuotaMB );
        Assert.IsTrue( result.BucketSettings.FlushEnabled );
    }

    [TestMethod]
    public void Should_parse_create_bucket_simple()
    {
        var result = _parser.ParseStatement( "CREATE BUCKET mybucket" );

        Assert.AreEqual( StatementType.CreateBucket, result.StatementType );
        Assert.AreEqual( "mybucket", result.Keyspace.BucketName );
        Assert.IsNotNull( result.BucketSettings );
        Assert.AreEqual( 256, result.BucketSettings.RamQuotaMB ); // default
    }

    [TestMethod]
    public void Should_parse_create_bucket_ephemeral()
    {
        var result = _parser.ParseStatement( "CREATE BUCKET mybucket TYPE EPHEMERAL RAMQUOTA 512" );

        Assert.AreEqual( BucketType.Ephemeral, result.BucketSettings.BucketType );
        Assert.AreEqual( 512, result.BucketSettings.RamQuotaMB );
    }

    // CREATE SCOPE

    [TestMethod]
    public void Should_parse_create_scope()
    {
        var result = _parser.ParseStatement( "CREATE SCOPE bucket.myscope" );

        Assert.AreEqual( StatementType.CreateScope, result.StatementType );
        Assert.AreEqual( "bucket", result.Keyspace.BucketName );
    }

    // CREATE COLLECTION

    [TestMethod]
    public void Should_parse_create_collection()
    {
        var result = _parser.ParseStatement( "CREATE COLLECTION bucket.scope.collection" );

        Assert.AreEqual( StatementType.CreateCollection, result.StatementType );
        Assert.AreEqual( "bucket", result.Keyspace.BucketName );
        Assert.AreEqual( "scope", result.Keyspace.ScopeName );
        Assert.AreEqual( "collection", result.Keyspace.CollectionName );
    }

    // DROP statements

    [TestMethod]
    public void Should_parse_drop_bucket()
    {
        var result = _parser.ParseStatement( "DROP BUCKET mybucket" );

        Assert.AreEqual( StatementType.DropBucket, result.StatementType );
        Assert.AreEqual( "mybucket", result.Keyspace.BucketName );
    }

    [TestMethod]
    public void Should_parse_drop_scope()
    {
        var result = _parser.ParseStatement( "DROP SCOPE bucket.myscope" );

        Assert.AreEqual( StatementType.DropScope, result.StatementType );
    }

    [TestMethod]
    public void Should_parse_drop_collection()
    {
        var result = _parser.ParseStatement( "DROP COLLECTION bucket.scope.collection" );

        Assert.AreEqual( StatementType.DropCollection, result.StatementType );
    }

    // BUILD INDEX

    [TestMethod]
    public void Should_parse_build_index()
    {
        var result = _parser.ParseStatement( "BUILD INDEX ON bucket.scope.collection(idx1, idx2)" );

        Assert.AreEqual( StatementType.Build, result.StatementType );
        Assert.AreEqual( "bucket", result.Keyspace.BucketName );
    }

    // UPDATE

    [TestMethod]
    public void Should_parse_update()
    {
        var result = _parser.ParseStatement( "UPDATE bucket.scope.collection SET active = true" );

        Assert.AreEqual( StatementType.Update, result.StatementType );
        Assert.AreEqual( "bucket", result.Keyspace.BucketName );
    }

    // Case insensitivity

    [TestMethod]
    public void Should_parse_case_insensitive_keywords()
    {
        var result = _parser.ParseStatement( "create index idx_test on bucket.scope.collection(field)" );

        Assert.AreEqual( StatementType.CreateIndex, result.StatementType );
        Assert.AreEqual( "idx_test", result.Name );
    }

    [TestMethod]
    public void Should_parse_mixed_case_keywords()
    {
        var result = _parser.ParseStatement( "Create Bucket mybucket" );

        Assert.AreEqual( StatementType.CreateBucket, result.StatementType );
    }

    // Statement preservation

    [TestMethod]
    public void Should_preserve_original_statement()
    {
        var statement = "CREATE INDEX idx_test ON bucket.scope.collection(field)";
        var result = _parser.ParseStatement( statement );

        Assert.AreEqual( statement, result.Statement );
    }

    // Error cases

    [TestMethod]
    public void Should_throw_on_unknown_statement()
    {
        try
        {
            _parser.ParseStatement( "ALTER TABLE bucket.collection" );
            Assert.Fail( "Expected NotSupportedException was not thrown." );
        }
        catch ( NotSupportedException )
        {
        }
    }

    [TestMethod]
    public void Should_throw_on_null_statement()
    {
        try
        {
            _parser.ParseStatement( null );
            Assert.Fail( "Expected ArgumentException was not thrown." );
        }
        catch ( ArgumentException )
        {
        }
    }

    [TestMethod]
    public void Should_throw_on_empty_statement()
    {
        try
        {
            _parser.ParseStatement( "" );
            Assert.Fail( "Expected ArgumentException was not thrown." );
        }
        catch ( ArgumentException )
        {
        }
    }

    // Whitespace

    [TestMethod]
    public void Should_handle_extra_whitespace()
    {
        var result = _parser.ParseStatement( "  CREATE   BUCKET   mybucket  " );

        Assert.AreEqual( StatementType.CreateBucket, result.StatementType );
        Assert.AreEqual( "mybucket", result.Keyspace.BucketName );
    }
}
