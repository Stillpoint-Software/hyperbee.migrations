using FluentAssertions;
using Hyperbee.Migrations.Providers.MongoDB.Squash;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 3 Task 3.3: MongoDBStatementClassifier unit coverage.
//
// The classifier is a thin projection over MongoStatementParser. Tests focus on:
//   - Kind extraction for each parser-supported statement type
//   - DatabaseName + CollectionName + ObjectName population
//   - Graceful default-deny (Kind=Unknown) on parser failure / empty / null
//
// Grammar fidelity itself is exercised by the existing parser tests.

[TestClass]
public class MongoDBStatementClassifierTests
{
    [TestMethod]
    public void Classify_CreateCollection_ExtractsDatabaseAndCollection()
    {
        var c = MongoDBStatementClassifier.Classify( "CREATE COLLECTION appdb.users" );

        c.Kind.Should().Be( MongoDBStatementKind.CreateCollection );
        c.DatabaseName.Should().Be( "appdb" );
        c.CollectionName.Should().Be( "users" );
        c.ObjectName.Should().BeNull();
        c.Body.Should().Be( "CREATE COLLECTION appdb.users" );
        c.Detail.Should().BeNull();
    }

    [TestMethod]
    public void Classify_DropCollection_ExtractsDatabaseAndCollection()
    {
        var c = MongoDBStatementClassifier.Classify( "DROP COLLECTION appdb.users" );

        c.Kind.Should().Be( MongoDBStatementKind.DropCollection );
        c.DatabaseName.Should().Be( "appdb" );
        c.CollectionName.Should().Be( "users" );
    }

    [TestMethod]
    public void Classify_CreateIndex_ExtractsAllFields()
    {
        var c = MongoDBStatementClassifier.Classify( "CREATE INDEX idx_email ON appdb.users(email)" );

        c.Kind.Should().Be( MongoDBStatementKind.CreateIndex );
        c.DatabaseName.Should().Be( "appdb" );
        c.CollectionName.Should().Be( "users" );
        c.ObjectName.Should().Be( "idx_email" );
    }

    [TestMethod]
    public void Classify_CreateUniqueIndex_ExtractsAllFields()
    {
        var c = MongoDBStatementClassifier.Classify( "CREATE UNIQUE INDEX idx_email ON appdb.users(email)" );

        c.Kind.Should().Be( MongoDBStatementKind.CreateUniqueIndex );
        c.DatabaseName.Should().Be( "appdb" );
        c.CollectionName.Should().Be( "users" );
        c.ObjectName.Should().Be( "idx_email" );
    }

    [TestMethod]
    public void Classify_CreateIndexCompoundFields_ExtractsAllFields()
    {
        // Multi-field index: parser accepts (field1, field2, ...).
        var c = MongoDBStatementClassifier.Classify( "CREATE INDEX idx_compound ON appdb.users(tenant_id, email)" );

        c.Kind.Should().Be( MongoDBStatementKind.CreateIndex );
        c.ObjectName.Should().Be( "idx_compound" );
    }

    [TestMethod]
    public void Classify_DropIndex_ExtractsAllFields()
    {
        var c = MongoDBStatementClassifier.Classify( "DROP INDEX idx_email ON appdb.users" );

        c.Kind.Should().Be( MongoDBStatementKind.DropIndex );
        c.DatabaseName.Should().Be( "appdb" );
        c.CollectionName.Should().Be( "users" );
        c.ObjectName.Should().Be( "idx_email" );
    }

    [TestMethod]
    public void Classify_Insert_ExtractsDatabaseAndCollection()
    {
        var c = MongoDBStatementClassifier.Classify( "INSERT INTO appdb.users" );

        c.Kind.Should().Be( MongoDBStatementKind.Insert );
        c.DatabaseName.Should().Be( "appdb" );
        c.CollectionName.Should().Be( "users" );
    }

    [TestMethod]
    public void Classify_BacktickQuotedIdentifiers_Supported()
    {
        // Parser identifier rule accepts backtick-quoted forms for names
        // containing characters outside the plain-identifier set.
        var c = MongoDBStatementClassifier.Classify( "CREATE COLLECTION `my-db`.`my-coll`" );

        c.Kind.Should().Be( MongoDBStatementKind.CreateCollection );
        c.DatabaseName.Should().Be( "my-db" );
        c.CollectionName.Should().Be( "my-coll" );
    }

    [TestMethod]
    public void Classify_UnknownVerb_ReturnsUnknownWithBodyPreserved()
    {
        var c = MongoDBStatementClassifier.Classify( "TRUNCATE appdb.users" );

        c.Kind.Should().Be( MongoDBStatementKind.Unknown );
        c.Body.Should().Be( "TRUNCATE appdb.users" );
        c.Detail.Should().NotBeNull();
        c.DatabaseName.Should().BeNull();
        c.CollectionName.Should().BeNull();
    }

    [TestMethod]
    public void Classify_SyntaxError_ReturnsUnknownWithDetail()
    {
        var c = MongoDBStatementClassifier.Classify( "CREATE INDEX missing_target" );

        c.Kind.Should().Be( MongoDBStatementKind.Unknown );
        c.Detail.Should().NotBeNull();
    }

    [TestMethod]
    public void Classify_EmptyInput_ReturnsUnknown()
    {
        var c = MongoDBStatementClassifier.Classify( "" );

        c.Kind.Should().Be( MongoDBStatementKind.Unknown );
        c.Body.Should().Be( "" );
    }

    [TestMethod]
    public void Classify_NullInput_ReturnsUnknown()
    {
        var c = MongoDBStatementClassifier.Classify( null );

        c.Kind.Should().Be( MongoDBStatementKind.Unknown );
        c.Body.Should().Be( "" );
    }

    [TestMethod]
    public void Classify_WhitespaceOnly_ReturnsUnknown()
    {
        var c = MongoDBStatementClassifier.Classify( "   \n\t  " );

        c.Kind.Should().Be( MongoDBStatementKind.Unknown );
    }
}
