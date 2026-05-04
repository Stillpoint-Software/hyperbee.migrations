using FluentAssertions;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Ast;

namespace Hyperbee.Migrations.Tests.Providers.OpenSearch.Internal;

[TestClass]
public class AstTests
{
    [TestMethod]
    public void CreateIndexAst_VerbName_IsCreateIndex()
    {
        var ast = new CreateIndexAst( "users", IfNotExists: false, Body: null, InjectDynamicStrict: true );

        ast.Verb.Should().Be( "CREATE INDEX" );
    }

    [TestMethod]
    public void CreateIndexAst_RecordEquality_BasedOnAllProperties()
    {
        var a = new CreateIndexAst( "users", IfNotExists: true, Body: new BodyRef( "b" ), InjectDynamicStrict: true );
        var b = new CreateIndexAst( "users", IfNotExists: true, Body: new BodyRef( "b" ), InjectDynamicStrict: true );

        a.Should().Be( b );
    }

    [TestMethod]
    public void CreateIndexAst_RecordEquality_DiffersOnIfNotExists()
    {
        var a = new CreateIndexAst( "users", IfNotExists: true, Body: null, InjectDynamicStrict: true );
        var b = new CreateIndexAst( "users", IfNotExists: false, Body: null, InjectDynamicStrict: true );

        a.Should().NotBe( b );
    }

    [TestMethod]
    public void ReindexAst_VerbName_IsReindex()
    {
        var ast = new ReindexAst( "src", "dst", null, InjectOpTypeCreate: true, UnsafeJustification: null );

        ast.Verb.Should().Be( "REINDEX" );
    }

    [TestMethod]
    public void ReindexAst_UnsafeBranch_CarriesJustification()
    {
        var ast = new ReindexAst( "src", "dst", null, InjectOpTypeCreate: false, UnsafeJustification: "OPS-1234" );

        ast.InjectOpTypeCreate.Should().BeFalse();
        ast.UnsafeJustification.Should().Be( "OPS-1234" );
    }

    [TestMethod]
    public void BodyRef_RecordEquality_OnName()
    {
        new BodyRef( "x" ).Should().Be( new BodyRef( "x" ) );
        new BodyRef( "x" ).Should().NotBe( new BodyRef( "y" ) );
    }
}
