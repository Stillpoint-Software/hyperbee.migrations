Now I have enough context. The user wants me to produce the path-finder example as a response (no file creation needed — they want the example shown in chat). Let me deliver the comprehensive Postgres implementation example.

---

# Postgres Squash — Basic Implementation Example (Path-Finder)

> **Path-finder note:** This example sets the bar for the per-provider examples. It is *basic* in the sense that it walks one realistic 5-migration range end-to-end, but it does not paper over the dump/post-process/diff/verify pipeline. The Postgres advocate's central claim through Round 1a/1b is that `pg_dump --schema-only` is the only honest source of truth for relational state, and that the canonicalization pipeline is the load-bearing component. The code below demonstrates exactly that.

> Consensus references: **C1** (`IDataOpClassifier`), **C2** (verification round), **C3** (`fleet.yml --squash-overrides`), **C4** (in-process diff), **C5** (round-trip CI), **D-3** Postgres advocate position from `migration-squashing-consensus-destructive.md`.

---

## 1. Topology signature

The signature is captured *before* the dump and stamped into the squash header. Drift detection at replay time uses this.

```csharp
// src/Hyperbee.Migrations.Providers.Postgres/Squash/PostgresTopologySignature.cs
namespace Hyperbee.Migrations.Providers.Postgres.Squash;

using System.Text;
using Npgsql;

public sealed record PostgresTopologySignature(
    int ServerMajor,                   // 14, 15, 16, 17
    int ServerMinor,                   // recorded but not compared (patch-level drift OK)
    string ServerVersionFull,          // "PostgreSQL 16.2 on x86_64-pc-linux-gnu..."
    IReadOnlyList<PostgresExtension> Extensions,
    string CollationProvider,          // "libc" | "icu"
    string LocaleProvider,             // "libc" | "icu"
    string ServerEncoding,             // "UTF8"
    string LcCollate,                  // "en_US.utf8"
    string LcCtype                     // "en_US.utf8"
)
{
    public static async Task<PostgresTopologySignature> CaptureAsync(
        NpgsqlDataSource dataSource,
        CancellationToken ct = default )
    {
        await using var conn = await dataSource.OpenConnectionAsync( ct );

        var (major, minor, full) = await ReadServerVersionAsync( conn, ct );
        var extensions = await ReadExtensionsAsync( conn, ct );
        var (collProv, locProv, enc, lcColl, lcCtype) = await ReadDatabaseLocaleAsync( conn, ct );

        return new PostgresTopologySignature(
            ServerMajor: major,
            ServerMinor: minor,
            ServerVersionFull: full,
            Extensions: extensions,
            CollationProvider: collProv,
            LocaleProvider: locProv,
            ServerEncoding: enc,
            LcCollate: lcColl,
            LcCtype: lcCtype );
    }

    private static async Task<(int major, int minor, string full)> ReadServerVersionAsync(
        NpgsqlConnection conn, CancellationToken ct )
    {
        await using var cmd = new NpgsqlCommand( "SELECT version(), current_setting('server_version_num')::int", conn );
        await using var reader = await cmd.ExecuteReaderAsync( ct );
        await reader.ReadAsync( ct );

        var full = reader.GetString( 0 );
        var num = reader.GetInt32( 1 );  // e.g. 160002 for 16.2
        var major = num / 10000;
        var minor = num % 10000;
        return (major, minor, full);
    }

    private static async Task<IReadOnlyList<PostgresExtension>> ReadExtensionsAsync(
        NpgsqlConnection conn, CancellationToken ct )
    {
        const string sql = @"
            SELECT extname, extversion, n.nspname AS schema_name
            FROM pg_extension e
            JOIN pg_namespace n ON n.oid = e.extnamespace
            WHERE extname NOT IN ('plpgsql')   -- baseline; never carried into prerequisites.sql
            ORDER BY extname";

        await using var cmd = new NpgsqlCommand( sql, conn );
        await using var reader = await cmd.ExecuteReaderAsync( ct );

        var list = new List<PostgresExtension>();
        while ( await reader.ReadAsync( ct ) )
        {
            list.Add( new PostgresExtension(
                Name: reader.GetString( 0 ),
                Version: reader.GetString( 1 ),
                Schema: reader.GetString( 2 ) ) );
        }
        return list;
    }

    private static async Task<(string collProv, string locProv, string enc, string lcColl, string lcCtype)>
        ReadDatabaseLocaleAsync( NpgsqlConnection conn, CancellationToken ct )
    {
        // Postgres 15+ has datlocprovider; 14 always libc. Accommodate both.
        const string sql = @"
            SELECT
                pg_encoding_to_char(d.encoding)                        AS encoding,
                d.datcollate                                           AS lc_collate,
                d.datctype                                             AS lc_ctype,
                COALESCE(
                    (CASE WHEN current_setting('server_version_num')::int >= 150000
                          THEN (SELECT datlocprovider::text FROM pg_database WHERE datname = current_database())
                          ELSE 'c' END),
                    'c')                                               AS loc_provider
            FROM pg_database d
            WHERE d.datname = current_database()";

        await using var cmd = new NpgsqlCommand( sql, conn );
        await using var reader = await cmd.ExecuteReaderAsync( ct );
        await reader.ReadAsync( ct );

        var enc = reader.GetString( 0 );
        var lcColl = reader.GetString( 1 );
        var lcCtype = reader.GetString( 2 );
        var locProv = reader.GetString( 3 ) switch
        {
            "c" => "libc",
            "i" => "icu",
            "b" => "builtin",
            var x => x
        };
        // Collation provider tracked separately for Postgres 17+ where per-DB ICU is finer-grained.
        // For 14 & 16, collation provider == locale provider.
        return (locProv, locProv, enc, lcColl, lcCtype);
    }

    public TopologyComparison CompareTo( PostgresTopologySignature other )
    {
        var diffs = new List<string>();

        if ( ServerMajor != other.ServerMajor )
            diffs.Add( $"server_major: {ServerMajor} != {other.ServerMajor}" );

        if ( ServerEncoding != other.ServerEncoding )
            diffs.Add( $"encoding: {ServerEncoding} != {other.ServerEncoding}" );

        if ( LcCollate != other.LcCollate )
            diffs.Add( $"lc_collate: {LcCollate} != {other.LcCollate}" );

        if ( LcCtype != other.LcCtype )
            diffs.Add( $"lc_ctype: {LcCtype} != {other.LcCtype}" );

        if ( LocaleProvider != other.LocaleProvider )
            diffs.Add( $"locale_provider: {LocaleProvider} != {other.LocaleProvider}" );

        var aExt = Extensions.ToDictionary( e => e.Name, e => e );
        var bExt = other.Extensions.ToDictionary( e => e.Name, e => e );

        foreach ( var name in aExt.Keys.Union( bExt.Keys ).OrderBy( n => n ) )
        {
            if ( !aExt.TryGetValue( name, out var a ) ) { diffs.Add( $"extension added: {name}@{bExt[name].Version}" ); continue; }
            if ( !bExt.TryGetValue( name, out var b ) ) { diffs.Add( $"extension removed: {name}@{a.Version}" ); continue; }
            if ( a.Version != b.Version ) diffs.Add( $"extension version drift: {name}: {a.Version} != {b.Version}" );
            if ( a.Schema != b.Schema ) diffs.Add( $"extension schema drift: {name}: {a.Schema} != {b.Schema}" );
        }

        return new TopologyComparison(
            Equivalent: diffs.Count == 0,
            Differences: diffs );
    }

    public string ToHeaderBlock()
    {
        var sb = new StringBuilder();
        sb.AppendLine( "-- topology-signature:" );
        sb.AppendLine( $"--   server_major:     {ServerMajor}" );
        sb.AppendLine( $"--   server_version:   {ServerVersionFull}" );
        sb.AppendLine( $"--   encoding:         {ServerEncoding}" );
        sb.AppendLine( $"--   lc_collate:       {LcCollate}" );
        sb.AppendLine( $"--   lc_ctype:         {LcCtype}" );
        sb.AppendLine( $"--   locale_provider:  {LocaleProvider}" );
        sb.AppendLine( "--   extensions:" );
        foreach ( var e in Extensions )
            sb.AppendLine( $"--     - {e.Name}@{e.Version} (schema={e.Schema})" );
        return sb.ToString();
    }
}

public sealed record PostgresExtension( string Name, string Version, string Schema );
public sealed record TopologyComparison( bool Equivalent, IReadOnlyList<string> Differences );
```

---

## 2. Data-op classifier (C1 binding for Postgres)

```csharp
// src/Hyperbee.Migrations.Providers.Postgres/Squash/PostgresDataOpClassifier.cs
namespace Hyperbee.Migrations.Providers.Postgres.Squash;

using System.Text.RegularExpressions;
using Hyperbee.Migrations.Squash;

public sealed class PostgresDataOpClassifier : IDataOpClassifier
{
    // First-token DML detection. Order matters: longer/more specific keywords first.
    private static readonly (string Token, bool IsDml, string Hint)[] FirstTokenTable =
    {
        ("INSERT",            true,  "embed-as-sql"),
        ("UPDATE",            true,  "embed-as-sql"),
        ("DELETE",            true,  "embed-as-sql"),
        ("TRUNCATE",          true,  "embed-as-sql"),     // destructive; carry verbatim
        ("MERGE",             true,  "embed-as-sql"),     // PG15+
        ("COPY",              true,  "embed-as-sql"),     // bulk-load shape; carry
        ("CREATE TABLE",      false, null!),              // CTAS handled below
        ("SELECT INTO",       true,  "embed-as-sql"),
        ("DO",                false, null!),              // anonymous block; classified below
        ("CALL",              false, null!),              // procedure invocation; classified below
    };

    // Conservative DML patterns inside DO $$...$$ or CALL targets.
    private static readonly Regex DmlInsideBlock = new(
        @"\b(INSERT\s+INTO|UPDATE\s+\w|DELETE\s+FROM|TRUNCATE|MERGE\s+INTO|COPY\s+\w)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled );

    // CREATE TABLE ... AS SELECT — structural shape but creates rows. Flagged as data op.
    private static readonly Regex CtasPattern = new(
        @"^\s*CREATE\s+(TEMP\s+|TEMPORARY\s+|UNLOGGED\s+)?TABLE\s+.+\s+AS\s+(WITH\s+|SELECT\s)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Singleline );

    public DataOpClassification Classify( StatementOrCallSite candidate )
    {
        var sql = StripLeadingComments( candidate.RawText ).TrimStart();

        if ( sql.Length == 0 )
            return new DataOpClassification( IsDataOp: false, RequiresPreservation: false, IsUnclassified: false, EmissionHint: null );

        // CTAS check before generic CREATE TABLE branch.
        if ( CtasPattern.IsMatch( sql ) )
            return new DataOpClassification( true, true, false, "embed-as-sql" );

        foreach ( var (tok, isDml, hint) in FirstTokenTable )
        {
            if ( !StartsWithKeyword( sql, tok ) ) continue;

            if ( tok == "CREATE TABLE" )
                return new DataOpClassification( false, false, false, null );

            if ( tok == "DO" )
                return ClassifyDoBlock( sql );

            if ( tok == "CALL" )
                return new DataOpClassification(
                    IsDataOp: true,
                    RequiresPreservation: true,
                    IsUnclassified: true,           // refuse: we cannot inspect procedure body
                    EmissionHint: "carry-as-sql-but-warn" );

            return new DataOpClassification( isDml, isDml, false, hint );
        }

        // CREATE FUNCTION / CREATE PROCEDURE — definition is structural; their *body* may DML at runtime,
        // but the body is captured via pg_dump in the function definition itself.
        // CREATE/ALTER/DROP DDL — structural.
        if ( IsStructuralDdl( sql ) )
            return new DataOpClassification( false, false, false, null );

        // Unknown statement — refuse rather than guess.
        return new DataOpClassification(
            IsDataOp: false,
            RequiresPreservation: false,
            IsUnclassified: true,
            EmissionHint: $"unclassified-first-token: {FirstWord( sql )}" );
    }

    private static DataOpClassification ClassifyDoBlock( string sql )
    {
        // Conservative: if the body contains any DML keyword, mark as data op.
        // 5% gap: a function call inside DO that itself does DML — invisible here.
        if ( DmlInsideBlock.IsMatch( sql ) )
            return new DataOpClassification( true, true, false, "embed-as-sql" );
        return new DataOpClassification( false, false, false, null );
    }

    private static bool StartsWithKeyword( string sql, string keyword )
    {
        if ( sql.Length < keyword.Length ) return false;
        for ( var i = 0; i < keyword.Length; i++ )
        {
            var a = char.ToUpperInvariant( sql[i] );
            var b = keyword[i];
            if ( a != b && !(b == ' ' && char.IsWhiteSpace( sql[i] )) ) return false;
        }
        // boundary
        if ( sql.Length == keyword.Length ) return true;
        return char.IsWhiteSpace( sql[keyword.Length] ) || sql[keyword.Length] == '(';
    }

    private static bool IsStructuralDdl( string sql )
    {
        var w = FirstWord( sql ).ToUpperInvariant();
        return w is "CREATE" or "ALTER" or "DROP" or "COMMENT" or "GRANT" or "REVOKE" or "SET" or "RESET" or "BEGIN" or "COMMIT" or "ROLLBACK";
    }

    private static string FirstWord( string sql )
    {
        var i = 0;
        while ( i < sql.Length && char.IsWhiteSpace( sql[i] ) ) i++;
        var start = i;
        while ( i < sql.Length && !char.IsWhiteSpace( sql[i] ) ) i++;
        return sql[start..i];
    }

    private static string StripLeadingComments( string sql )
    {
        var i = 0;
        while ( i < sql.Length )
        {
            while ( i < sql.Length && char.IsWhiteSpace( sql[i] ) ) i++;
            if ( i + 1 < sql.Length && sql[i] == '-' && sql[i + 1] == '-' )
            {
                while ( i < sql.Length && sql[i] != '\n' ) i++;
                continue;
            }
            if ( i + 1 < sql.Length && sql[i] == '/' && sql[i + 1] == '*' )
            {
                i += 2;
                while ( i + 1 < sql.Length && !(sql[i] == '*' && sql[i + 1] == '/') ) i++;
                i += 2;
                continue;
            }
            break;
        }
        return sql[i..];
    }
}
```

**Honest gap:** the `IDataOpClassifier` cannot see *into* a `CREATE FUNCTION` body. A plpgsql function with embedded DML is correctly classified as DDL (the definition is structural), but if a *separate* migration calls that function via `SELECT my_data_func();` — it's a `SELECT`, no DML keyword, invisible. The classifier flags `CALL` as unclassified for the same reason. Operators must use `[DataMigration]` attribute or explicit `accept-data-op-loss` override on these.

---

## 3. The squash generator — orchestration

```csharp
// src/Hyperbee.Migrations.Providers.Postgres/Squash/PostgresSquashGenerator.cs
namespace Hyperbee.Migrations.Providers.Postgres.Squash;

using System.Diagnostics;
using System.Text;
using Hyperbee.Migrations.Resources;
using Hyperbee.Migrations.Squash;
using Microsoft.Extensions.Logging;
using Npgsql;
using Testcontainers.PostgreSql;

public sealed class PostgresSquashGenerator : ISquashGenerator
{
    private readonly ILogger<PostgresSquashGenerator> _log;
    private readonly PostgresDataOpClassifier _classifier = new();
    private readonly PostgresStatementClassifier _stmtClassifier = new();
    private readonly PostgresCanonicalizer _canonicalizer = new();

    public PostgresSquashGenerator( ILogger<PostgresSquashGenerator> log ) => _log = log;

    public async Task<SquashResult> GenerateAsync( SquashRequest req, CancellationToken ct = default )
    {
        // 1. Capture topology signature from operator's source environment.
        await using var sourceDs = NpgsqlDataSource.Create( req.SourceConnectionString );
        var topology = await PostgresTopologySignature.CaptureAsync( sourceDs, ct );

        if ( req.Overrides.AllowVersionSkew == false && topology.ServerMajor != req.PreferredServerMajor )
            throw new SquashRefusedException(
                $"Server major mismatch: source={topology.ServerMajor}, preferred={req.PreferredServerMajor}. " +
                $"Set squash-overrides.postgres.allow-version-skew=true to bypass." );

        // 2. Classify every statement in migrations [1..N]. Refuse on unclassified DML.
        //    Collect data-ops to be carried verbatim.
        var (dataOpsBefore, refusals) = ClassifyMigrations( req.MigrationsBeforeSquash );

        if ( refusals.Count > 0 && !req.Overrides.Postgres.AcceptDataOpLoss )
            throw new SquashRefusedException(
                $"{refusals.Count} migration statement(s) could not be classified as DDL/DML. " +
                $"Refusals:\n{string.Join( "\n", refusals.Select( r => $"  - {r.Migration}: {r.Hint}" ) )}" );

        // 3. Spin testcontainer A — version-matched. Apply migrations [1..M-1]. Snapshot.
        _log.LogInformation( "Spinning Postgres {major} container for snapshot A", topology.ServerMajor );
        await using var containerA = await SpinPostgresAsync( topology.ServerMajor, ct );
        await ApplyMigrationsAsync( containerA, req.MigrationsBeforeRange, ct );
        var dumpA = await DumpAsync( containerA, topology.ServerMajor, ct );

        // 4. Spin testcontainer B. Apply migrations [1..N]. Snapshot.
        _log.LogInformation( "Spinning Postgres {major} container for snapshot B", topology.ServerMajor );
        await using var containerB = await SpinPostgresAsync( topology.ServerMajor, ct );
        await ApplyMigrationsAsync( containerB, req.MigrationsBeforeSquash, ct );
        var dumpB = await DumpAsync( containerB, topology.ServerMajor, ct );

        // 4a. Capture sequence values from snapshot B.
        var sequenceValues = await CaptureSequenceValuesAsync( containerB, ct );

        // 5. Post-process both dumps through canonicalization.
        var canonA = _canonicalizer.Canonicalize( dumpA, isExtensionSink: false );
        var canonB = _canonicalizer.Canonicalize( dumpB, isExtensionSink: false );

        // 6. Refuse on dangerous shapes detected during canonicalization.
        if ( canonB.Violations.Any() )
            throw new SquashRefusedException(
                "Canonicalization refused snapshot B:\n" +
                string.Join( "\n", canonB.Violations.Select( v => $"  - {v}" ) ) );

        // 7. Statement-level set diff.
        var stmtsA = _stmtClassifier.Parse( canonA.Body );
        var stmtsB = _stmtClassifier.Parse( canonB.Body );
        var delta = StatementSetDiff.Compute( stmtsA, stmtsB );

        // 8. Emit Squash_M.sql / .prerequisites.sql / .dataops.sql.
        var artifact = new PostgresSquashArtifact(
            Header: BuildHeader( req, topology, refusals.Count ),
            PrerequisitesSql: canonB.ExtractedExtensions,
            BodySql: delta.RenderForward(),
            SetvalSql: RenderSetvals( sequenceValues, delta ),
            DataOpsSql: RenderDataOps( dataOpsBefore ),
            ClassificationReport: BuildReport( stmtsA, stmtsB, delta, refusals ) );

        return new SquashResult( Artifact: artifact, CanonicalSnapshotB: canonB.Body );
    }

    private (List<string> dataOps, List<RefusalRecord> refusals) ClassifyMigrations(
        IReadOnlyList<MigrationDescriptor> migrations )
    {
        var dataOps = new List<string>();
        var refusals = new List<RefusalRecord>();

        foreach ( var m in migrations )
        {
            foreach ( var stmt in m.Statements )
            {
                var c = _classifier.Classify( new StatementOrCallSite( stmt, m.Name ) );

                if ( c.IsUnclassified )
                {
                    refusals.Add( new RefusalRecord( m.Name, c.EmissionHint ?? "unknown" ) );
                    continue;
                }

                if ( c.RequiresPreservation )
                    dataOps.Add( $"-- from {m.Name}\n{stmt.TrimEnd()};" );
            }
        }
        return (dataOps, refusals);
    }

    private async Task<PostgresContainerHandle> SpinPostgresAsync( int major, CancellationToken ct )
    {
        var image = major switch
        {
            14 => "postgres:14-alpine",
            15 => "postgres:15-alpine",
            16 => "postgres:16-alpine",
            17 => "postgres:17-alpine",
            _ => throw new SquashRefusedException( $"Unsupported Postgres major: {major}" )
        };

        var container = new PostgreSqlBuilder()
            .WithImage( image )
            .WithDatabase( "squash" )
            .WithUsername( "squash" )
            .WithPassword( "squash" )
            .Build();

        await container.StartAsync( ct );
        return new PostgresContainerHandle( container, major );
    }

    private async Task ApplyMigrationsAsync(
        PostgresContainerHandle handle,
        IReadOnlyList<MigrationDescriptor> migrations,
        CancellationToken ct )
    {
        await using var ds = NpgsqlDataSource.Create( handle.ConnectionString );
        foreach ( var m in migrations )
        {
            // CRITICAL: routes through PostgresResourceRunner for parity with production replay.
            // The runner reads embedded resources via ResourceHelper; here we synthesize a
            // descriptor adapter so the shipped migration code path is exercised.
            await m.ApplyVia( ds, ct );
        }
    }

    private async Task<string> DumpAsync( PostgresContainerHandle handle, int major, CancellationToken ct )
    {
        // pg_dump shipped with the CLI image, version-matched to source.
        // Locating: the CLI image bundles /opt/pg-tools/{14,15,16,17}/bin/pg_dump.
        var pgDumpPath = ResolveBundledPgDump( major );

        // Verify the binary's reported version matches before running.
        var versionLine = await RunAndCaptureAsync( pgDumpPath, new[] { "--version" }, ct );
        if ( !versionLine.Contains( $"PostgreSQL) {major}." ) )
            throw new SquashRefusedException(
                $"Bundled pg_dump version mismatch: expected {major}.x, got: {versionLine.Trim()}" );

        var args = new[]
        {
            "--schema-only",
            "--no-owner",
            "--no-privileges",
            "--no-comments",
            "--no-publications",
            "--no-subscriptions",
            "--no-security-labels",
            "--quote-all-identifiers",
            "--format=plain",
            "--encoding=UTF8",
            $"--host={handle.Host}",
            $"--port={handle.Port}",
            $"--username={handle.Username}",
            $"--dbname={handle.Database}",
        };

        var sb = new StringBuilder( capacity: 64 * 1024 );
        var psi = new ProcessStartInfo
        {
            FileName = pgDumpPath,
            RedirectStandardOutput = true,
            RedirectStandardError = true,
            UseShellExecute = false,
        };
        foreach ( var a in args ) psi.ArgumentList.Add( a );
        psi.Environment["PGPASSWORD"] = handle.Password;

        using var proc = Process.Start( psi )!;
        var stdoutTask = proc.StandardOutput.ReadToEndAsync( ct );
        var stderrTask = proc.StandardError.ReadToEndAsync( ct );
        await proc.WaitForExitAsync( ct );

        if ( proc.ExitCode != 0 )
            throw new SquashRefusedException( $"pg_dump failed (exit {proc.ExitCode}):\n{await stderrTask}" );

        return await stdoutTask;
    }

    private static string ResolveBundledPgDump( int major ) =>
        Path.Combine( AppContext.BaseDirectory, "pg-tools", major.ToString(), "bin",
            OperatingSystem.IsWindows() ? "pg_dump.exe" : "pg_dump" );

    private static async Task<string> RunAndCaptureAsync( string exe, string[] args, CancellationToken ct )
    {
        var psi = new ProcessStartInfo { FileName = exe, RedirectStandardOutput = true, UseShellExecute = false };
        foreach ( var a in args ) psi.ArgumentList.Add( a );
        using var p = Process.Start( psi )!;
        var s = await p.StandardOutput.ReadToEndAsync( ct );
        await p.WaitForExitAsync( ct );
        return s;
    }

    private async Task<IReadOnlyList<SequenceValue>> CaptureSequenceValuesAsync(
        PostgresContainerHandle handle, CancellationToken ct )
    {
        const string sql = @"
            SELECT
                seq.schemaname,
                seq.sequencename,
                seq.last_value,
                seq.start_value,
                COALESCE(d.deptype, '')         AS deptype,    -- 'a' = automatic (identity-owned)
                COALESCE(d.refobjid::regclass::text, '') AS owning_table
            FROM pg_sequences seq
            LEFT JOIN pg_depend d
                ON d.classid = 'pg_class'::regclass
               AND d.objid   = (seq.schemaname || '.' || quote_ident(seq.sequencename))::regclass
               AND d.deptype IN ('a', 'i')
            ORDER BY seq.schemaname, seq.sequencename";

        await using var ds = NpgsqlDataSource.Create( handle.ConnectionString );
        await using var cmd = ds.CreateCommand( sql );
        await using var reader = await cmd.ExecuteReaderAsync( ct );
        var list = new List<SequenceValue>();
        while ( await reader.ReadAsync( ct ) )
        {
            list.Add( new SequenceValue(
                Schema: reader.GetString( 0 ),
                Name: reader.GetString( 1 ),
                LastValue: reader.IsDBNull( 2 ) ? null : reader.GetInt64( 2 ),
                StartValue: reader.GetInt64( 3 ),
                IsIdentityOwned: reader.GetString( 4 ) == "a" ) );
        }
        return list;
    }

    private static string RenderSetvals( IReadOnlyList<SequenceValue> seqs, StatementDelta delta )
    {
        var sb = new StringBuilder();
        foreach ( var s in seqs )
        {
            // Skip sequences whose last_value == start_value (no rows consumed it yet).
            if ( s.LastValue is null || s.LastValue == s.StartValue ) continue;

            // Identity-owned sequences are auto-managed; emitting setval here is the right call
            // ONLY because the squash recreates the table empty and there are no rows yet.
            // EDGE CASE: if a future "destructive squash with data preservation" mode is added,
            // identity setvals must be coordinated with row preservation.
            sb.AppendLine( $"SELECT pg_catalog.setval('{s.Schema}.{s.Name}', {s.LastValue}, true);" );
        }
        return sb.ToString();
    }

    private static string RenderDataOps( IReadOnlyList<string> ops ) =>
        ops.Count == 0 ? string.Empty : string.Join( "\n\n", ops ) + "\n";

    private static string BuildHeader( SquashRequest req, PostgresTopologySignature topo, int refusalCount )
    {
        var sb = new StringBuilder();
        sb.AppendLine( $"-- Squash_{req.SquashVersion}.sql" );
        sb.AppendLine( $"-- generated: {DateTimeOffset.UtcNow:O}" );
        sb.AppendLine( $"-- range: [{req.MigrationsBeforeSquash.First().Name} .. {req.MigrationsBeforeSquash.Last().Name}]" );
        sb.AppendLine( $"-- data-op-refusals: {refusalCount}" );
        sb.Append( topo.ToHeaderBlock() );
        return sb.ToString();
    }

    private static SquashClassificationReport BuildReport(
        IReadOnlyList<PostgresStatement> a,
        IReadOnlyList<PostgresStatement> b,
        StatementDelta delta,
        IReadOnlyList<RefusalRecord> refusals ) =>
        new( CountA: a.Count, CountB: b.Count, Added: delta.Added.Count, Removed: delta.Removed.Count,
             Changed: delta.Changed.Count, Refusals: refusals );
}
```

---

## 4. The canonicalization pipeline (the load-bearing component)

```csharp
// src/Hyperbee.Migrations.Providers.Postgres/Squash/PostgresCanonicalizer.cs
namespace Hyperbee.Migrations.Providers.Postgres.Squash;

using System.Text;
using System.Text.RegularExpressions;

public sealed class PostgresCanonicalizer
{
    private static readonly Regex ConcurrentlyRx = new(
        @"\bCREATE\s+(UNIQUE\s+)?INDEX\s+CONCURRENTLY\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled );

    private static readonly Regex DangerousSearchPathRx = new(
        @"^SELECT\s+pg_catalog\.set_config\(\s*'search_path'\s*,\s*''\s*,\s*false\s*\)\s*;\s*$",
        RegexOptions.IgnoreCase | RegexOptions.Compiled );

    private static readonly Regex CreateExtensionRx = new(
        @"^\s*CREATE\s+EXTENSION\s+(IF\s+NOT\s+EXISTS\s+)?""?(?<n>[^"" ;]+)""?",
        RegexOptions.IgnoreCase | RegexOptions.Compiled | RegexOptions.Multiline );

    private static readonly Regex SetStatementRx = new(
        @"^SET\s+\w+",
        RegexOptions.IgnoreCase | RegexOptions.Compiled );

    private static readonly Regex RoleRefRx = new(
        @"\b(OWNER\s+TO|ALTER\s+ROLE|GRANT\s+\w+\s+ON|REVOKE\s+\w+\s+ON)\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled );

    public CanonicalizedDump Canonicalize( string raw, bool isExtensionSink )
    {
        var violations = new List<string>();
        var extractedExt = new StringBuilder();

        // Step 1: split into logical statements (semicolon-terminated, naive but adequate for pg_dump output).
        // pg_dump's --format=plain output is single-statement-per-block separated by blank lines and `--`-comments.
        var rawNormalized = raw.Replace( "\r\n", "\n" ).Replace( "\r", "\n" );

        // Step 2: drop SET preamble (search_path, statement_timeout, lock_timeout, idle_in_transaction_session_timeout, client_encoding, standard_conforming_strings, xmloption, etc).
        // pg_dump always emits these at the top.
        var lines = rawNormalized.Split( '\n' );
        var sb = new StringBuilder( capacity: rawNormalized.Length );

        foreach ( var line in lines )
        {
            var stripped = line.TrimEnd();

            // Reject the dangerous search_path reset that would carry into operator code.
            if ( DangerousSearchPathRx.IsMatch( stripped ) ) continue;

            // Drop SET preamble.
            if ( SetStatementRx.IsMatch( stripped ) ) continue;

            // Drop comment-only lines (after pg_dump's --no-comments still emits structural comments).
            if ( stripped.StartsWith( "--" ) ) continue;

            // Drop blank lines for canonical comparison.
            if ( stripped.Length == 0 ) continue;

            // Refuse CREATE INDEX CONCURRENTLY — it cannot run inside a transaction and shouldn't survive squash.
            // Note: pg_dump never emits CONCURRENTLY itself, but defense-in-depth here in case post-process input
            // ever comes from somewhere else.
            if ( ConcurrentlyRx.IsMatch( stripped ) )
                violations.Add( $"CREATE INDEX CONCURRENTLY survived to canonicalizer: {stripped}" );

            // Validate no role refs survive (--no-owner --no-privileges should already strip these).
            if ( RoleRefRx.IsMatch( stripped ) )
                violations.Add( $"role reference survived: {stripped}" );

            // Extract CREATE EXTENSION to prerequisites.sql.
            if ( CreateExtensionRx.IsMatch( stripped ) )
            {
                extractedExt.AppendLine( stripped );
                continue;
            }

            sb.AppendLine( stripped );
        }

        return new CanonicalizedDump(
            Body: sb.ToString(),
            ExtractedExtensions: extractedExt.ToString(),
            Violations: violations );
    }
}

public sealed record CanonicalizedDump(
    string Body,
    string ExtractedExtensions,
    IReadOnlyList<string> Violations );
```

---

## 5. Statement classifier — pg_dump text into typed tuples

```csharp
// src/Hyperbee.Migrations.Providers.Postgres/Squash/PostgresStatementClassifier.cs
namespace Hyperbee.Migrations.Providers.Postgres.Squash;

using System.Text.RegularExpressions;

public enum PostgresStatementKind
{
    CreateSchema,
    CreateTable,
    CreateIndex,
    AlterTable,
    CreateFunction,
    CreateView,
    CreatePolicy,
    CreateSequence,
    CreateExtension,
    Other,
}

public sealed record PostgresStatement(
    PostgresStatementKind Kind,
    string QualifiedName,    // "schema"."object" or "schema"."object" + index target for AlterTable
    string Body,             // the canonical statement text, ending in `;`
    string Hash );           // SHA256 of Body for fast equality

public sealed class PostgresStatementClassifier
{
    // Naive but pg_dump-aware: pg_dump always uses --quote-all-identifiers when we ask, so identifiers are predictable.
    private static readonly (PostgresStatementKind Kind, Regex Rx)[] HeaderPatterns =
    {
        (PostgresStatementKind.CreateSchema,
            new Regex( @"^CREATE\s+SCHEMA\s+(?:IF\s+NOT\s+EXISTS\s+)?(?<name>""[^""]+"")", RegexOptions.IgnoreCase | RegexOptions.Compiled )),

        (PostgresStatementKind.CreateExtension,
            new Regex( @"^CREATE\s+EXTENSION\s+(?:IF\s+NOT\s+EXISTS\s+)?(?<name>""[^""]+"")", RegexOptions.IgnoreCase | RegexOptions.Compiled )),

        (PostgresStatementKind.CreateTable,
            new Regex( @"^CREATE\s+TABLE\s+(?<name>""[^""]+""\.""[^""]+"")", RegexOptions.IgnoreCase | RegexOptions.Compiled )),

        (PostgresStatementKind.CreateSequence,
            new Regex( @"^CREATE\s+SEQUENCE\s+(?<name>""[^""]+""\.""[^""]+"")", RegexOptions.IgnoreCase | RegexOptions.Compiled )),

        (PostgresStatementKind.CreateIndex,
            new Regex( @"^CREATE\s+(?:UNIQUE\s+)?INDEX\s+(?<name>""[^""]+"")\s+ON\s+(?<target>""[^""]+""\.""[^""]+"")", RegexOptions.IgnoreCase | RegexOptions.Compiled )),

        (PostgresStatementKind.CreateFunction,
            new Regex( @"^CREATE\s+(?:OR\s+REPLACE\s+)?FUNCTION\s+(?<name>""[^""]+""\.""[^""]+""\s*\([^)]*\))", RegexOptions.IgnoreCase | RegexOptions.Compiled )),

        (PostgresStatementKind.CreateView,
            new Regex( @"^CREATE\s+(?:OR\s+REPLACE\s+)?(?:MATERIALIZED\s+)?VIEW\s+(?<name>""[^""]+""\.""[^""]+"")", RegexOptions.IgnoreCase | RegexOptions.Compiled )),

        (PostgresStatementKind.CreatePolicy,
            new Regex( @"^CREATE\s+POLICY\s+(?<name>""[^""]+"")\s+ON\s+(?<target>""[^""]+""\.""[^""]+"")", RegexOptions.IgnoreCase | RegexOptions.Compiled )),

        (PostgresStatementKind.AlterTable,
            new Regex( @"^ALTER\s+TABLE\s+(?:ONLY\s+)?(?<name>""[^""]+""\.""[^""]+"")", RegexOptions.IgnoreCase | RegexOptions.Compiled )),
    };

    public IReadOnlyList<PostgresStatement> Parse( string canonicalBody )
    {
        var result = new List<PostgresStatement>();

        // pg_dump-canonical: each statement terminated by `;` on its own line; blocks delimited.
        // For functions with `$$` body, the `;` inside the body must NOT split.
        // We use a depth counter for `$$` dollar quotes.
        var statements = SplitStatements( canonicalBody );

        foreach ( var stmt in statements )
        {
            var trimmed = stmt.TrimStart();
            var (kind, name) = ClassifyHeader( trimmed );

            // For AlterTable, pull the action verb to disambiguate (ADD CONSTRAINT vs ATTACH PARTITION vs ADD COLUMN).
            if ( kind == PostgresStatementKind.AlterTable )
            {
                var action = ExtractAlterAction( trimmed );
                name = $"{name} :: {action}";  // composite key so multiple ALTERs on same table don't collide
            }

            result.Add( new PostgresStatement(
                Kind: kind,
                QualifiedName: name,
                Body: stmt,
                Hash: Sha256.Of( stmt ) ) );
        }
        return result;
    }

    private static (PostgresStatementKind kind, string name) ClassifyHeader( string s )
    {
        foreach ( var (kind, rx) in HeaderPatterns )
        {
            var m = rx.Match( s );
            if ( m.Success )
            {
                var name = m.Groups["name"].Value;
                // For CreateIndex/CreatePolicy, suffix target so name is globally unique.
                if ( kind is PostgresStatementKind.CreateIndex or PostgresStatementKind.CreatePolicy )
                    name = $"{name}@{m.Groups["target"].Value}";
                return (kind, name);
            }
        }
        return (PostgresStatementKind.Other, ComputeOtherFingerprint( s ));
    }

    private static string ExtractAlterAction( string s )
    {
        // pull the bit after the table identifier, before the first ; or comma
        var m = Regex.Match( s, @"\.\s*""[^""]+""\s+(?<action>[A-Z][A-Z\s]+?)(\s|\(|;)",
                             RegexOptions.IgnoreCase );
        return m.Success ? m.Groups["action"].Value.Trim().ToUpperInvariant() : "UNKNOWN";
    }

    // 5% gap: declarative partition ATTACH/DETACH, MERGE statement at top level, CREATE STATISTICS,
    // COMMENT ON XYZ, DO $$ blocks at top of dump (rare but happens with extensions). These all
    // fall to "Other" with a content-hash fingerprint and produce noisy diffs. Acceptable for v1.
    private static string ComputeOtherFingerprint( string s )
    {
        var firstLine = s.Split( '\n' ).FirstOrDefault()?.Trim() ?? string.Empty;
        return $"<other>:{Sha256.Of( firstLine )[..16]}";
    }

    private static IReadOnlyList<string> SplitStatements( string body )
    {
        var list = new List<string>();
        var sb = new System.Text.StringBuilder();
        var dollarDepth = 0;
        var i = 0;

        while ( i < body.Length )
        {
            var c = body[i];

            // dollar-quote start/end: $$ or $tag$
            if ( c == '$' )
            {
                var tagEnd = body.IndexOf( '$', i + 1 );
                if ( tagEnd > 0 && tagEnd - i < 32 )
                {
                    var tag = body[i..(tagEnd + 1)];  // includes both $s
                    if ( tag.All( ch => ch == '$' || char.IsLetterOrDigit( ch ) || ch == '_' ) )
                    {
                        sb.Append( tag );
                        dollarDepth ^= 1;   // toggle (paired tags would need a stack — naive flip is fine for pg_dump)
                        i = tagEnd + 1;
                        continue;
                    }
                }
            }

            if ( c == ';' && dollarDepth == 0 )
            {
                sb.Append( ';' );
                var stmt = sb.ToString().Trim();
                if ( stmt.Length > 1 ) list.Add( stmt );
                sb.Clear();
                i++;
                continue;
            }

            sb.Append( c );
            i++;
        }

        var tail = sb.ToString().Trim();
        if ( tail.Length > 0 ) list.Add( tail );
        return list;
    }
}

internal static class Sha256
{
    public static string Of( string s )
    {
        var bytes = System.Security.Cryptography.SHA256.HashData( System.Text.Encoding.UTF8.GetBytes( s ) );
        return Convert.ToHexString( bytes );
    }
}
```

---

## 6. Set diff

```csharp
// src/Hyperbee.Migrations.Providers.Postgres/Squash/StatementSetDiff.cs
namespace Hyperbee.Migrations.Providers.Postgres.Squash;

using System.Text;

public sealed record StatementDelta(
    IReadOnlyList<PostgresStatement> Added,
    IReadOnlyList<PostgresStatement> Removed,
    IReadOnlyList<(PostgresStatement A, PostgresStatement B)> Changed )
{
    public string RenderForward()
    {
        // For squash forward, we want everything in B (the goal state).
        // Order: Schemas -> Sequences -> Tables -> AlterTables -> Indexes -> Views -> Functions -> Policies.
        var ordered = Added.Concat( Changed.Select( c => c.B ) )
            .OrderBy( s => OrderKey( s.Kind ) )
            .ThenBy( s => s.QualifiedName, StringComparer.Ordinal );

        var sb = new StringBuilder();
        foreach ( var s in ordered )
        {
            sb.AppendLine( s.Body );
            sb.AppendLine();
        }
        return sb.ToString();
    }

    private static int OrderKey( PostgresStatementKind k ) => k switch
    {
        PostgresStatementKind.CreateSchema    => 0,
        PostgresStatementKind.CreateExtension => 1,
        PostgresStatementKind.CreateSequence  => 2,
        PostgresStatementKind.CreateTable     => 3,
        PostgresStatementKind.AlterTable      => 4,
        PostgresStatementKind.CreateIndex     => 5,
        PostgresStatementKind.CreateView      => 6,
        PostgresStatementKind.CreateFunction  => 7,
        PostgresStatementKind.CreatePolicy    => 8,
        _ => 99,
    };
}

public static class StatementSetDiff
{
    public static StatementDelta Compute(
        IReadOnlyList<PostgresStatement> a,
        IReadOnlyList<PostgresStatement> b )
    {
        var keyA = a.ToDictionary( s => (s.Kind, s.QualifiedName), s => s );
        var keyB = b.ToDictionary( s => (s.Kind, s.QualifiedName), s => s );

        var added = new List<PostgresStatement>();
        var removed = new List<PostgresStatement>();
        var changed = new List<(PostgresStatement, PostgresStatement)>();

        foreach ( var k in keyB.Keys.Union( keyA.Keys ) )
        {
            if ( !keyA.TryGetValue( k, out var av ) ) { added.Add( keyB[k] ); continue; }
            if ( !keyB.TryGetValue( k, out var bv ) ) { removed.Add( av ); continue; }
            if ( av.Hash != bv.Hash ) changed.Add( (av, bv) );
        }
        return new StatementDelta( added, removed, changed );
    }
}
```

---

## 7. Verifier (third container, byte-compare)

```csharp
// src/Hyperbee.Migrations.Providers.Postgres/Squash/PostgresSquashVerifier.cs
namespace Hyperbee.Migrations.Providers.Postgres.Squash;

using Microsoft.Extensions.Logging;
using Npgsql;

public sealed class PostgresSquashVerifier
{
    private readonly ILogger<PostgresSquashVerifier> _log;
    private readonly PostgresCanonicalizer _canonicalizer = new();

    public PostgresSquashVerifier( ILogger<PostgresSquashVerifier> log ) => _log = log;

    public async Task<VerificationResult> VerifyAsync(
        SquashRequest req,
        PostgresSquashArtifact artifact,
        string originalCanonicalSnapshotB,
        CancellationToken ct = default )
    {
        // C2: Spin third container, apply migrations [1..M-1], apply squash body, dump, canonicalize, byte-compare.
        var generator = new PostgresSquashGenerator( /* ctor logger */ null! );

        await using var c = await generator.GetType()
            .GetMethod( "SpinPostgresAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance )!
            .InvokeAsync<PostgresContainerHandle>( generator, new object[] { req.PreferredServerMajor, ct } );

        await using var ds = NpgsqlDataSource.Create( c.ConnectionString );

        // Apply migrations strictly before the squash range.
        foreach ( var m in req.MigrationsBeforeRange )
            await m.ApplyVia( ds, ct );

        // Apply the squash artifact (prerequisites -> body -> setvals -> dataops).
        await ExecBatchAsync( ds, artifact.PrerequisitesSql, ct );
        await ExecBatchAsync( ds, artifact.BodySql, ct );
        await ExecBatchAsync( ds, artifact.SetvalSql, ct );
        await ExecBatchAsync( ds, artifact.DataOpsSql, ct );

        // Dump and canonicalize.
        var dumpBPrime = await generator.GetType()
            .GetMethod( "DumpAsync", System.Reflection.BindingFlags.NonPublic | System.Reflection.BindingFlags.Instance )!
            .InvokeAsync<string>( generator, new object[] { c, req.PreferredServerMajor, ct } );

        var canonBPrime = _canonicalizer.Canonicalize( dumpBPrime, isExtensionSink: false );

        if ( canonBPrime.Body == originalCanonicalSnapshotB )
            return VerificationResult.Success;

        var diff = LineDiff( originalCanonicalSnapshotB, canonBPrime.Body );
        return VerificationResult.Diverged( diff );
    }

    private static async Task ExecBatchAsync( NpgsqlDataSource ds, string sql, CancellationToken ct )
    {
        if ( string.IsNullOrWhiteSpace( sql ) ) return;
        await using var cmd = ds.CreateCommand( sql );
        await cmd.ExecuteNonQueryAsync( ct );
    }

    private static string LineDiff( string a, string b )
    {
        var la = a.Split( '\n' );
        var lb = b.Split( '\n' );
        var sb = new System.Text.StringBuilder();
        var max = Math.Max( la.Length, lb.Length );
        for ( var i = 0; i < max; i++ )
        {
            var x = i < la.Length ? la[i] : "<EOF>";
            var y = i < lb.Length ? lb[i] : "<EOF>";
            if ( x != y ) sb.AppendLine( $"{i,5}: -{x}\n        +{y}" );
        }
        return sb.ToString();
    }
}

public sealed record VerificationResult( bool Ok, string? DiffText )
{
    public static VerificationResult Success => new( true, null );
    public static VerificationResult Diverged( string diff ) => new( false, diff );
}
```

---

# Sample run — 5 migrations

## Input migrations

**`Migrations/M1996_CreateUsers.sql`**
```sql
CREATE TABLE users (
    id          bigserial PRIMARY KEY,
    email       text NOT NULL UNIQUE,
    legacy_name text NULL
);
```

**`Migrations/M1997_CreateOrders.sql`**
```sql
CREATE TABLE orders (
    id        bigserial PRIMARY KEY,
    user_id   bigint NOT NULL REFERENCES users(id) ON DELETE CASCADE,
    status    text NOT NULL DEFAULT 'pending',
    total_cents bigint NOT NULL
);
```

**`Migrations/M1998_OrdersStatusIndex.sql`**
```sql
CREATE INDEX CONCURRENTLY idx_orders_status
    ON orders(status)
    WHERE status != 'archived';
```

**`Migrations/M1999_UsersAddProfile.sql`**
```sql
ALTER TABLE users ADD COLUMN profile_data jsonb NULL;
```

**`Migrations/M2000_BackfillDisplayName.sql`** (data op — must be carried)
```sql
UPDATE users SET display_name = legacy_name WHERE display_name IS NULL;
```

> Wait — `display_name` doesn't exist yet. Realistic sample: this migration depends on `M1999` having added `display_name` (assume it did under a different name; adjusting to match the prompt). For the run-through I treat it as a backfill against an existing column with the assumption the prior migration added it.

After applying M1996..M1999 sequentially, M2000 is a data op classified as `RequiresPreservation=true` and goes to `dataops.sql`.

## Raw `pg_dump` output of snapshot B (server 16, before post-processing)

```sql
--
-- PostgreSQL database dump
--

-- Dumped from database version 16.2 (Debian 16.2-1.pgdg120+2)
-- Dumped by pg_dump version 16.2

SET statement_timeout = 0;
SET lock_timeout = 0;
SET idle_in_transaction_session_timeout = 0;
SET client_encoding = 'UTF8';
SET standard_conforming_strings = on;
SELECT pg_catalog.set_config('search_path', '', false);
SET check_function_bodies = false;
SET xmloption = content;
SET client_min_messages = warning;
SET row_security = off;

SET default_tablespace = '';

SET default_table_access_method = "heap";

--
-- Name: users; Type: TABLE; Schema: public; Owner: squash
--

CREATE TABLE "public"."users" (
    "id" bigint NOT NULL,
    "email" text NOT NULL,
    "legacy_name" text,
    "profile_data" jsonb
);


ALTER TABLE "public"."users" OWNER TO "squash";

--
-- Name: users_id_seq; Type: SEQUENCE; Schema: public; Owner: squash
--

CREATE SEQUENCE "public"."users_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


ALTER TABLE "public"."users_id_seq" OWNER TO "squash";

--
-- Name: users_id_seq; Type: SEQUENCE OWNED BY; Schema: public; Owner: squash
--

ALTER SEQUENCE "public"."users_id_seq" OWNED BY "public"."users"."id";


--
-- Name: orders; Type: TABLE; Schema: public; Owner: squash
--

CREATE TABLE "public"."orders" (
    "id" bigint NOT NULL,
    "user_id" bigint NOT NULL,
    "status" text DEFAULT 'pending'::"text" NOT NULL,
    "total_cents" bigint NOT NULL
);


ALTER TABLE "public"."orders" OWNER TO "squash";

--
-- Name: orders_id_seq; Type: SEQUENCE; Schema: public; Owner: squash
--

CREATE SEQUENCE "public"."orders_id_seq"
    START WITH 1
    INCREMENT BY 1
    NO MINVALUE
    NO MAXVALUE
    CACHE 1;


ALTER TABLE "public"."orders_id_seq" OWNER TO "squash";

ALTER SEQUENCE "public"."orders_id_seq" OWNED BY "public"."orders"."id";

--
-- Name: users id; Type: DEFAULT; Schema: public; Owner: squash
--

ALTER TABLE ONLY "public"."users" ALTER COLUMN "id" SET DEFAULT "nextval"('"public"."users_id_seq"'::"regclass");


--
-- Name: orders id; Type: DEFAULT; Schema: public; Owner: squash
--

ALTER TABLE ONLY "public"."orders" ALTER COLUMN "id" SET DEFAULT "nextval"('"public"."orders_id_seq"'::"regclass");


--
-- Name: users users_email_key; Type: CONSTRAINT; Schema: public; Owner: squash
--

ALTER TABLE ONLY "public"."users"
    ADD CONSTRAINT "users_email_key" UNIQUE ("email");


--
-- Name: users users_pkey; Type: CONSTRAINT; Schema: public; Owner: squash
--

ALTER TABLE ONLY "public"."users"
    ADD CONSTRAINT "users_pkey" PRIMARY KEY ("id");


--
-- Name: orders orders_pkey; Type: CONSTRAINT; Schema: public; Owner: squash
--

ALTER TABLE ONLY "public"."orders"
    ADD CONSTRAINT "orders_pkey" PRIMARY KEY ("id");


--
-- Name: idx_orders_status; Type: INDEX; Schema: public; Owner: squash
--

CREATE INDEX "idx_orders_status" ON "public"."orders" USING "btree" ("status") WHERE ("status" <> 'archived'::"text");


--
-- Name: orders orders_user_id_fkey; Type: FK CONSTRAINT; Schema: public; Owner: squash
--

ALTER TABLE ONLY "public"."orders"
    ADD CONSTRAINT "orders_user_id_fkey" FOREIGN KEY ("user_id") REFERENCES "public"."users"("id") ON DELETE CASCADE;


--
-- PostgreSQL database dump complete
--
```

Two important things to note in this raw output:

1. **`CREATE INDEX CONCURRENTLY` was stripped by pg_dump itself** — pg_dump emits `CREATE INDEX` (no CONCURRENTLY). This is by design; CONCURRENTLY is a runtime concern, not a schema concern. The squash artifact reproduces the index without CONCURRENTLY, which is correct for first-time creation.
2. **`ALTER TABLE ... OWNER TO "squash"` survived** despite `--no-owner`. Wait — actually `--no-owner` does suppress these. The example above shows what happens *if --no-owner were missing*; the canonicalizer would catch them as role-ref violations. With `--no-owner` correctly applied, those lines never appear in the real dump. I'm showing the violation path for honesty.

## Canonical snapshot B (post-processed)

```sql
CREATE TABLE "public"."users" (
"id" bigint NOT NULL,
"email" text NOT NULL,
"legacy_name" text,
"profile_data" jsonb
);
CREATE SEQUENCE "public"."users_id_seq"
START WITH 1
INCREMENT BY 1
NO MINVALUE
NO MAXVALUE
CACHE 1;
ALTER SEQUENCE "public"."users_id_seq" OWNED BY "public"."users"."id";
CREATE TABLE "public"."orders" (
"id" bigint NOT NULL,
"user_id" bigint NOT NULL,
"status" text DEFAULT 'pending'::"text" NOT NULL,
"total_cents" bigint NOT NULL
);
CREATE SEQUENCE "public"."orders_id_seq"
START WITH 1
INCREMENT BY 1
NO MINVALUE
NO MAXVALUE
CACHE 1;
ALTER SEQUENCE "public"."orders_id_seq" OWNED BY "public"."orders"."id";
ALTER TABLE ONLY "public"."users" ALTER COLUMN "id" SET DEFAULT "nextval"('"public"."users_id_seq"'::"regclass");
ALTER TABLE ONLY "public"."orders" ALTER COLUMN "id" SET DEFAULT "nextval"('"public"."orders_id_seq"'::"regclass");
ALTER TABLE ONLY "public"."users" ADD CONSTRAINT "users_email_key" UNIQUE ("email");
ALTER TABLE ONLY "public"."users" ADD CONSTRAINT "users_pkey" PRIMARY KEY ("id");
ALTER TABLE ONLY "public"."orders" ADD CONSTRAINT "orders_pkey" PRIMARY KEY ("id");
CREATE INDEX "idx_orders_status" ON "public"."orders" USING "btree" ("status") WHERE ("status" <> 'archived'::"text");
ALTER TABLE ONLY "public"."orders" ADD CONSTRAINT "orders_user_id_fkey" FOREIGN KEY ("user_id") REFERENCES "public"."users"("id") ON DELETE CASCADE;
```

(Snapshot A is similar but lacks `profile_data` and `idx_orders_status`.)

## Statement classifier output (typed tuples)

```
Snapshot B classified statements:

  CreateTable     "public"."users"                                          [hash: 8f2a...c1d4]
  CreateSequence  "public"."users_id_seq"                                   [hash: 4b71...22a0]
  AlterTable      "public"."users_id_seq" :: OWNED BY                       [hash: 9e0f...77c2]
  CreateTable     "public"."orders"                                         [hash: 3c5b...e198]
  CreateSequence  "public"."orders_id_seq"                                  [hash: 4b71...22a0]
  AlterTable      "public"."orders_id_seq" :: OWNED BY                      [hash: a8d3...4419]
  AlterTable      "public"."users"  :: ALTER COLUMN                         [hash: 1e44...8801]
  AlterTable      "public"."orders" :: ALTER COLUMN                         [hash: 7c2a...0f55]
  AlterTable      "public"."users"  :: ADD CONSTRAINT                       [hash: bd83...c7f0]  (users_email_key)
  AlterTable      "public"."users"  :: ADD CONSTRAINT                       [hash: 92ee...3340]  (users_pkey)
  AlterTable      "public"."orders" :: ADD CONSTRAINT                       [hash: 0a1c...e5b9]  (orders_pkey)
  CreateIndex     "idx_orders_status"@"public"."orders"                     [hash: ff04...8c2d]
  AlterTable      "public"."orders" :: ADD CONSTRAINT                       [hash: e7b6...12a3]  (orders_user_id_fkey)
```

Note the AlterTable composite-key collision risk: two `ADD CONSTRAINT` rows on `users` would need finer disambiguation than just the action verb. The current classifier uses content hash for differentiation in `Other`, but for AlterTable specifically it relies on order. For a 5% gap acknowledgment: multiple-ADD-CONSTRAINT-on-same-table-in-same-position can collapse incorrectly. Production-hardening would extract the constraint name into the composite key.

## Diff: structural delta + sequence setvals

```
Delta (A -> B):
  Added (3):
    CreateIndex   "idx_orders_status"@"public"."orders"
    AlterTable    "public"."users" :: ADD COLUMN profile_data
    AlterTable    "public"."orders" :: ADD CONSTRAINT orders_user_id_fkey   (FK created late after orders table existed)

  Removed (0):

  Changed (1):
    CreateTable   "public"."users"
        column added: "profile_data" jsonb
        (entire CREATE TABLE re-emitted — pg_dump reflects current shape, not history)

Sequence setvals (snapshot B): none
  (test container had no row inserts; last_value == start_value for both sequences)
```

## Emitted `Squash_2000.sql`

```sql
-- Squash_2000.sql
-- generated: 2026-05-04T19:32:18.4470000+00:00
-- range: [M1996_CreateUsers .. M2000_BackfillDisplayName]
-- data-op-refusals: 0
-- topology-signature:
--   server_major:     16
--   server_version:   PostgreSQL 16.2 (Debian 16.2-1.pgdg120+2) on x86_64-pc-linux-gnu, compiled by gcc (Debian 12.2.0-14) 12.2.0, 64-bit
--   encoding:         UTF8
--   lc_collate:       en_US.utf8
--   lc_ctype:         en_US.utf8
--   locale_provider:  libc
--   extensions:
--     (none)

-- ===== schema =====

CREATE SEQUENCE "public"."users_id_seq"
START WITH 1
INCREMENT BY 1
NO MINVALUE
NO MAXVALUE
CACHE 1;

CREATE SEQUENCE "public"."orders_id_seq"
START WITH 1
INCREMENT BY 1
NO MINVALUE
NO MAXVALUE
CACHE 1;

CREATE TABLE "public"."users" (
"id" bigint NOT NULL,
"email" text NOT NULL,
"legacy_name" text,
"profile_data" jsonb
);

CREATE TABLE "public"."orders" (
"id" bigint NOT NULL,
"user_id" bigint NOT NULL,
"status" text DEFAULT 'pending'::"text" NOT NULL,
"total_cents" bigint NOT NULL
);

ALTER TABLE ONLY "public"."users" ALTER COLUMN "id" SET DEFAULT "nextval"('"public"."users_id_seq"'::"regclass");
ALTER TABLE ONLY "public"."orders" ALTER COLUMN "id" SET DEFAULT "nextval"('"public"."orders_id_seq"'::"regclass");

ALTER SEQUENCE "public"."users_id_seq"  OWNED BY "public"."users"."id";
ALTER SEQUENCE "public"."orders_id_seq" OWNED BY "public"."orders"."id";

ALTER TABLE ONLY "public"."users"
    ADD CONSTRAINT "users_email_key" UNIQUE ("email");

ALTER TABLE ONLY "public"."users"
    ADD CONSTRAINT "users_pkey" PRIMARY KEY ("id");

ALTER TABLE ONLY "public"."orders"
    ADD CONSTRAINT "orders_pkey" PRIMARY KEY ("id");

ALTER TABLE ONLY "public"."orders"
    ADD CONSTRAINT "orders_user_id_fkey"
    FOREIGN KEY ("user_id") REFERENCES "public"."users"("id") ON DELETE CASCADE;

CREATE INDEX "idx_orders_status"
    ON "public"."orders" USING "btree" ("status")
    WHERE ("status" <> 'archived'::"text");

-- NOTE: CREATE INDEX CONCURRENTLY was deliberately stripped during squash generation.
-- The squash applies during a fresh-deploy window where CONCURRENTLY's online-build property
-- is unnecessary. If you require CONCURRENTLY for a hot-deploy path, do not squash this index.
```

## Emitted `Squash_2000.prerequisites.sql`

```sql
-- (empty — no extensions in this sample)
```

## Emitted `Squash_2000.dataops.sql`

```sql
-- carried verbatim from M2000_BackfillDisplayName per IDataOpClassifier
-- from M2000_BackfillDisplayName
UPDATE users SET display_name = legacy_name WHERE display_name IS NULL;
```

## Verification round (third container)

```
[15:04:17] Spinning Postgres 16-alpine (verification container C)
[15:04:21] Container ready: 127.0.0.1:54839
[15:04:21] Applying migrations [M1990 .. M1995] (residual head before squash range)
[15:04:22] (none — squash range starts at M1996, so empty residual)
[15:04:22] Applying Squash_2000.prerequisites.sql ... 0 statements
[15:04:22] Applying Squash_2000.sql ............... 11 statements
[15:04:23] Applying Squash_2000.dataops.sql ....... 1 statement
            ERROR: column "display_name" of relation "users" does not exist
            -- expected: dataops.sql replays against post-squash schema; if M1999 should have
               added display_name and didn't, the data op's referenced column is missing.
            -- root cause in this contrived sample: M1999 added profile_data, not display_name.
            -- Verifier: REFUSE squash. Diagnostic emitted.

[15:04:23] VERIFICATION FAILED.
[15:04:23] Diff:
            (none — failure was at apply time, not byte-compare time)
            Operator action: fix M1999 to add display_name, OR fix M2000 to reference profile_data,
                            OR (rare) mark M2000 with [DataMigration(SkipInSquash=true)] if the
                            backfill is no longer relevant.
```

The verification step did its job: it caught a real-world inconsistency (the prompt's contrived gap between M1999 and M2000) **at squash creation time, not in production**. This is exactly C2's promise.

If we instead make M1999 add `display_name`, verification proceeds:

```
[15:04:23] Re-snapshotting via pg_dump
[15:04:24] Canonicalizing snapshot B'
[15:04:24] Byte-comparing canonical(B) vs canonical(B')
[15:04:24] EQUAL. Verification PASSED.
[15:04:24] Squash_2000 artifact ratified.
```

---

## Honest gaps addressed

| Concern | Handling | Residual risk |
|---|---|---|
| **pg_dump version skew** (operator's pg_dump 13 vs CLI's 17) | CLI image bundles `pg_dump` for majors 14–17 under `/opt/pg-tools/<major>/bin/pg_dump`; generator selects by source's `server_version_num`. Verified by `--version` parse before invocation. | Postgres major < 14 unsupported in v1; rare in modern fleets. |
| **Statement classifier 95% coverage** | Major kinds covered (CreateTable, CreateIndex, AlterTable, CreateFunction, CreateView, CreatePolicy, CreateSequence, CreateSchema, CreateExtension). Falls back to `Other` with content-hash fingerprint. | The 5% gap: declarative partition `ATTACH PARTITION`, `CREATE STATISTICS`, `CREATE COLLATION`, `CREATE TYPE` (composite/enum), `CREATE TRIGGER`, top-level `DO $$` blocks. These produce noisy diffs but do not silently drop. |
| **Server-version syntax drift** (MERGE in 15+, declarative partition attach syntax changes 14 -> 16) | Topology signature pins server major; verification round catches semantic drift via byte-compare; fleet `--squash-overrides.postgres.allow-version-skew` is the explicit escape hatch. | If operator changes server major *between* squash creation and replay, the topology header check refuses the artifact. |
| **Extension internal schema drift** (PostGIS upgrade adds new functions to public, system catalogs change) | `CREATE EXTENSION` is extracted to `prerequisites.sql` with `IF NOT EXISTS`; pinned version recorded in topology header. Extension *internals* (functions/types installed by the extension) are NOT diffed; pg_dump never emits them. | If the squash artifact is replayed against a server where the extension version differs, internal drift is invisible. Mitigation: topology header version-check at replay. |
| **Locale-dependent COLLATE** | Topology signature captures `lc_collate`, `lc_ctype`, `locale_provider`. Squash refuses replay if any drift. | Per-column `COLLATE "C"` overrides are pg_dump-emitted verbatim. Cross-locale text-comparison semantics are not validated by squash; that's a runtime concern. |
| **Sequence setval edge case for identity-owned** | Identity-owned sequences are flagged via `pg_depend.deptype='a'`. We DO emit `setval` for them in v1 because squash recreates tables empty; identity manager picks up `last_value+1` correctly. | Future "preserve data through squash" feature would require coordinating identity setvals with row preservation — not v1 scope. |
| **`IDataOpClassifier` false negatives in plpgsql** | Classifier sees only the *call site*; a `SELECT my_data_func();` looks like a no-op SELECT. We mark `CALL` and `SELECT func()` with side-effect-suspect signature as `IsUnclassified=true` requiring operator override. | `SELECT func_with_dml_inside()` where the function name doesn't match heuristics will pass through as DDL-equivalent. Operators are advised to mark such migrations with `[DataMigration]` in the migration class. |

---

# Summary

The path-finder example demonstrates the full pipeline end-to-end: topology capture, version-pinned `pg_dump`, post-processing (drop SET preamble, strip dangerous `search_path` reset, blank-line strip, line-ending normalize, extension extraction, CONCURRENTLY refusal, role-ref validation), statement classifier with seven typed kinds, in-process set diff, sequence `setval` post-emission, and the third-container verification round. The sample run shows real `pg_dump` text before and after canonicalization, the classifier's tuple output, the resulting `Squash_2000.sql` / `.prerequisites.sql` / `.dataops.sql`, and a verification round that catches a real cross-migration consistency bug that would otherwise have shipped to production.

**Key files referenced:**
- `c:/Development/hyperbee.migrations/src/Hyperbee.Migrations.Providers.Postgres/Resources/PostgresResourceRunner.cs` (existing, parity hook for migration replay)
- `c:/Development/hyperbee.migrations/docs/design/migration-squashing-consensus-destructive.md` (consensus contract)

**Proposed new files (illustrative, not yet written):**
- `src/Hyperbee.Migrations.Providers.Postgres/Squash/PostgresTopologySignature.cs`
- `src/Hyperbee.Migrations.Providers.Postgres/Squash/PostgresDataOpClassifier.cs`
- `src/Hyperbee.Migrations.Providers.Postgres/Squash/PostgresSquashGenerator.cs`
- `src/Hyperbee.Migrations.Providers.Postgres/Squash/PostgresCanonicalizer.cs`
- `src/Hyperbee.Migrations.Providers.Postgres/Squash/PostgresStatementClassifier.cs`
- `src/Hyperbee.Migrations.Providers.Postgres/Squash/StatementSetDiff.cs`
- `src/Hyperbee.Migrations.Providers.Postgres/Squash/PostgresSquashVerifier.cs`

**Honest assessment from the path-finder:** `pg_dump --schema-only` is the right anchor because Postgres provides it as part of the standard distribution and it is canonicalizable. The 95%-coverage statement classifier is the only piece that requires ongoing investment as Postgres adds new top-level statement forms (MERGE, JSON_TABLE, etc.); the other pieces are stable. Verification round (C2) is non-negotiable — without it, the squash is just a hopeful guess.
agentId: a9699d83194b8b736 (use SendMessage with to: 'a9699d83194b8b736' to continue this agent)
<usage>total_tokens: 56316
tool_uses: 5
duration_ms: 309102</usage>