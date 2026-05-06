using System.Text;
using DotNet.Testcontainers.Containers;
using FluentAssertions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Testcontainers.PostgreSql;

namespace Hyperbee.Migrations.Spike.PostgresClassifier;

[TestClass]
public class ClassifierSpikeTests
{
    private static PostgreSqlContainer? _container;
    private static string _capturedDump = "";

    [ClassInitialize]
    public static async Task ClassInit( TestContext _ )
    {
        _container = new PostgreSqlBuilder( "postgres:16-alpine" )
            .WithDatabase( "spikedb" )
            .WithUsername( "spike" )
            .WithPassword( "spike" )
            .WithCleanUp( true )
            .Build();

        await _container.StartAsync();

        // Copy fixture into the container, then apply via psql.
        // Going through psql (not Npgsql) sidesteps client-side multi-statement parsing
        // and gives us a faithful round-trip against real Postgres tooling.
        var fixturePath = Path.Combine( AppContext.BaseDirectory, "Fixtures", "kitchen-sink.sql" );
        var sqlBytes = await File.ReadAllBytesAsync( fixturePath );
        await _container.CopyAsync( sqlBytes, "/tmp/kitchen-sink.sql" );

        var apply = await _container.ExecAsync( new[]
        {
            "psql", "-v", "ON_ERROR_STOP=1",
            "-U", "spike", "-d", "spikedb",
            "-f", "/tmp/kitchen-sink.sql"
        } );
        if ( apply.ExitCode != 0 )
            throw new InvalidOperationException(
                $"psql apply failed (exit={apply.ExitCode}). stderr:\n{apply.Stderr}\n\nstdout:\n{apply.Stdout}" );

        // Capture pg_dump --schema-only via docker exec inside the container
        _capturedDump = await DumpSchemaAsync( _container );

        // Persist dump for inspection
        var dumpOutPath = Path.Combine( AppContext.BaseDirectory, "captured-dump.sql" );
        await File.WriteAllTextAsync( dumpOutPath, _capturedDump );
    }

    [ClassCleanup]
    public static async Task ClassCleanupAsync()
    {
        if ( _container is not null )
            await _container.DisposeAsync();
    }

    private static async Task<string> DumpSchemaAsync( PostgreSqlContainer container )
    {
        // Testcontainers ExecAsync returns stdout/stderr.
        var result = await container.ExecAsync( new[]
        {
            "pg_dump",
            "--schema-only",
            "--no-owner",
            "--no-privileges",
            "-U", "spike",
            "-d", "spikedb"
        } );

        if ( result.ExitCode != 0 )
            throw new InvalidOperationException( $"pg_dump failed (exit={result.ExitCode}): {result.Stderr}" );

        return result.Stdout;
    }

    [TestMethod]
    public void Splitter_OnFixture_ProducesExpectedShape()
    {
        // Sanity check on the splitter against the raw fixture (not the dump).
        var fixturePath = Path.Combine( AppContext.BaseDirectory, "Fixtures", "kitchen-sink.sql" );
        var script = File.ReadAllText( fixturePath );

        var statements = PostgresStatementSplitter.Split( script );

        statements.Should().NotBeEmpty();

        // Hand-counted from kitchen-sink.sql: 1 ext + 1 schema + 3 types/domains + 1 seq +
        // 7 tables (2 root partitioned + 4 partitions + 1 audit_log) + 4 indexes +
        // 4 functions + 1 trigger + 1 RLS enable + 1 policy + 1 view + 1 mview + 1 mview-idx +
        // 2 comments = ~30
        statements.Count.Should().BeGreaterThan( 25 );

        // Dollar-quoted bodies must NOT have been split internally.
        var dynQuery = statements.SingleOrDefault( s => s.Contains( "dynamic_query", StringComparison.Ordinal ) );
        dynQuery.Should().NotBeNull();
        dynQuery!.Should().Contain( "EXECUTE format" );

        var isPremium = statements.SingleOrDefault( s => s.Contains( "is_premium", StringComparison.Ordinal ) );
        isPremium.Should().NotBeNull();
        // Body contains a semicolon-bearing comment + multi-line block comment
        isPremium!.Should().Contain( "RETURN cnt > 10" );

        // RLS policy USING+WITH CHECK must have been kept as a single statement.
        var policy = statements.SingleOrDefault( s => s.StartsWith( "CREATE POLICY", StringComparison.OrdinalIgnoreCase ) );
        policy.Should().NotBeNull();
        policy!.Should().Contain( "USING" );
        policy.Should().Contain( "WITH CHECK" );
    }

    [TestMethod]
    public void Splitter_OnRealDump_ProducesNonEmpty()
    {
        _capturedDump.Should().NotBeNullOrWhiteSpace( "ClassInitialize captures pg_dump output" );

        var statements = PostgresStatementSplitter.Split( _capturedDump );
        statements.Should().NotBeEmpty();

        Console.WriteLine( $"[Splitter] dump bytes={_capturedDump.Length}, statements={statements.Count}" );
    }

    [TestMethod]
    public void Classifier_OnRealDump_ReportTally()
    {
        var statements = PostgresStatementSplitter.Split( _capturedDump );
        var classified = statements.Select( PostgresStatementClassifier.Classify ).ToList();

        var byKind = classified
            .GroupBy( c => c.Kind )
            .OrderByDescending( g => g.Count() )
            .ToList();

        // Emit a tally that becomes part of SPIKE_REPORT.md
        Console.WriteLine( "=== Classifier tally on real pg_dump output ===" );
        Console.WriteLine( $"Total statements: {classified.Count}" );
        foreach ( var g in byKind )
            Console.WriteLine( $"  {g.Key,-32} {g.Count(),4}" );

        var unknown = classified.Where( c => c.Kind == PostgresStatementKind.Unknown ).ToList();
        Console.WriteLine();
        Console.WriteLine( $"=== Unknown statements ({unknown.Count}) ===" );
        foreach ( var u in unknown.Take( 30 ) )
        {
            var preview = u.Body.Length > 200 ? u.Body[..200] + "..." : u.Body;
            Console.WriteLine( "---" );
            Console.WriteLine( preview );
        }

        // Persist for the report.
        var reportLines = new List<string>();
        reportLines.Add( $"Total statements: {classified.Count}" );
        foreach ( var g in byKind )
            reportLines.Add( $"{g.Key},{g.Count()}" );
        reportLines.Add( "" );
        reportLines.Add( "=== Unknown statements ===" );
        foreach ( var u in unknown )
        {
            reportLines.Add( "---" );
            reportLines.Add( u.Body );
        }

        File.WriteAllLines(
            Path.Combine( AppContext.BaseDirectory, "classifier-tally.txt" ),
            reportLines );

        // Spike threshold: at least 80% of statements must be classified to a known kind.
        var known = classified.Count - unknown.Count;
        var pct = (double) known / classified.Count;
        Console.WriteLine();
        Console.WriteLine( $"Known coverage: {pct:P1}" );

        pct.Should().BeGreaterThanOrEqualTo( 0.80, "spike target is >= 80% known classification" );
    }

    [TestMethod]
    public void Classifier_OnRealDump_NoFunctionBodyLeakage()
    {
        // Specific guarantee: function bodies (which contain semicolons, $$, etc.)
        // must end up as exactly one CreateFunction record each, not split apart.
        var statements = PostgresStatementSplitter.Split( _capturedDump );
        var classified = statements.Select( PostgresStatementClassifier.Classify ).ToList();

        var functions = classified
            .Where( c => c.Kind == PostgresStatementKind.CreateFunction )
            .ToList();

        Console.WriteLine( $"Functions classified: {functions.Count}" );
        foreach ( var f in functions )
            Console.WriteLine( $"  {f.SchemaName}.{f.ObjectName}" );

        // We created 4 functions in the fixture (normalize_email, is_premium, dynamic_query, audit_trg).
        functions.Should().HaveCountGreaterThanOrEqualTo( 4 );

        // dynamic_query has a body that uses nested $$ inside an outer dollar-quote.
        // pg_dump may rewrite the outer tag (e.g. to $_$), so we don't assert on the
        // specific tag — but we do assert the body kept the inner $$ format(...) intact
        // and was classified as exactly one function.
        var dyn = functions.SingleOrDefault( f => f.ObjectName == "dynamic_query" );
        dyn.Should().NotBeNull();
        dyn!.Body.Should().Contain( "format($$SELECT" );
        dyn.Body.Should().Contain( "EXECUTE" );
    }
}
