using System.Text;
using System.Text.RegularExpressions;
using Hyperbee.Migrations.Squash;

namespace Hyperbee.Migrations.Providers.Postgres.Squash;

/// <summary>
/// Postgres-flavored <see cref="ISnapshotCanonicalizer"/>. Normalizes
/// <c>pg_dump --schema-only</c> output into a deterministic byte form for
/// the C12 generation determinism gate (per ADR-0019).
/// </summary>
/// <remarks>
/// The canonical pipeline (per spike findings F1, F3, F5):
/// <list type="number">
///   <item>Strip the <c>SET</c> session preamble (server-version-dependent).</item>
///   <item>Strip <c>\restrict</c> / <c>\unrestrict</c> psql directives (pg_dump 16+).</item>
///   <item>Strip the <c>SELECT pg_catalog.set_config('search_path', '', false);</c> line.</item>
///   <item>Strip pg_dump banner comments
///         (<c>-- PostgreSQL database dump</c>, <c>-- Dumped from database
///         version ...</c>, <c>-- Name: ...; Type: ...</c>, etc.).</item>
///   <item>Normalize line endings to LF, encoding UTF-8 no BOM.</item>
///   <item>Collapse blank-line runs to a single blank line; trim trailing
///         whitespace per line.</item>
///   <item>Refuse <c>CREATE INDEX CONCURRENTLY</c> as a defense-in-depth
///         signal (per ADR-0019 A2; pg_dump --schema-only doesn't emit it,
///         but operator-authored migrations might leak in).</item>
/// </list>
/// </remarks>
public sealed class PostgresSnapshotCanonicalizer : ISnapshotCanonicalizer
{
    public string ProviderId => PostgresTopologySignature.ProviderIdValue;

    private static readonly Regex SetPreambleLine = new(
        @"^\s*(SET|RESET)\s+", RegexOptions.IgnoreCase | RegexOptions.Compiled );

    private static readonly Regex SearchPathConfigLine = new(
        @"^\s*SELECT\s+pg_catalog\.set_config\s*\(\s*'search_path'\s*,",
        RegexOptions.IgnoreCase | RegexOptions.Compiled );

    private static readonly Regex PsqlDirectiveLine = new(
        @"^\s*\\\w+", RegexOptions.Compiled );

    private static readonly Regex PgDumpBannerLine = new(
        @"^\s*--\s+(PostgreSQL database dump|Dumped from database|Dumped by pg_dump|" +
        @"Name:\s.*Type:\s|TOC entry|Type:|Section:)",
        RegexOptions.IgnoreCase | RegexOptions.Compiled );

    private static readonly Regex CreateIndexConcurrently = new(
        @"\bCREATE\s+(?:UNIQUE\s+)?INDEX\s+CONCURRENTLY\b",
        RegexOptions.IgnoreCase | RegexOptions.Compiled );

    public string Canonicalize( string snapshot )
    {
        ArgumentNullException.ThrowIfNull( snapshot );

        // CONCURRENTLY refusal (ADR-0019 A2). Surface as a clear diagnostic
        // before any line filtering happens.
        if ( CreateIndexConcurrently.IsMatch( snapshot ) )
        {
            throw new MigrationException(
                "Snapshot contains `CREATE INDEX CONCURRENTLY` — refused per ADR-0019 amendment A2. " +
                "pg_dump --schema-only does not emit it; check for operator-authored migrations leaking " +
                "into the squash range. Drop CONCURRENTLY or exclude the offending migration before squash." );
        }

        // Normalize line endings before line-oriented filtering so \r\n inputs
        // don't confuse the stride.
        var unified = snapshot.Replace( "\r\n", "\n" ).Replace( '\r', '\n' );

        var sb = new StringBuilder( unified.Length );
        var prevLineWasBlank = false;
        var preambleStripped = false;

        foreach ( var raw in unified.Split( '\n' ) )
        {
            var line = raw.TrimEnd();

            // Strip pg_dump preamble lines unconditionally.
            if ( !preambleStripped )
            {
                if ( PsqlDirectiveLine.IsMatch( line )
                     || SetPreambleLine.IsMatch( line )
                     || SearchPathConfigLine.IsMatch( line )
                     || PgDumpBannerLine.IsMatch( line ) )
                {
                    continue;
                }

                // First non-stripped, non-blank line marks end of preamble.
                if ( !string.IsNullOrWhiteSpace( line ) )
                    preambleStripped = true;
                else
                    continue; // skip leading blank lines
            }

            // Post-preamble: still drop psql directives (trailing \unrestrict),
            // pg_dump banner comments scattered between objects, and SET lines
            // (rare but possible from user-shipped pg_settings tweaks).
            if ( PsqlDirectiveLine.IsMatch( line )
                 || PgDumpBannerLine.IsMatch( line )
                 || SetPreambleLine.IsMatch( line )
                 || SearchPathConfigLine.IsMatch( line ) )
            {
                continue;
            }

            // Collapse blank-line runs.
            if ( string.IsNullOrWhiteSpace( line ) )
            {
                if ( prevLineWasBlank )
                    continue;
                prevLineWasBlank = true;
                sb.Append( '\n' );
                continue;
            }

            prevLineWasBlank = false;
            sb.Append( line ).Append( '\n' );
        }

        // Trim trailing blank lines (the loop's append always emits a newline).
        var result = sb.ToString().TrimEnd( '\n' ) + "\n";
        return result;
    }

    /// <summary>
    /// Emit canonical script-form output (per ADR-0022). For Postgres this is
    /// equivalent to the canonicalized snapshot — pg_dump's output IS already
    /// script form. Provider extensions (e.g., function-body dollar-tag
    /// normalization) layer on top via additional canonicalization passes if
    /// needed; v1 does not normalize tags because they're already opaque to
    /// the diff at the classifier level.
    /// </summary>
    public string EmitScript( string canonicalContent ) => canonicalContent;
}
