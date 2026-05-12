using FluentAssertions;
using Hyperbee.Migrations.Providers.OpenSearch.Squash;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 2 Task 2.3: OpenSearchStatementClassifier unit coverage.
//
// The classifier is a thin projection over the existing OpenSearch grammar.
// Tests focus on:
//   - Kind extraction for each supported AST node
//   - ObjectName population per kind
//   - Composite verb (MIGRATE INDEX) flattening + Detail enumerating children
//   - WHEN VERSION wrapper retains wrapped verb in Detail
//   - Default-deny on parser failure / empty / null
//
// Grammar fidelity is exercised by the existing OpenSearch parser tests.

[TestClass]
public class OpenSearchStatementClassifierTests
{
    [TestMethod]
    public void Classify_CreateIndex_ExtractsName()
    {
        var c = OpenSearchStatementClassifier.Classify( "CREATE INDEX users" );

        c.Kind.Should().Be( OpenSearchStatementKind.CreateIndex );
        c.ObjectName.Should().Be( "users" );
        c.Body.Should().Be( "CREATE INDEX users" );
        c.Detail.Should().BeNull();
    }

    [TestMethod]
    public void Classify_CreateIndexWithBody_ExtractsName()
    {
        var c = OpenSearchStatementClassifier.Classify( "CREATE INDEX users WITH BODY @body.json" );

        c.Kind.Should().Be( OpenSearchStatementKind.CreateIndex );
        c.ObjectName.Should().Be( "users" );
    }

    [TestMethod]
    public void Classify_DropIndex_ExtractsName()
    {
        var c = OpenSearchStatementClassifier.Classify( "DROP INDEX users" );

        c.Kind.Should().Be( OpenSearchStatementKind.DropIndex );
        c.ObjectName.Should().Be( "users" );
    }

    [TestMethod]
    public void Classify_DropIndexIfExists()
    {
        var c = OpenSearchStatementClassifier.Classify( "DROP INDEX users IF EXISTS" );
        c.Kind.Should().Be( OpenSearchStatementKind.DropIndex );
        c.ObjectName.Should().Be( "users" );
    }

    [TestMethod]
    public void Classify_UpdateMapping_ExtractsIndexName()
    {
        var c = OpenSearchStatementClassifier.Classify( "UPDATE MAPPING ON users WITH BODY @m.json" );

        c.Kind.Should().Be( OpenSearchStatementKind.UpdateMapping );
        c.ObjectName.Should().Be( "users" );
    }

    [TestMethod]
    public void Classify_UpdateSettings_ExtractsIndexName()
    {
        var c = OpenSearchStatementClassifier.Classify( "UPDATE SETTINGS ON users WITH BODY @s.json" );

        c.Kind.Should().Be( OpenSearchStatementKind.UpdateSettings );
        c.ObjectName.Should().Be( "users" );
    }

    [TestMethod]
    public void Classify_Refresh_ExtractsIndexName()
    {
        var c = OpenSearchStatementClassifier.Classify( "REFRESH users" );
        c.Kind.Should().Be( OpenSearchStatementKind.Refresh );
        c.ObjectName.Should().Be( "users" );
    }

    [TestMethod]
    public void Classify_WaitForHealth_ExtractsIndexName()
    {
        var c = OpenSearchStatementClassifier.Classify( "WAIT FOR GREEN ON users TIMEOUT 30s" );

        c.Kind.Should().Be( OpenSearchStatementKind.WaitForHealth );
        c.ObjectName.Should().Be( "users" );
    }

    [TestMethod]
    public void Classify_WaitForHealth_WithoutIndex_ObjectNameNull()
    {
        var c = OpenSearchStatementClassifier.Classify( "WAIT FOR GREEN TIMEOUT 30s" );

        c.Kind.Should().Be( OpenSearchStatementKind.WaitForHealth );
        c.ObjectName.Should().BeNull();
    }

    [TestMethod]
    public void Classify_WaitUntilTask_ExtractsTaskId()
    {
        // Task IDs round-trip through the parser's identifier rule (plain
        // or backtick-quoted). The single-quote string form is not part of
        // the grammar.
        var c = OpenSearchStatementClassifier.Classify( "WAIT UNTIL TASK `task-abc-123` COMPLETE" );

        c.Kind.Should().Be( OpenSearchStatementKind.WaitUntilTask );
        c.ObjectName.Should().Be( "task-abc-123" );
    }

    [TestMethod]
    public void Classify_Reindex_ExtractsDestination()
    {
        // Per the AST shape: ObjectName carries the destination (the
        // post-reindex state); diagnostics that need the source consult
        // the AST directly via a re-parse.
        var c = OpenSearchStatementClassifier.Classify( "REINDEX FROM users_v1 TO users_v2" );

        c.Kind.Should().Be( OpenSearchStatementKind.Reindex );
        c.ObjectName.Should().Be( "users_v2" );
    }

    [TestMethod]
    public void Classify_AliasSwap_ExtractsAlias()
    {
        var c = OpenSearchStatementClassifier.Classify( "ALIAS SWAP current FROM users_v1 TO users_v2" );

        c.Kind.Should().Be( OpenSearchStatementKind.AliasSwap );
        c.ObjectName.Should().Be( "current" );
    }

    [TestMethod]
    public void Classify_AliasAdd_ExtractsAlias()
    {
        var c = OpenSearchStatementClassifier.Classify( "ALIAS ADD current ON users_v2" );
        c.Kind.Should().Be( OpenSearchStatementKind.AliasAdd );
        c.ObjectName.Should().Be( "current" );
    }

    [TestMethod]
    public void Classify_AliasRemove_ExtractsAlias()
    {
        var c = OpenSearchStatementClassifier.Classify( "ALIAS REMOVE current ON users_v1" );
        c.Kind.Should().Be( OpenSearchStatementKind.AliasRemove );
        c.ObjectName.Should().Be( "current" );
    }

    [TestMethod]
    public void Classify_CreateTemplate_ExtractsName()
    {
        var c = OpenSearchStatementClassifier.Classify( "CREATE TEMPLATE my-template WITH BODY @t.json" );
        c.Kind.Should().Be( OpenSearchStatementKind.CreateTemplate );
        c.ObjectName.Should().Be( "my-template" );
    }

    [TestMethod]
    public void Classify_CreateComponent_ExtractsName()
    {
        var c = OpenSearchStatementClassifier.Classify( "CREATE COMPONENT my-component WITH BODY @c.json" );
        c.Kind.Should().Be( OpenSearchStatementKind.CreateComponent );
        c.ObjectName.Should().Be( "my-component" );
    }

    [TestMethod]
    public void Classify_DropTemplate_ExtractsName()
    {
        var c = OpenSearchStatementClassifier.Classify( "DROP TEMPLATE my-template" );
        c.Kind.Should().Be( OpenSearchStatementKind.DropTemplate );
        c.ObjectName.Should().Be( "my-template" );
    }

    [TestMethod]
    public void Classify_DropComponent_ExtractsName()
    {
        var c = OpenSearchStatementClassifier.Classify( "DROP COMPONENT my-component" );
        c.Kind.Should().Be( OpenSearchStatementKind.DropComponent );
        c.ObjectName.Should().Be( "my-component" );
    }

    [TestMethod]
    public void Classify_CreatePolicy_ExtractsPolicyId()
    {
        var c = OpenSearchStatementClassifier.Classify( "CREATE POLICY hot-warm-delete WITH BODY @p.json" );
        c.Kind.Should().Be( OpenSearchStatementKind.CreatePolicy );
        c.ObjectName.Should().Be( "hot-warm-delete" );
    }

    [TestMethod]
    public void Classify_ApplyPolicy_ExtractsPolicyId()
    {
        var c = OpenSearchStatementClassifier.Classify( "APPLY POLICY hot-warm-delete TO logs-*" );

        c.Kind.Should().Be( OpenSearchStatementKind.ApplyPolicy );
        c.ObjectName.Should().Be( "hot-warm-delete" );
    }

    [TestMethod]
    public void Classify_MigrateIndex_FlattensToComposite_WithChildDetail()
    {
        // MIGRATE INDEX is parsed into a CompositeStatementAst with multiple
        // child statements (CREATE, REINDEX, ALIAS SWAP). The classifier
        // surfaces the composite at the top level with child verbs in Detail.
        var c = OpenSearchStatementClassifier.Classify(
            "MIGRATE INDEX users_v1 TO users_v2 WITH BODY @m.json VIA ALIAS current" );

        c.Kind.Should().Be( OpenSearchStatementKind.Composite );
        c.ObjectName.Should().Be( "MIGRATE INDEX" );
        c.Detail.Should().NotBeNull();
        c.Detail.Should().Contain( "child statement" );
        c.Detail.Should().Contain( "CREATE INDEX" );
        c.Detail.Should().Contain( "REINDEX" );
    }

    [TestMethod]
    public void Classify_MigrateIndex_WithTemplate_FlattensToComposite()
    {
        // Companion test exercising the `WITH TEMPLATE` form -- earlier
        // versions of the parser routed only this form correctly because
        // the body-source slot was typed as `BodyRef?` (concrete) rather
        // than `BodySource?` (abstract). The fix lifts the tuple type so
        // both forms round-trip.
        var c = OpenSearchStatementClassifier.Classify(
            "MIGRATE INDEX users_v1 TO users_v2 WITH TEMPLATE users-template VIA ALIAS current" );

        c.Kind.Should().Be( OpenSearchStatementKind.Composite );
        c.ObjectName.Should().Be( "MIGRATE INDEX" );
        c.Detail.Should().Contain( "CREATE INDEX" );
    }

    [TestMethod]
    public void Classify_MigrateIndex_WithSiblingBodyRef_FlattensToComposite()
    {
        // Sibling-property body form (`WITH BODY $name`).
        var c = OpenSearchStatementClassifier.Classify(
            "MIGRATE INDEX users_v1 TO users_v2 WITH BODY $body" );

        c.Kind.Should().Be( OpenSearchStatementKind.Composite );
        c.ObjectName.Should().Be( "MIGRATE INDEX" );
    }

    [TestMethod]
    public void Classify_WhenVersion_RetainsWrappedVerbInDetail()
    {
        var c = OpenSearchStatementClassifier.Classify( "WHEN VERSION >= '2.10' CREATE INDEX users" );

        c.Kind.Should().Be( OpenSearchStatementKind.WhenVersion );
        // Wrapped object name (the index) flows through.
        c.ObjectName.Should().Be( "users" );
        c.Detail.Should().NotBeNull();
        c.Detail.Should().Contain( "WHEN VERSION" );
        c.Detail.Should().Contain( "2.10" );
        c.Detail.Should().Contain( "CREATE INDEX" );
    }

    [TestMethod]
    public void Classify_UnknownVerb_ReturnsUnknownWithBodyPreservedAndDetail()
    {
        var c = OpenSearchStatementClassifier.Classify( "SHUTDOWN NOW" );

        c.Kind.Should().Be( OpenSearchStatementKind.Unknown );
        c.Body.Should().Be( "SHUTDOWN NOW" );
        c.Detail.Should().NotBeNull();
        c.Detail.Should().Contain( "Unable to parse" );
    }

    [TestMethod]
    public void Classify_SyntaxError_ReturnsUnknownWithDetail()
    {
        // Missing required clause -- parser surfaces via OpenSearchParseException
        // which the classifier wraps into Detail.
        var c = OpenSearchStatementClassifier.Classify( "CREATE INDEX" );

        c.Kind.Should().Be( OpenSearchStatementKind.Unknown );
        c.Detail.Should().NotBeNull();
    }

    [TestMethod]
    public void Classify_EmptyInput_ReturnsUnknown()
    {
        var c = OpenSearchStatementClassifier.Classify( "" );
        c.Kind.Should().Be( OpenSearchStatementKind.Unknown );
        c.Body.Should().Be( "" );
    }

    [TestMethod]
    public void Classify_NullInput_ReturnsUnknown()
    {
        var c = OpenSearchStatementClassifier.Classify( null );
        c.Kind.Should().Be( OpenSearchStatementKind.Unknown );
        c.Body.Should().Be( "" );
    }

    [TestMethod]
    public void Classify_WhitespaceOnly_ReturnsUnknown()
    {
        var c = OpenSearchStatementClassifier.Classify( "   \n\t  " );
        c.Kind.Should().Be( OpenSearchStatementKind.Unknown );
    }
}
