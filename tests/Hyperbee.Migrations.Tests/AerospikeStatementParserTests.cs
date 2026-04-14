using System;
using Hyperbee.Migrations.Providers.Aerospike.Parsers;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class AerospikeStatementParserTests
{
    private readonly AerospikeStatementParser _parser = new();

    // CREATE INDEX

    [TestMethod]
    public void Should_parse_create_index_with_string_type()
    {
        var result = _parser.ParseStatement( "CREATE INDEX idx_email ON test.users (email) STRING" );

        Assert.AreEqual( AerospikeStatementType.CreateIndex, result.StatementType );
        Assert.AreEqual( "idx_email", result.IndexName );
        Assert.AreEqual( "test", result.Namespace );
        Assert.AreEqual( "users", result.SetName );
        Assert.AreEqual( "email", result.BinName );
        Assert.AreEqual( AerospikeIndexType.String, result.IndexType );
    }

    [TestMethod]
    public void Should_parse_create_index_with_numeric_type()
    {
        var result = _parser.ParseStatement( "CREATE INDEX idx_age ON test.users (age) NUMERIC" );

        Assert.AreEqual( AerospikeStatementType.CreateIndex, result.StatementType );
        Assert.AreEqual( "idx_age", result.IndexName );
        Assert.AreEqual( "age", result.BinName );
        Assert.AreEqual( AerospikeIndexType.Numeric, result.IndexType );
    }

    [TestMethod]
    public void Should_parse_create_index_with_geo_type()
    {
        var result = _parser.ParseStatement( "CREATE INDEX idx_loc ON test.users (location) GEO2DSPHERE" );

        Assert.AreEqual( AerospikeStatementType.CreateIndex, result.StatementType );
        Assert.AreEqual( "idx_loc", result.IndexName );
        Assert.AreEqual( "location", result.BinName );
        Assert.AreEqual( AerospikeIndexType.Geo2DSphere, result.IndexType );
    }

    [TestMethod]
    public void Should_parse_create_index_without_type_defaults_to_string()
    {
        var result = _parser.ParseStatement( "CREATE INDEX idx_name ON test.users (name)" );

        Assert.AreEqual( AerospikeStatementType.CreateIndex, result.StatementType );
        Assert.AreEqual( "idx_name", result.IndexName );
        Assert.AreEqual( "name", result.BinName );
        Assert.AreEqual( AerospikeIndexType.String, result.IndexType );
    }

    [TestMethod]
    public void Should_parse_create_index_case_insensitive_keywords()
    {
        var result = _parser.ParseStatement( "create index idx_test on test.data (value) numeric" );

        Assert.AreEqual( AerospikeStatementType.CreateIndex, result.StatementType );
        Assert.AreEqual( "idx_test", result.IndexName );
        Assert.AreEqual( AerospikeIndexType.Numeric, result.IndexType );
    }

    [TestMethod]
    public void Should_parse_create_index_mixed_case()
    {
        var result = _parser.ParseStatement( "Create Index idx_test On test.data (value) String" );

        Assert.AreEqual( AerospikeStatementType.CreateIndex, result.StatementType );
        Assert.AreEqual( AerospikeIndexType.String, result.IndexType );
    }

    // DROP INDEX

    [TestMethod]
    public void Should_parse_drop_index()
    {
        var result = _parser.ParseStatement( "DROP INDEX test idx_email" );

        Assert.AreEqual( AerospikeStatementType.DropIndex, result.StatementType );
        Assert.AreEqual( "test", result.Namespace );
        Assert.AreEqual( "idx_email", result.IndexName );
    }

    [TestMethod]
    public void Should_parse_drop_index_case_insensitive()
    {
        var result = _parser.ParseStatement( "drop index myns idx_test" );

        Assert.AreEqual( AerospikeStatementType.DropIndex, result.StatementType );
        Assert.AreEqual( "myns", result.Namespace );
        Assert.AreEqual( "idx_test", result.IndexName );
    }

    // CREATE SET

    [TestMethod]
    public void Should_parse_create_set()
    {
        var result = _parser.ParseStatement( "CREATE SET test.users" );

        Assert.AreEqual( AerospikeStatementType.CreateSet, result.StatementType );
        Assert.AreEqual( "test", result.Namespace );
        Assert.AreEqual( "users", result.SetName );
    }

    // INSERT INTO

    [TestMethod]
    public void Should_parse_insert_into()
    {
        var result = _parser.ParseStatement( "INSERT INTO test.users (PK, name, email) VALUES ('user1', 'John', 'john@test.com')" );

        Assert.AreEqual( AerospikeStatementType.Insert, result.StatementType );
        Assert.AreEqual( "test", result.Namespace );
        Assert.AreEqual( "users", result.SetName );
    }

    [TestMethod]
    public void Should_parse_insert_into_case_insensitive()
    {
        var result = _parser.ParseStatement( "insert into test.data (PK, value) values ('key1', 123)" );

        Assert.AreEqual( AerospikeStatementType.Insert, result.StatementType );
        Assert.AreEqual( "test", result.Namespace );
        Assert.AreEqual( "data", result.SetName );
    }

    // DELETE FROM

    [TestMethod]
    public void Should_parse_delete_from()
    {
        var result = _parser.ParseStatement( "DELETE FROM test.users WHERE PK = 'user1'" );

        Assert.AreEqual( AerospikeStatementType.Delete, result.StatementType );
        Assert.AreEqual( "test", result.Namespace );
        Assert.AreEqual( "users", result.SetName );
    }

    [TestMethod]
    public void Should_parse_delete_from_case_insensitive()
    {
        var result = _parser.ParseStatement( "delete from test.data where PK = 'key1'" );

        Assert.AreEqual( AerospikeStatementType.Delete, result.StatementType );
        Assert.AreEqual( "test", result.Namespace );
        Assert.AreEqual( "data", result.SetName );
    }

    // Backtick-quoted identifiers

    [TestMethod]
    public void Should_parse_create_index_with_backtick_quoted_names()
    {
        var result = _parser.ParseStatement( "CREATE INDEX `idx-email` ON `test-ns`.`user-set` (`e-mail`) STRING" );

        Assert.AreEqual( AerospikeStatementType.CreateIndex, result.StatementType );
        Assert.AreEqual( "idx-email", result.IndexName );
        Assert.AreEqual( "test-ns", result.Namespace );
        Assert.AreEqual( "user-set", result.SetName );
        Assert.AreEqual( "e-mail", result.BinName );
    }

    // Original statement preservation

    [TestMethod]
    public void Should_preserve_original_statement()
    {
        var statement = "CREATE INDEX idx_test ON test.users (name) STRING";
        var result = _parser.ParseStatement( statement );

        Assert.AreEqual( statement, result.Statement );
    }

    // Flags: RECREATE and WAIT

    [TestMethod]
    public void Should_have_false_flags_by_default()
    {
        var result = _parser.ParseStatement( "CREATE INDEX idx_test ON test.users (name)" );

        Assert.IsFalse( result.Recreate );
        Assert.IsFalse( result.WaitReady );
    }

    [TestMethod]
    public void Should_parse_recreate_flag()
    {
        var result = _parser.ParseStatement( "CREATE INDEX RECREATE idx_test ON test.users (name)" );

        Assert.IsTrue( result.Recreate );
        Assert.IsFalse( result.WaitReady );
        Assert.AreEqual( "idx_test", result.IndexName );
    }

    [TestMethod]
    public void Should_parse_wait_flag()
    {
        var result = _parser.ParseStatement( "CREATE INDEX WAIT idx_test ON test.users (name)" );

        Assert.IsFalse( result.Recreate );
        Assert.IsTrue( result.WaitReady );
        Assert.AreEqual( "idx_test", result.IndexName );
    }

    [TestMethod]
    public void Should_parse_recreate_and_wait_flags()
    {
        var result = _parser.ParseStatement( "CREATE INDEX RECREATE WAIT idx_test ON test.users (name) STRING" );

        Assert.IsTrue( result.Recreate );
        Assert.IsTrue( result.WaitReady );
        Assert.AreEqual( "idx_test", result.IndexName );
        Assert.AreEqual( AerospikeIndexType.String, result.IndexType );
    }

    [TestMethod]
    public void Should_parse_flags_case_insensitive()
    {
        var result = _parser.ParseStatement( "create index recreate wait idx_test on test.users (name)" );

        Assert.IsTrue( result.Recreate );
        Assert.IsTrue( result.WaitReady );
    }

    [TestMethod]
    public void Should_parse_if_not_exists()
    {
        var result = _parser.ParseStatement( "CREATE INDEX IF NOT EXISTS idx_test ON test.users (name) STRING" );

        Assert.AreEqual( AerospikeStatementType.CreateIndex, result.StatementType );
        Assert.AreEqual( "idx_test", result.IndexName );
        Assert.AreEqual( AerospikeIndexType.String, result.IndexType );
    }

    [TestMethod]
    public void Should_parse_if_not_exists_with_wait()
    {
        var result = _parser.ParseStatement( "CREATE INDEX IF NOT EXISTS WAIT idx_test ON test.users (name)" );

        Assert.AreEqual( "idx_test", result.IndexName );
        Assert.IsTrue( result.WaitReady );
    }

    // Whitespace handling

    [TestMethod]
    public void Should_handle_extra_whitespace()
    {
        var result = _parser.ParseStatement( "  CREATE   INDEX   idx_test   ON   test.users   (name)   STRING  " );

        Assert.AreEqual( AerospikeStatementType.CreateIndex, result.StatementType );
        Assert.AreEqual( "idx_test", result.IndexName );
    }

    // Error cases

    [TestMethod]
    public void Should_throw_on_unknown_statement()
    {
        try
        {
            _parser.ParseStatement( "ALTER INDEX test idx_test" );
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
}
