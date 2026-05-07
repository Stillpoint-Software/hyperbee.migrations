using FluentAssertions;
using Hyperbee.Migrations.Providers.Postgres.Squash;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 6 — Postgres production classifier tests.
//
// Mirrors the spike's coverage but exercises the productionized classifier in
// `Hyperbee.Migrations.Providers.Postgres.Squash`, which adds spike-discovered
// extensions: \restrict/\unrestrict strip, ALTER INDEX [ATTACH PARTITION],
// ALTER TABLE ADD CONSTRAINT, and the DROP family.
//
// Live pg_dump round-trip lives in `spikes/postgres-classifier/` and the
// upcoming Phase 7 integration tests.

[TestClass]
public class PostgresClassifierProductionTests
{
    [TestMethod]
    public void Splitter_StripsPgDump16PsqlDirectives()
    {
        var script =
            "\\restrict 4Pt21ZeOK\n" +
            "SET statement_timeout = 0;\n" +
            "CREATE TABLE t (id int);\n" +
            "\\unrestrict 4Pt21ZeOK\n";

        var statements = PostgresStatementSplitter.Split( script );

        statements.Should().HaveCount( 2 );
        statements[0].Should().Be( "SET statement_timeout = 0" );
        statements[1].Should().Be( "CREATE TABLE t (id int)" );
    }

    [TestMethod]
    public void Splitter_RespectsDollarQuotedFunctionBody()
    {
        var script = """
            CREATE FUNCTION f() RETURNS void LANGUAGE plpgsql AS $body$
            BEGIN
                -- comment with ; semicolon does NOT terminate; body is opaque
                RAISE NOTICE 'still inside';
            END;
            $body$;
            CREATE TABLE t (id int);
            """;

        var statements = PostgresStatementSplitter.Split( script );
        statements.Should().HaveCount( 2 );
        statements[0].Should().Contain( "BEGIN" ).And.Contain( "$body$" );
        statements[1].Should().Be( "CREATE TABLE t (id int)" );
    }

    [TestMethod]
    public void Splitter_RespectsNestedDollarQuotes()
    {
        var script = """
            CREATE FUNCTION x() RETURNS void LANGUAGE plpgsql AS $outer$
            BEGIN
                EXECUTE format($inner$SELECT 1; SELECT 2;$inner$);
            END;
            $outer$;
            """;

        var statements = PostgresStatementSplitter.Split( script );
        statements.Should().HaveCount( 1 );
        statements[0].Should().Contain( "$inner$SELECT 1; SELECT 2;$inner$" );
    }

    [TestMethod]
    public void Classifier_RecognizesAlterIndexAttachPartition()
    {
        var stmt = "ALTER INDEX app.feature_flag_pkey ATTACH PARTITION app.feature_flag_acme_pkey";
        var c = PostgresStatementClassifier.Classify( stmt );

        c.Kind.Should().Be( PostgresStatementKind.AlterIndexAttachPartition );
    }

    [TestMethod]
    public void Classifier_RecognizesAlterTableAddConstraint()
    {
        var stmt = "ALTER TABLE app.\"order\" ADD CONSTRAINT order_pkey PRIMARY KEY (id, placed_at)";
        var c = PostgresStatementClassifier.Classify( stmt );

        c.Kind.Should().Be( PostgresStatementKind.AlterTableAddConstraint );
        c.SchemaName.Should().Be( "app" );
        c.ObjectName.Should().Be( "order" );
    }

    [TestMethod]
    public void Classifier_RecognizesDropFamily()
    {
        PostgresStatementClassifier.Classify( "DROP TABLE app.t" ).Kind
            .Should().Be( PostgresStatementKind.DropTable );

        PostgresStatementClassifier.Classify( "DROP MATERIALIZED VIEW IF EXISTS app.mv" ).Kind
            .Should().Be( PostgresStatementKind.DropMaterializedView );

        PostgresStatementClassifier.Classify( "DROP FUNCTION app.f" ).Kind
            .Should().Be( PostgresStatementKind.DropFunction );

        PostgresStatementClassifier.Classify( "DROP EXTENSION pgcrypto" ).Kind
            .Should().Be( PostgresStatementKind.DropExtension );
    }

    [TestMethod]
    public void Classifier_RecognizesAllSquashWorkflowKinds()
    {
        // Spot-check the kinds the squash codegen actually relies on.
        var cases = new (string, PostgresStatementKind)[]
        {
            ("CREATE EXTENSION IF NOT EXISTS pgcrypto", PostgresStatementKind.CreateExtension),
            ("CREATE SCHEMA app", PostgresStatementKind.CreateSchema),
            ("CREATE TYPE app.status AS ENUM ('a','b')", PostgresStatementKind.CreateType),
            ("CREATE DOMAIN app.email AS text", PostgresStatementKind.CreateDomain),
            ("CREATE SEQUENCE app.s", PostgresStatementKind.CreateSequence),
            ("CREATE TABLE app.t (id int)", PostgresStatementKind.CreateTable),
            ("CREATE TABLE app.t_2025 PARTITION OF app.t FOR VALUES FROM ('2025-01-01') TO ('2026-01-01')", PostgresStatementKind.CreateTablePartitionOf),
            ("CREATE INDEX idx_a ON app.t(a)", PostgresStatementKind.CreateIndex),
            ("CREATE UNIQUE INDEX idx_u ON app.t(b)", PostgresStatementKind.CreateUniqueIndex),
            ("CREATE VIEW app.v AS SELECT 1", PostgresStatementKind.CreateView),
            ("CREATE TRIGGER trg AFTER INSERT ON app.t FOR EACH ROW EXECUTE FUNCTION app.f()", PostgresStatementKind.CreateTrigger),
            ("CREATE POLICY p ON app.t FOR ALL TO PUBLIC USING (true)", PostgresStatementKind.CreatePolicy),
            ("ALTER TABLE app.t ENABLE ROW LEVEL SECURITY", PostgresStatementKind.AlterTableEnableRls),
        };

        foreach ( var (sql, expected) in cases )
        {
            PostgresStatementClassifier.Classify( sql ).Kind
                .Should().Be( expected, $"classifier should handle: {sql}" );
        }
    }
}
