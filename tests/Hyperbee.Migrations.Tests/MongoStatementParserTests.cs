using System;
using Hyperbee.Migrations.Providers.MongoDB.Parsers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class MongoStatementParserTests
{
    private readonly MongoStatementParser _parser = new();

    // CREATE COLLECTION

    [TestMethod]
    public void Should_parse_create_collection()
    {
        var result = _parser.ParseStatement( "CREATE COLLECTION mydb.users" );

        Assert.AreEqual( MongoStatementType.CreateCollection, result.StatementType );
        Assert.AreEqual( "mydb", result.DatabaseName );
        Assert.AreEqual( "users", result.CollectionName );
    }

    [TestMethod]
    public void Should_parse_create_collection_with_backtick_quoted_names()
    {
        var result = _parser.ParseStatement( "CREATE COLLECTION `my-db`.`user-data`" );

        Assert.AreEqual( MongoStatementType.CreateCollection, result.StatementType );
        Assert.AreEqual( "my-db", result.DatabaseName );
        Assert.AreEqual( "user-data", result.CollectionName );
    }

    [TestMethod]
    public void Should_parse_create_collection_case_insensitive()
    {
        var result = _parser.ParseStatement( "create collection MyDB.Users" );

        Assert.AreEqual( MongoStatementType.CreateCollection, result.StatementType );
        Assert.AreEqual( "MyDB", result.DatabaseName );
        Assert.AreEqual( "Users", result.CollectionName );
    }

    // DROP COLLECTION

    [TestMethod]
    public void Should_parse_drop_collection()
    {
        var result = _parser.ParseStatement( "DROP COLLECTION mydb.users" );

        Assert.AreEqual( MongoStatementType.DropCollection, result.StatementType );
        Assert.AreEqual( "mydb", result.DatabaseName );
        Assert.AreEqual( "users", result.CollectionName );
    }

    // CREATE INDEX

    [TestMethod]
    public void Should_parse_create_index_single_field()
    {
        var result = _parser.ParseStatement( "CREATE INDEX idx_email ON mydb.users(email)" );

        Assert.AreEqual( MongoStatementType.CreateIndex, result.StatementType );
        Assert.AreEqual( "idx_email", result.IndexName );
        Assert.AreEqual( "mydb", result.DatabaseName );
        Assert.AreEqual( "users", result.CollectionName );
        CollectionAssert.AreEqual( new[] { "email" }, result.FieldNames );
    }

    [TestMethod]
    public void Should_parse_create_index_multi_field()
    {
        var result = _parser.ParseStatement( "CREATE INDEX idx_name_email ON mydb.users(name, email)" );

        Assert.AreEqual( MongoStatementType.CreateIndex, result.StatementType );
        Assert.AreEqual( "idx_name_email", result.IndexName );
        Assert.AreEqual( "mydb", result.DatabaseName );
        Assert.AreEqual( "users", result.CollectionName );
        CollectionAssert.AreEqual( new[] { "name", "email" }, result.FieldNames );
    }

    [TestMethod]
    public void Should_parse_create_index_with_backtick_quoted_names()
    {
        var result = _parser.ParseStatement( "CREATE INDEX `idx-email` ON `my-db`.`user-data`(`e-mail`)" );

        Assert.AreEqual( MongoStatementType.CreateIndex, result.StatementType );
        Assert.AreEqual( "idx-email", result.IndexName );
        Assert.AreEqual( "my-db", result.DatabaseName );
        Assert.AreEqual( "user-data", result.CollectionName );
        CollectionAssert.AreEqual( new[] { "e-mail" }, result.FieldNames );
    }

    // CREATE UNIQUE INDEX

    [TestMethod]
    public void Should_parse_create_unique_index()
    {
        var result = _parser.ParseStatement( "CREATE UNIQUE INDEX idx_email ON mydb.users(email)" );

        Assert.AreEqual( MongoStatementType.CreateUniqueIndex, result.StatementType );
        Assert.AreEqual( "idx_email", result.IndexName );
        Assert.AreEqual( "mydb", result.DatabaseName );
        Assert.AreEqual( "users", result.CollectionName );
        CollectionAssert.AreEqual( new[] { "email" }, result.FieldNames );
    }

    [TestMethod]
    public void Should_parse_create_unique_index_multi_field()
    {
        var result = _parser.ParseStatement( "CREATE UNIQUE INDEX idx_compound ON mydb.users(name, email)" );

        Assert.AreEqual( MongoStatementType.CreateUniqueIndex, result.StatementType );
        Assert.AreEqual( "idx_compound", result.IndexName );
        CollectionAssert.AreEqual( new[] { "name", "email" }, result.FieldNames );
    }

    // DROP INDEX

    [TestMethod]
    public void Should_parse_drop_index()
    {
        var result = _parser.ParseStatement( "DROP INDEX idx_email ON mydb.users" );

        Assert.AreEqual( MongoStatementType.DropIndex, result.StatementType );
        Assert.AreEqual( "idx_email", result.IndexName );
        Assert.AreEqual( "mydb", result.DatabaseName );
        Assert.AreEqual( "users", result.CollectionName );
    }

    // INSERT INTO

    [TestMethod]
    public void Should_parse_insert_into()
    {
        var result = _parser.ParseStatement( "INSERT INTO mydb.users" );

        Assert.AreEqual( MongoStatementType.Insert, result.StatementType );
        Assert.AreEqual( "mydb", result.DatabaseName );
        Assert.AreEqual( "users", result.CollectionName );
    }

    // Case insensitivity

    [TestMethod]
    public void Should_parse_mixed_case_keywords()
    {
        var result = _parser.ParseStatement( "Create Index idx_test On mydb.users(field1)" );

        Assert.AreEqual( MongoStatementType.CreateIndex, result.StatementType );
        Assert.AreEqual( "idx_test", result.IndexName );
    }

    [TestMethod]
    public void Should_parse_lowercase_keywords()
    {
        var result = _parser.ParseStatement( "drop collection mydb.users" );

        Assert.AreEqual( MongoStatementType.DropCollection, result.StatementType );
    }

    // Original statement preserved

    [TestMethod]
    public void Should_preserve_original_statement()
    {
        var statement = "CREATE COLLECTION mydb.users";
        var result = _parser.ParseStatement( statement );

        Assert.AreEqual( statement, result.Statement );
    }

    // Error cases

    [TestMethod]
    public void Should_throw_on_unknown_statement()
    {
        try
        {
            _parser.ParseStatement( "ALTER TABLE mydb.users" );
            Assert.Fail( "Expected NotSupportedException was not thrown." );
        }
        catch ( NotSupportedException )
        {
        }
    }

    [TestMethod]
    public void Should_throw_on_malformed_create()
    {
        try
        {
            _parser.ParseStatement( "CREATE mydb.users" );
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

    [TestMethod]
    public void Should_throw_on_whitespace_statement()
    {
        try
        {
            _parser.ParseStatement( "   " );
            Assert.Fail( "Expected ArgumentException was not thrown." );
        }
        catch ( ArgumentException )
        {
        }
    }

    // Whitespace variations

    [TestMethod]
    public void Should_handle_extra_whitespace()
    {
        var result = _parser.ParseStatement( "  CREATE   COLLECTION   mydb.users  " );

        Assert.AreEqual( MongoStatementType.CreateCollection, result.StatementType );
        Assert.AreEqual( "mydb", result.DatabaseName );
        Assert.AreEqual( "users", result.CollectionName );
    }
}
