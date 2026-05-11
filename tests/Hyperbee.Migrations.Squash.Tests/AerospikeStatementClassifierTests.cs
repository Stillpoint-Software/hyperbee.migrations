using FluentAssertions;
using Hyperbee.Migrations.Providers.Aerospike.Squash;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 1 (R-P1, R-P8): AerospikeStatementClassifier unit coverage.
//
// The classifier is a thin lift over AerospikeStatementParser. Tests focus on:
//   - Kind extraction for each parser-supported statement type
//   - Namespace + SetName + ObjectName population
//   - Graceful default-deny (Kind=Unknown) on parser failure / empty input
//
// Grammar fidelity itself is exercised by the existing parser tests.

[TestClass]
public class AerospikeStatementClassifierTests
{
    [TestMethod]
    public void Classify_CreateIndex_ExtractsNamespaceSetAndIndexName()
    {
        var c = AerospikeStatementClassifier.Classify( "CREATE INDEX idx_name ON test.users (name) STRING" );

        c.Kind.Should().Be( AerospikeStatementKind.CreateIndex );
        c.Namespace.Should().Be( "test" );
        c.SetName.Should().Be( "users" );
        c.ObjectName.Should().Be( "idx_name" );
        c.Body.Should().Be( "CREATE INDEX idx_name ON test.users (name) STRING" );
        c.Detail.Should().BeNull();
    }

    [TestMethod]
    public void Classify_DropIndex_ExtractsNamespaceAndIndexName()
    {
        // AQL: DROP INDEX <namespace> <indexName> (no dot, no ON)
        var c = AerospikeStatementClassifier.Classify( "DROP INDEX test idx_name" );

        c.Kind.Should().Be( AerospikeStatementKind.DropIndex );
        c.Namespace.Should().Be( "test" );
        c.SetName.Should().BeNull();
        c.ObjectName.Should().Be( "idx_name" );
    }

    [TestMethod]
    public void Classify_CreateSet_ExtractsNamespaceAndSetName()
    {
        var c = AerospikeStatementClassifier.Classify( "CREATE SET test.users" );

        c.Kind.Should().Be( AerospikeStatementKind.CreateSet );
        c.Namespace.Should().Be( "test" );
        c.SetName.Should().Be( "users" );
        c.ObjectName.Should().BeNull();
    }

    [TestMethod]
    public void Classify_Insert_ExtractsNamespaceAndSetName()
    {
        var c = AerospikeStatementClassifier.Classify( "INSERT INTO test.users (PK, name) VALUES ('k1', 'alice')" );

        c.Kind.Should().Be( AerospikeStatementKind.Insert );
        c.Namespace.Should().Be( "test" );
        c.SetName.Should().Be( "users" );
    }

    [TestMethod]
    public void Classify_Delete_ExtractsNamespaceAndSetName()
    {
        var c = AerospikeStatementClassifier.Classify( "DELETE FROM test.users WHERE PK = 'k1'" );

        c.Kind.Should().Be( AerospikeStatementKind.Delete );
        c.Namespace.Should().Be( "test" );
        c.SetName.Should().Be( "users" );
    }

    [TestMethod]
    public void Classify_CreateIndex_RespectsBacktickQuotedIdentifiers()
    {
        var c = AerospikeStatementClassifier.Classify( "CREATE INDEX `idx-with-dash` ON `my-ns`.`my-set` (`bin-name`) NUMERIC" );

        c.Kind.Should().Be( AerospikeStatementKind.CreateIndex );
        c.Namespace.Should().Be( "my-ns" );
        c.SetName.Should().Be( "my-set" );
        c.ObjectName.Should().Be( "idx-with-dash" );
    }

    [TestMethod]
    public void Classify_UnknownVerb_ReturnsUnknownWithBodyPreserved()
    {
        var c = AerospikeStatementClassifier.Classify( "TRUNCATE test.users" );

        c.Kind.Should().Be( AerospikeStatementKind.Unknown );
        c.Body.Should().Be( "TRUNCATE test.users" );
        c.Detail.Should().NotBeNull();
        c.Namespace.Should().BeNull();
        c.SetName.Should().BeNull();
    }

    [TestMethod]
    public void Classify_SyntaxError_ReturnsUnknownWithDetail()
    {
        var c = AerospikeStatementClassifier.Classify( "CREATE INDEX missing_target" );

        c.Kind.Should().Be( AerospikeStatementKind.Unknown );
        c.Detail.Should().NotBeNull();
    }

    [TestMethod]
    public void Classify_EmptyInput_ReturnsUnknown()
    {
        var c = AerospikeStatementClassifier.Classify( "" );

        c.Kind.Should().Be( AerospikeStatementKind.Unknown );
        c.Body.Should().Be( "" );
    }

    [TestMethod]
    public void Classify_NullInput_ReturnsUnknown()
    {
        var c = AerospikeStatementClassifier.Classify( null );

        c.Kind.Should().Be( AerospikeStatementKind.Unknown );
        c.Body.Should().Be( "" );
    }

    [TestMethod]
    public void Classify_WhitespaceOnly_ReturnsUnknown()
    {
        var c = AerospikeStatementClassifier.Classify( "   \n\t  " );

        c.Kind.Should().Be( AerospikeStatementKind.Unknown );
    }

    [TestMethod]
    public void Classify_CreateIndexWithOptionalFlags_PreservesKind()
    {
        // CREATE INDEX [IF NOT EXISTS] [RECREATE] [WAIT] ...
        var c = AerospikeStatementClassifier.Classify( "CREATE INDEX IF NOT EXISTS WAIT idx ON test.users (name) STRING" );

        c.Kind.Should().Be( AerospikeStatementKind.CreateIndex );
        c.Namespace.Should().Be( "test" );
        c.SetName.Should().Be( "users" );
        c.ObjectName.Should().Be( "idx" );
    }
}
