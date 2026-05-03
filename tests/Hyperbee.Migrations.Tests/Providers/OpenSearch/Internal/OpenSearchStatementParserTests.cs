using FluentAssertions;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Ast;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Grammar;

namespace Hyperbee.Migrations.Tests.Providers.OpenSearch.Internal;

[TestClass]
public class OpenSearchStatementParserTests
{
    private readonly OpenSearchStatementParser _parser = new();

    // CREATE INDEX positive cases

    [TestMethod]
    public void CreateIndex_BarePlainName_Parses()
    {
        var ast = _parser.Parse( "CREATE INDEX users" );

        ast.Should().BeOfType<CreateIndexAst>();
        var c = (CreateIndexAst) ast;
        c.IndexName.Should().Be( "users" );
        c.IfNotExists.Should().BeFalse();
        c.Body.Should().BeNull();
        c.InjectDynamicStrict.Should().BeTrue();
    }

    [TestMethod]
    public void CreateIndex_BacktickName_StripsBackticks()
    {
        var ast = _parser.Parse( "CREATE INDEX `users-v2`" );

        var c = (CreateIndexAst) ast;
        c.IndexName.Should().Be( "users-v2" );
    }

    [TestMethod]
    public void CreateIndex_DashedNameWithoutBackticks_Parses()
    {
        var ast = _parser.Parse( "CREATE INDEX users-v2" );

        var c = (CreateIndexAst) ast;
        c.IndexName.Should().Be( "users-v2" );
    }

    [TestMethod]
    public void CreateIndex_IfNotExists_FlagsTrue()
    {
        var ast = _parser.Parse( "CREATE INDEX users IF NOT EXISTS" );

        var c = (CreateIndexAst) ast;
        c.IfNotExists.Should().BeTrue();
    }

    [TestMethod]
    public void CreateIndex_WithBody_CapturesBodyRef()
    {
        var ast = _parser.Parse( "CREATE INDEX users WITH BODY $usersIndex" );

        var c = (CreateIndexAst) ast;
        c.Body.Should().NotBeNull();
        c.Body.Should().BeOfType<BodyRef>().Which.Name.Should().Be( "usersIndex" );
    }

    [TestMethod]
    public void CreateIndex_AllOptions_Composes()
    {
        var ast = _parser.Parse( "CREATE INDEX `users-v2` IF NOT EXISTS WITH BODY $body" );

        var c = (CreateIndexAst) ast;
        c.IndexName.Should().Be( "users-v2" );
        c.IfNotExists.Should().BeTrue();
        c.Body.Should().BeOfType<BodyRef>().Which.Name.Should().Be( "body" );
    }

    [TestMethod]
    public void CreateIndex_KeywordsCaseInsensitive_Parses()
    {
        var ast = _parser.Parse( "create index users if not exists with body $b" );

        ast.Should().BeOfType<CreateIndexAst>();
    }

    // REINDEX positive cases

    [TestMethod]
    public void Reindex_BareFromTo_InjectsOpTypeCreate()
    {
        var ast = _parser.Parse( "REINDEX FROM users TO users-v2" );

        ast.Should().BeOfType<ReindexAst>();
        var r = (ReindexAst) ast;
        r.Source.Should().Be( "users" );
        r.Destination.Should().Be( "users-v2" );
        r.Body.Should().BeNull();
        r.InjectOpTypeCreate.Should().BeTrue();
        r.UnsafeJustification.Should().BeNull();
    }

    [TestMethod]
    public void Reindex_WithBody_CapturesBodyRef()
    {
        var ast = _parser.Parse( "REINDEX FROM users TO users-v2 WITH BODY $reindexBody" );

        var r = (ReindexAst) ast;
        r.Body.Should().BeOfType<BodyRef>().Which.Name.Should().Be( "reindexBody" );
    }

    [TestMethod]
    public void Reindex_UnsafeWithJustification_DisablesInjection()
    {
        var ast = _parser.Parse( "REINDEX UNSAFE(\"OPS-1234 reason: re-write idempotent script\") FROM users TO users-v2" );

        var r = (ReindexAst) ast;
        r.InjectOpTypeCreate.Should().BeFalse();
        r.UnsafeJustification.Should().Be( "OPS-1234 reason: re-write idempotent script" );
    }

    [TestMethod]
    public void Reindex_BacktickedNames_StripBackticks()
    {
        var ast = _parser.Parse( "REINDEX FROM `users.v1` TO `users.v2`" );

        var r = (ReindexAst) ast;
        r.Source.Should().Be( "users.v1" );
        r.Destination.Should().Be( "users.v2" );
    }

    // Negative cases

    [TestMethod]
    public void Parse_NullStatement_Throws()
    {
        var act = () => _parser.Parse( null! );

        act.Should().Throw<ArgumentException>();
    }

    [TestMethod]
    public void Parse_WhitespaceOnly_Throws()
    {
        var act = () => _parser.Parse( "   " );

        act.Should().Throw<ArgumentException>();
    }

    [TestMethod]
    public void Parse_UnknownVerb_Throws()
    {
        var act = () => _parser.Parse( "DROP TABLE users" );

        act.Should().Throw<OpenSearchParseException>()
            .WithMessage( "*DROP TABLE users*" );
    }

    [TestMethod]
    public void CreateIndex_MissingName_Throws()
    {
        var act = () => _parser.Parse( "CREATE INDEX" );

        act.Should().Throw<OpenSearchParseException>();
    }

    [TestMethod]
    public void Reindex_MissingTo_Throws()
    {
        var act = () => _parser.Parse( "REINDEX FROM users" );

        act.Should().Throw<OpenSearchParseException>();
    }

    [TestMethod]
    public void Reindex_BareUnsafeWithoutJustification_Throws()
    {
        var act = () => _parser.Parse( "REINDEX UNSAFE FROM users TO users-v2" );

        act.Should().Throw<OpenSearchParseException>();
    }

    [TestMethod]
    public void Reindex_UnsafeEmptyJustification_Throws()
    {
        // Empty quoted string violates the non-empty-justification rule per R-18.
        var act = () => _parser.Parse( "REINDEX UNSAFE(\"\") FROM users TO users-v2" );

        // Either the parser rejects, or the justification predicate throws — both acceptable.
        act.Should().Throw<Exception>();
    }
}
