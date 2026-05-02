#nullable enable
using FluentAssertions;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Ast;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Grammar;

namespace Hyperbee.Migrations.Tests.Providers.OpenSearch.Internal;

[TestClass]
public class WhenVersionTests
{
    private readonly OpenSearchStatementParser _parser = new();

    // ---- parser ----

    [TestMethod]
    public void WhenVersion_GreaterThanOrEqual_WrapsCreateIndex()
    {
        var ast = _parser.Parse( "WHEN VERSION >= '2.10' CREATE INDEX users" );

        var w = (WhenVersionAst) ast;
        w.Op.Should().Be( VersionComparator.GtEq );
        w.Version.Should().Be( new Version( 2, 10 ) );
        w.Child.Should().BeOfType<CreateIndexAst>();
    }

    [TestMethod]
    public void WhenVersion_AllSixComparators_Parse()
    {
        var samples = new (string op, VersionComparator expected)[]
        {
            ("=",  VersionComparator.Eq),
            ("!=", VersionComparator.NotEq),
            ("<",  VersionComparator.Lt),
            ("<=", VersionComparator.LtEq),
            (">",  VersionComparator.Gt),
            (">=", VersionComparator.GtEq)
        };

        foreach ( var (op, expected) in samples )
        {
            var ast = (WhenVersionAst) _parser.Parse( $"WHEN VERSION {op} '2.10' DROP INDEX users" );
            ast.Op.Should().Be( expected, because: $"`{op}` should map to {expected}" );
        }
    }

    [TestMethod]
    public void WhenVersion_TwoComponentVersion_Parses()
    {
        var ast = (WhenVersionAst) _parser.Parse( "WHEN VERSION = '2.10' REFRESH users" );
        ast.Version.Should().Be( new Version( 2, 10 ) );
    }

    [TestMethod]
    public void WhenVersion_ThreeComponentVersion_Parses()
    {
        var ast = (WhenVersionAst) _parser.Parse( "WHEN VERSION = '2.10.1' REFRESH users" );
        ast.Version.Should().Be( new Version( 2, 10, 1 ) );
    }

    [TestMethod]
    public void WhenVersion_KeywordsCaseInsensitive_Parses()
    {
        var ast = _parser.Parse( "when version >= '2.10' create index users" );
        ast.Should().BeOfType<WhenVersionAst>();
    }

    [TestMethod]
    public void WhenVersion_WrapsAnyChildStatement()
    {
        // Sanity: WHEN VERSION should compose with several different children
        // — not just the simple bare-name verbs.
        _parser.Parse( "WHEN VERSION >= '2.10' DROP INDEX users IF EXISTS" )
            .Should().BeOfType<WhenVersionAst>();
        _parser.Parse( "WHEN VERSION >= '2.10' UPDATE MAPPING ON users WITH BODY $body" )
            .Should().BeOfType<WhenVersionAst>();
        _parser.Parse( "WHEN VERSION >= '2.10' MIGRATE INDEX users-v1 TO users-v2 WITH TEMPLATE t VIA ALIAS users" )
            .Should().BeOfType<WhenVersionAst>();
    }

    // ---- v1 suffix rejection (R-15a documented rule) ----

    [TestMethod]
    public void WhenVersion_PreReleaseSuffix_RejectedAtParseTime_WithRemediation()
    {
        var act = () => _parser.Parse( "WHEN VERSION = '2.11.0-SNAPSHOT' DROP INDEX users" );
        act.Should().Throw<Exception>()
            .Where( ex => ex is OpenSearchParseException || ex is InvalidOperationException )
            .Where( ex => ex.Message.Contains( "SNAPSHOT" ) || ex.Message.Contains( "pre-release" ) || ex.Message.Contains( "MAJOR.MINOR" ) );
    }

    [TestMethod]
    public void WhenVersion_RcSuffix_RejectedAtParseTime()
    {
        var act = () => _parser.Parse( "WHEN VERSION > '2.11.0-rc1' DROP INDEX users" );
        act.Should().Throw<Exception>()
            .Where( ex => ex is OpenSearchParseException || ex is InvalidOperationException );
    }

    [TestMethod]
    public void WhenVersion_AwsOpenSearchPrefix_RejectedAtParseTime()
    {
        var act = () => _parser.Parse( "WHEN VERSION >= 'OpenSearch_2.11' DROP INDEX users" );
        act.Should().Throw<Exception>()
            .Where( ex => ex is OpenSearchParseException || ex is InvalidOperationException );
    }

    [TestMethod]
    public void WhenVersion_FourComponentVersion_RejectedAtParseTime()
    {
        var act = () => _parser.Parse( "WHEN VERSION = '2.10.1.2' DROP INDEX users" );
        act.Should().Throw<Exception>()
            .Where( ex => ex is OpenSearchParseException || ex is InvalidOperationException );
    }

    [TestMethod]
    public void WhenVersion_OneComponentVersion_RejectedAtParseTime()
    {
        var act = () => _parser.Parse( "WHEN VERSION = '2' DROP INDEX users" );
        act.Should().Throw<Exception>()
            .Where( ex => ex is OpenSearchParseException || ex is InvalidOperationException );
    }

    [TestMethod]
    public void WhenVersion_EmptyVersionLiteral_RejectedAtParseTime()
    {
        var act = () => _parser.Parse( "WHEN VERSION = '' DROP INDEX users" );
        act.Should().Throw<Exception>();
    }

    // ---- AST.Evaluate: semver comparison correctness (R-15a metric) ----

    [TestMethod]
    public void Evaluate_2_9_LessThan_2_10_IsTrue_ProvingSemverNotLexical()
    {
        // R-15a load-bearing case: lexical comparison says '2.9' > '2.10'
        // (because '9' > '1' as a character). We need numeric comparison.
        var ast = MakeWhen( VersionComparator.Lt, new Version( 2, 10 ) );
        ast.Evaluate( new Version( 2, 9 ) ).Should().BeTrue(
            because: "semver comparison must treat 2.9 < 2.10; lexical sort would invert this" );
    }

    [TestMethod]
    public void Evaluate_2_10_NormalizesEquivalentTo_2_10_0()
    {
        // R-15a metric: '2.10.0' = '2.10'. System.Version's default treats
        // missing components as -1 so 2.10 != 2.10.0 by default; the AST's
        // Evaluate normalizes both sides to .0.0 before comparing.
        var astTwoDot = MakeWhen( VersionComparator.Eq, new Version( 2, 10 ) );
        astTwoDot.Evaluate( new Version( 2, 10, 0 ) ).Should().BeTrue();

        var astThreeDot = MakeWhen( VersionComparator.Eq, new Version( 2, 10, 0 ) );
        astThreeDot.Evaluate( new Version( 2, 10 ) ).Should().BeTrue();
    }

    [TestMethod]
    public void Evaluate_AllComparators_Work()
    {
        var cluster = new Version( 2, 10, 0 );

        MakeWhen( VersionComparator.Eq,    new Version( 2, 10 ) ).Evaluate( cluster ).Should().BeTrue();
        MakeWhen( VersionComparator.Eq,    new Version( 2, 11 ) ).Evaluate( cluster ).Should().BeFalse();

        MakeWhen( VersionComparator.NotEq, new Version( 2, 10 ) ).Evaluate( cluster ).Should().BeFalse();
        MakeWhen( VersionComparator.NotEq, new Version( 2, 11 ) ).Evaluate( cluster ).Should().BeTrue();

        MakeWhen( VersionComparator.Lt,    new Version( 2, 11 ) ).Evaluate( cluster ).Should().BeTrue();
        MakeWhen( VersionComparator.Lt,    new Version( 2, 10 ) ).Evaluate( cluster ).Should().BeFalse();

        MakeWhen( VersionComparator.LtEq,  new Version( 2, 10 ) ).Evaluate( cluster ).Should().BeTrue();
        MakeWhen( VersionComparator.LtEq,  new Version( 2, 9 ) ).Evaluate( cluster ).Should().BeFalse();

        MakeWhen( VersionComparator.Gt,    new Version( 2, 9 ) ).Evaluate( cluster ).Should().BeTrue();
        MakeWhen( VersionComparator.Gt,    new Version( 2, 10 ) ).Evaluate( cluster ).Should().BeFalse();

        MakeWhen( VersionComparator.GtEq,  new Version( 2, 10 ) ).Evaluate( cluster ).Should().BeTrue();
        MakeWhen( VersionComparator.GtEq,  new Version( 2, 11 ) ).Evaluate( cluster ).Should().BeFalse();
    }

    [TestMethod]
    public void Evaluate_PatchLevelDifferences_Compare()
    {
        // Differentiating across patch versions matters for "feature requires
        // 2.10.3+ bug fix" guards.
        var cluster = new Version( 2, 10, 2 );
        MakeWhen( VersionComparator.Lt, new Version( 2, 10, 3 ) ).Evaluate( cluster ).Should().BeTrue();
        MakeWhen( VersionComparator.GtEq, new Version( 2, 10, 3 ) ).Evaluate( cluster ).Should().BeFalse();
    }

    [TestMethod]
    public void Evaluate_NullClusterVersion_Throws()
    {
        var ast = MakeWhen( VersionComparator.Eq, new Version( 2, 10 ) );
        var act = () => ast.Evaluate( null! );
        act.Should().Throw<ArgumentNullException>();
    }

    private static WhenVersionAst MakeWhen( VersionComparator op, Version version )
    {
        var child = new RefreshAst( IndexName: "x" );
        return new WhenVersionAst( op, version, child );
    }
}
