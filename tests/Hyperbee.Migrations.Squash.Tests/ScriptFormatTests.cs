using FluentAssertions;
using Hyperbee.Migrations.Resources;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 4 — Script-Format Resource Support (ADR-0022)
//
// Provider-independent tests for the universal script-form scaffolding:
//   - ResourceFormatDetector classifies by extension
//   - ScriptStatementSplitter handles `;` boundaries and lexical features
//
// Per-provider grammar-lift tests (each ParseScript) live alongside the
// provider's own statement-parser tests; this file covers the shared layer.

[TestClass]
public class ScriptFormatTests
{
    // -----------------------------------------------------------------
    // ResourceFormatDetector
    // -----------------------------------------------------------------

    [TestMethod]
    public void Detector_StatementsJson_ClassifiesAsJsonArray()
    {
        ResourceFormatDetector.Classify( "statements.json" ).Should().Be( ResourceFormat.JsonArray );
        ResourceFormatDetector.Classify( "Migration_1000.statements.json" ).Should().Be( ResourceFormat.JsonArray );
        ResourceFormatDetector.Classify( "STATEMENTS.JSON" ).Should().Be( ResourceFormat.JsonArray );
    }

    [TestMethod]
    public void Detector_Pql_ClassifiesAsScript()
    {
        ResourceFormatDetector.Classify( "create-indexes.pql" ).Should().Be( ResourceFormat.Script );
        ResourceFormatDetector.Classify( "schema.pql" ).Should().Be( ResourceFormat.Script );
        ResourceFormatDetector.Classify( "MIGRATION.PQL" ).Should().Be( ResourceFormat.Script );
    }

    [TestMethod]
    public void Detector_BareStatements_IsNotRecognized()
    {
        // The bare `.statements` script extension (v3.0 pre-release only,
        // never shipped) was replaced by `.pql`. It must NOT classify as
        // Script; only `.statements.json` (legacy JSON) is still honored.
        var act = () => ResourceFormatDetector.Classify( "create-indexes.statements" );
        act.Should().Throw<MigrationException>().WithMessage( "*Unrecognized resource extension*" );
    }

    [TestMethod]
    public void Detector_Sql_ClassifiesAsScript()
    {
        ResourceFormatDetector.Classify( "schema.sql" ).Should().Be( ResourceFormat.Script );
        ResourceFormatDetector.Classify( "MIGRATION.SQL" ).Should().Be( ResourceFormat.Script );
    }

    [TestMethod]
    public void Detector_StatementsJson_ClassifiesAsJsonArrayNotScript()
    {
        // The `.json` branch is evaluated first so a compound
        // `*.statements.json` always classifies as the legacy JSON-array
        // form and never falls through to the script branch.
        ResourceFormatDetector.Classify( "foo.statements.json" ).Should().Be( ResourceFormat.JsonArray );
    }

    [TestMethod]
    public void Detector_UnknownExtension_Throws()
    {
        var act = () => ResourceFormatDetector.Classify( "schema.txt" );
        act.Should().Throw<MigrationException>().WithMessage( "*Unrecognized resource extension*" );
    }

    [TestMethod]
    public void Detector_NullOrEmpty_Throws()
    {
        var actNull = () => ResourceFormatDetector.Classify( null! );
        actNull.Should().Throw<ArgumentException>();

        var actEmpty = () => ResourceFormatDetector.Classify( "" );
        actEmpty.Should().Throw<ArgumentException>();
    }

    // -----------------------------------------------------------------
    // ScriptStatementSplitter
    // -----------------------------------------------------------------

    [TestMethod]
    public void Splitter_SimpleSemicolons_ProducesNStatements()
    {
        var script = "SELECT 1; SELECT 2; SELECT 3;";
        var parts = ScriptStatementSplitter.Split( script );

        parts.Should().BeEquivalentTo( new[] { "SELECT 1", "SELECT 2", "SELECT 3" } );
    }

    [TestMethod]
    public void Splitter_TrailingTerminatorOptional()
    {
        var withTrailing = ScriptStatementSplitter.Split( "SELECT 1;" );
        var withoutTrailing = ScriptStatementSplitter.Split( "SELECT 1" );
        withTrailing.Should().BeEquivalentTo( withoutTrailing );
    }

    [TestMethod]
    public void Splitter_LineComments_AreNotStatementBoundaries()
    {
        // -- and // line comments may contain `;`; the splitter must NOT split there.
        var script = """
            -- comment with ; semicolon
            SELECT 1;
            // another ; here
            SELECT 2;
            """;
        var parts = ScriptStatementSplitter.Split( script );
        parts.Should().HaveCount( 2 );
        parts[0].Should().Contain( "SELECT 1" );
        parts[1].Should().Contain( "SELECT 2" );
    }

    [TestMethod]
    public void Splitter_BlockComments_AreNotStatementBoundaries()
    {
        var script = """
            /* multi-line
               comment with ; semicolons inside */
            SELECT 1;
            SELECT 2 /* trailing ; comment */;
            """;
        var parts = ScriptStatementSplitter.Split( script );
        parts.Should().HaveCount( 2 );
    }

    [TestMethod]
    public void Splitter_SingleQuotedString_HasInnerSemicolonsRespected()
    {
        var script = """
            INSERT INTO t VALUES ('hello; world; embedded');
            INSERT INTO t VALUES ('it''s an apostrophe');
            """;
        var parts = ScriptStatementSplitter.Split( script );
        parts.Should().HaveCount( 2 );
        parts[0].Should().Contain( "hello; world; embedded" );
        parts[1].Should().Contain( "it''s an apostrophe" );
    }

    [TestMethod]
    public void Splitter_DoubleQuotedString_HasInnerSemicolonsRespected()
    {
        var script = """db.col.find({"q": "a; b; c"});""";
        var parts = ScriptStatementSplitter.Split( script );
        parts.Should().HaveCount( 1 );
        parts[0].Should().Contain( "a; b; c" );
    }

    [TestMethod]
    public void Splitter_BackticksRespected()
    {
        var script = "CREATE INDEX `idx;name` ON t(`bin`);";
        var parts = ScriptStatementSplitter.Split( script );
        parts.Should().HaveCount( 1 );
        parts[0].Should().Contain( "idx;name" );
    }

    [TestMethod]
    public void Splitter_NestedBlockComments()
    {
        // Postgres allows nested block comments. The splitter must track depth.
        var script = """
            /* outer /* inner */ outer */
            SELECT 1;
            """;
        var parts = ScriptStatementSplitter.Split( script );
        parts.Should().HaveCount( 1 );
        parts[0].Should().Contain( "SELECT 1" );
    }

    [TestMethod]
    public void Splitter_EmptyAndCommentOnlySegments_AreDropped()
    {
        var script = """
            -- only a comment
            ;;;
            SELECT 1;
            -- another comment
            ;
            """;
        var parts = ScriptStatementSplitter.Split( script );
        parts.Should().HaveCount( 1 );
        parts[0].Should().Contain( "SELECT 1" );
    }

    [TestMethod]
    public void Splitter_EmptyInput_ReturnsEmpty()
    {
        ScriptStatementSplitter.Split( "" ).Should().BeEmpty();
        ScriptStatementSplitter.Split( "   \n\t  " ).Should().BeEmpty();
        ScriptStatementSplitter.Split( "-- only comment\n" ).Should().BeEmpty();
    }

    [TestMethod]
    public void Splitter_NullInput_Throws()
    {
        var act = () => ScriptStatementSplitter.Split( null! );
        act.Should().Throw<ArgumentNullException>();
    }
}
