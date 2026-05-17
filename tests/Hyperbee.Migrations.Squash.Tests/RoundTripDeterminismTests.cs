using FluentAssertions;
using Hyperbee.Migrations.Resources;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 8 Task 8.3 — round-trip determinism gate per ADR-0022.
//
// Verifies that for the universal .pql script form, the canonical
// pipeline parse -> re-emit -> re-parse produces an AST-equivalent result
// (same statement texts, in the same order, with the same per-provider
// classification).
//
// V1 canonical formatter for script form: re-join the splitter's output with
// '; \n' separators + a trailing semicolon. This is the form the squash
// codegen emits when it materializes a generated squash migration's
// resource body. The gate fires when canonical re-emission would produce
// a different statement set on re-parse, which catches splitter / parser
// regressions before they reach generation.

[TestClass]
public class RoundTripDeterminismTests
{
    [TestMethod]
    public void Splitter_RoundTrip_PreservesStatementSet()
    {
        // Mixed-feature script exercising every lexical surface.
        var script = """
            -- top comment
            CREATE TABLE app.t (id int);
            /* block comment with ; semicolon */
            INSERT INTO app.t VALUES ('hello; world');
            CREATE INDEX `idx;name` ON app.t (id);
            """;

        var first = ScriptStatementSplitter.Split( script );
        var reEmitted = ReEmit( first );
        var second = ScriptStatementSplitter.Split( reEmitted );

        second.Should().BeEquivalentTo( first,
            options => options.WithStrictOrdering(),
            "round-trip: parse + re-emit + re-parse must produce the same statement set" );
    }

    [TestMethod]
    public void Splitter_RoundTrip_OnRealAerospikeFixture()
    {
        var script = """
            CREATE INDEX WAIT idx_users_email ON test.users (email) STRING;
            CREATE INDEX WAIT idx_users_active ON test.users (active) NUMERIC;
            """;

        var first = ScriptStatementSplitter.Split( script );
        var second = ScriptStatementSplitter.Split( ReEmit( first ) );

        second.Should().BeEquivalentTo( first, options => options.WithStrictOrdering() );

        // Stronger check: per-provider parser produces equal classifications.
        var p = new Hyperbee.Migrations.Providers.Aerospike.Parsers.AerospikeStatementParser();
        var firstAsts = first.Select( p.ParseStatement ).ToList();
        var secondAsts = second.Select( p.ParseStatement ).ToList();
        for ( var i = 0; i < firstAsts.Count; i++ )
        {
            firstAsts[i].StatementType.Should().Be( secondAsts[i].StatementType );
            firstAsts[i].IndexName.Should().Be( secondAsts[i].IndexName );
        }
    }

    [TestMethod]
    public void Splitter_RoundTrip_DoesNotCorruptStringLiterals()
    {
        var script = """
            INSERT INTO t VALUES ('hello', 'with ''escaped'' quotes');
            INSERT INTO t VALUES ('semi; inside');
            """;

        var first = ScriptStatementSplitter.Split( script );
        var second = ScriptStatementSplitter.Split( ReEmit( first ) );

        second.Should().HaveCount( 2 );
        second[0].Should().Contain( "''escaped''" );
        second[1].Should().Contain( "semi; inside" );
    }

    [TestMethod]
    public void Splitter_RoundTrip_StablyDropsCommentsAndBlankLines()
    {
        // Comments are stripped on first parse; subsequent parses of the
        // re-emitted output should produce the same statement set (idempotent).
        var script = """
            -- comment 1
            CREATE TABLE app.a (id int);

            -- comment 2

            /* block */
            CREATE TABLE app.b (id int);
            """;

        var first = ScriptStatementSplitter.Split( script );
        var firstReEmit = ReEmit( first );
        var second = ScriptStatementSplitter.Split( firstReEmit );
        var secondReEmit = ReEmit( second );

        secondReEmit.Should().Be( firstReEmit, "re-emit should be byte-stable across rounds" );
    }

    [TestMethod]
    public void Splitter_RoundTrip_RespectsBacktickIdentifiers()
    {
        var script = "CREATE INDEX `idx with spaces` ON `db`.`table` (`bin`);";
        var first = ScriptStatementSplitter.Split( script );
        var second = ScriptStatementSplitter.Split( ReEmit( first ) );

        second.Should().BeEquivalentTo( first, options => options.WithStrictOrdering() );
        second[0].Should().Contain( "`idx with spaces`" );
    }

    // Canonical re-emission used by all round-trip tests above. The squash
    // codegen uses an equivalent shape when materializing a generated
    // squash's resource body — keeping the test gate aligned with the
    // generation path.
    private static string ReEmit( IReadOnlyList<string> statements ) =>
        string.Join( ";\n", statements ) + ";";
}
