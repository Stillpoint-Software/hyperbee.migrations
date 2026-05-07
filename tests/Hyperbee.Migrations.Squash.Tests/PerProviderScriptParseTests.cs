using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 4 — per-provider grammar-lift verification (ParseScript ≡ ParseStatement).
//
// For each NoSQL provider, parse a representative statement set in BOTH the
// JSON-array shape (each statement parsed via ParseStatement) and the new
// script form (parsed via ParseScript). Assert AST equivalence.
//
// Each test uses the provider's own parser type — the squash test project
// references all 5 provider projects, so we can drive their parsers directly.

[TestClass]
public class PerProviderScriptParseTests
{
    [TestMethod]
    public void Aerospike_ParseScript_MatchesParseStatement()
    {
        var aql = """
            -- Phase 4 fixture: AQL CREATE INDEX equivalents
            CREATE INDEX WAIT idx_users_email ON test.users (email) STRING;
            CREATE INDEX WAIT idx_users_active ON test.users (active) NUMERIC;
            """;

        var parser = new Hyperbee.Migrations.Providers.Aerospike.Parsers.AerospikeStatementParser();

        var fromScript = parser.ParseScript( aql ).ToList();
        var fromIndividual = new[]
        {
            parser.ParseStatement( "CREATE INDEX WAIT idx_users_email ON test.users (email) STRING" ),
            parser.ParseStatement( "CREATE INDEX WAIT idx_users_active ON test.users (active) NUMERIC" )
        };

        fromScript.Should().HaveCount( 2 );
        for ( var i = 0; i < fromScript.Count; i++ )
        {
            fromScript[i].StatementType.Should().Be( fromIndividual[i].StatementType );
            fromScript[i].Namespace.Should().Be( fromIndividual[i].Namespace );
            fromScript[i].SetName.Should().Be( fromIndividual[i].SetName );
            fromScript[i].IndexName.Should().Be( fromIndividual[i].IndexName );
            fromScript[i].BinName.Should().Be( fromIndividual[i].BinName );
            fromScript[i].IndexType.Should().Be( fromIndividual[i].IndexType );
        }
    }

    [TestMethod]
    public void Couchbase_ParseScript_MatchesParseStatement()
    {
        var n1ql = """
            -- Phase 4 fixture: N1QL CREATE INDEX equivalents
            CREATE PRIMARY INDEX ON `default`;
            CREATE INDEX idx_email ON `default` (email);
            """;

        var parser = new Hyperbee.Migrations.Providers.Couchbase.Parsers.StatementParser();

        var fromScript = parser.ParseScript( n1ql ).ToList();
        var fromIndividual = new[]
        {
            parser.ParseStatement( "CREATE PRIMARY INDEX ON `default`" ),
            parser.ParseStatement( "CREATE INDEX idx_email ON `default` (email)" )
        };

        fromScript.Should().HaveCount( 2 );
        for ( var i = 0; i < fromScript.Count; i++ )
            fromScript[i].StatementType.Should().Be( fromIndividual[i].StatementType );
    }

    [TestMethod]
    public void MongoDB_ParseScript_MatchesParseStatement()
    {
        // Mongo grammar requires `database.collection` (dotted ref).
        var script = """
            // Phase 4 fixture: Mongo CREATE INDEX equivalents
            CREATE INDEX users_email_idx ON appdb.users (email);
            CREATE UNIQUE INDEX users_unique_id ON appdb.users (userId);
            """;

        var parser = new Hyperbee.Migrations.Providers.MongoDB.Parsers.MongoStatementParser();

        var fromScript = parser.ParseScript( script ).ToList();
        var fromIndividual = new[]
        {
            parser.ParseStatement( "CREATE INDEX users_email_idx ON appdb.users (email)" ),
            parser.ParseStatement( "CREATE UNIQUE INDEX users_unique_id ON appdb.users (userId)" )
        };

        fromScript.Should().HaveCount( 2 );
        for ( var i = 0; i < fromScript.Count; i++ )
            fromScript[i].StatementType.Should().Be( fromIndividual[i].StatementType );
    }

    [TestMethod]
    public void OpenSearch_ParseScript_MatchesParseStatement()
    {
        // OpenSearch verbs without inline bodies (Form 1 @path only). REFRESH
        // takes the index name directly, not the keyword INDEX.
        var script = """
            -- Phase 4 fixture: OpenSearch verbs without inline bodies (Form 1 only)
            CREATE INDEX logs_2024 WITH BODY @logs_2024.json;
            REFRESH logs_2024;
            """;

        var parser = new Hyperbee.Migrations.Providers.OpenSearch.Internal.Grammar.OpenSearchStatementParser();

        var fromScript = parser.ParseScript( script ).ToList();
        var fromIndividual = new[]
        {
            parser.Parse( "CREATE INDEX logs_2024 WITH BODY @logs_2024.json" ),
            parser.Parse( "REFRESH logs_2024" )
        };

        fromScript.Should().HaveCount( 2 );
        for ( var i = 0; i < fromScript.Count; i++ )
            fromScript[i].Verb.Should().Be( fromIndividual[i].Verb );
    }

    [TestMethod]
    public void Aerospike_ParseScript_HandlesCommentsAndWhitespace()
    {
        // Comments and blank lines must be invisible to the parser.
        var script = """
            -- top comment with ; semicolon

            /* block
               comment */

            CREATE INDEX idx_a ON ns.s (b) STRING;

            // another comment
            CREATE INDEX idx_b ON ns.s (c) NUMERIC;
            """;
        var parser = new Hyperbee.Migrations.Providers.Aerospike.Parsers.AerospikeStatementParser();
        var items = parser.ParseScript( script ).ToList();
        items.Should().HaveCount( 2 );
        items[0].IndexName.Should().Be( "idx_a" );
        items[1].IndexName.Should().Be( "idx_b" );
    }
}
