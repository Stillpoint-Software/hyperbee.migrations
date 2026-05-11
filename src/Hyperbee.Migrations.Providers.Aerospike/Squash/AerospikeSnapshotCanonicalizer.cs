using System.Globalization;
using System.Text;
using Hyperbee.Migrations.Providers.Aerospike.Parsers;
using Hyperbee.Migrations.Squash;

namespace Hyperbee.Migrations.Providers.Aerospike.Squash;

/// <summary>
/// Canonicalizes an Aerospike snapshot blob into deterministic AQL script form.
/// </summary>
/// <remarks>
/// <para>
/// V1 input contract (produced by <c>InfoSnapshotStrategy</c>): a multi-line
/// blob with section headers, where each section's body is the verbatim
/// <c>Info.Request</c> response for that section. Section headers are
/// case-insensitive line-leading <c>[sets]</c> and <c>[sindex]</c>. Comments
/// (lines starting with <c>#</c>) and blank lines between sections are
/// permitted and ignored.
/// </para>
/// <para>
/// Example input:
/// <code>
/// # aerospike-snapshot v1
/// # namespace: test
///
/// [sets]
/// ns=test:set=users:objects=1234:tombstones=0:memory_used=5012345;ns=test:set=orders:objects=42
///
/// [sindex]
/// ns=test:indexname=idx_email:set=users:bin=email:type=STRING:state=RW:keys=1234;ns=test:indexname=idx_age:set=users:bin=age:type=NUMERIC
/// </code>
/// </para>
/// <para>
/// Canonicalization steps:
/// <list type="number">
///   <item>Parse <c>[sets]</c> entries, extract <c>ns</c>+<c>set</c>, drop the
///         ephemeral fields (<c>objects</c>, <c>tombstones</c>,
///         <c>memory_used</c>, <c>truncate_lut</c>, etc.).</item>
///   <item>Parse <c>[sindex]</c> entries, extract <c>ns</c>+<c>indexname</c>+
///         <c>set</c>+<c>bin</c>+<c>type</c>, drop runtime counters
///         (<c>state</c>, <c>keys</c>, <c>entries</c>,
///         <c>ibtr_memory_used</c>, etc.).</item>
///   <item>Sort each section's entries by canonical key: sets by
///         <c>(ns, set)</c>; indexes by <c>(ns, indexname)</c>.</item>
///   <item>Emit AQL statements: <c>CREATE SET ns.set;</c> for each set,
///         <c>CREATE INDEX WAIT name ON ns.set(bin) TYPE;</c> for each index,
///         line ends normalized to <c>\n</c>.</item>
/// </list>
/// Already-canonical input (statement form, no section headers) is parsed via
/// <see cref="AerospikeStatementClassifier"/> and re-emitted -- making
/// <see cref="Canonicalize"/> idempotent.
/// </para>
/// <para>
/// UDF capture is deferred from v1 (per Task 1.3 / 1.4 deferral notes); the
/// canonicalizer does not parse a <c>[udfs]</c> section.
/// </para>
/// </remarks>
public sealed class AerospikeSnapshotCanonicalizer : ISnapshotCanonicalizer
{
    public string ProviderId => AerospikeTopologySignature.ProviderIdValue;

    public string Canonicalize( string snapshot )
    {
        ArgumentNullException.ThrowIfNull( snapshot );

        var sections = ParseSections( snapshot );

        if ( sections.HasAny )
            return EmitFromSections( sections );

        // No [sets]/[sindex] section headers -- treat input as already-canonical
        // AQL statement form. Parse via the statement classifier and re-emit so
        // the output is sorted and normalized regardless of operator-authored
        // ordering.
        return EmitFromStatements( snapshot );
    }

    public string EmitScript( string canonicalContent ) => Canonicalize( canonicalContent );

    // ---- section parsing ----------------------------------------------------

    internal sealed record Sections(
        string Sets,
        string SIndex )
    {
        public bool HasAny => Sets != null || SIndex != null;
    }

    internal static Sections ParseSections( string snapshot )
    {
        string sets = null;
        string sindex = null;
        string current = null;
        var buffer = new StringBuilder();

        foreach ( var line in snapshot.Split( '\n' ) )
        {
            var trimmed = line.TrimEnd( '\r' );

            if ( trimmed.StartsWith( '[' ) && trimmed.EndsWith( ']' ) )
            {
                Flush( current, buffer, ref sets, ref sindex );
                current = trimmed.Substring( 1, trimmed.Length - 2 ).Trim().ToLowerInvariant();
                buffer.Clear();
                continue;
            }

            if ( current == null )
                continue; // preamble before first section is ignored

            buffer.Append( trimmed ).Append( '\n' );
        }

        Flush( current, buffer, ref sets, ref sindex );

        return new Sections( sets, sindex );
    }

    private static void Flush( string section, StringBuilder buffer, ref string sets, ref string sindex )
    {
        if ( section == null || buffer.Length == 0 )
            return;

        var content = buffer.ToString().Trim();
        if ( content.Length == 0 )
            return;

        switch ( section )
        {
            case "sets":
                sets = content;
                break;
            case "sindex":
                sindex = content;
                break;
        }
    }

    // ---- info-response parsing ---------------------------------------------

    // Aerospike info responses concatenate entries with `;`. Each entry is a
    // bag of `key=value` pairs separated by `:`. Multi-line content is treated
    // as a single logical response (newlines collapsed to nothing first).
    internal static IEnumerable<IReadOnlyDictionary<string, string>> ParseEntries( string response )
    {
        if ( string.IsNullOrEmpty( response ) )
            yield break;

        var collapsed = response.Replace( "\n", "" ).Replace( "\r", "" );

        foreach ( var entry in collapsed.Split( ';', StringSplitOptions.RemoveEmptyEntries ) )
        {
            var map = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
            foreach ( var pair in entry.Split( ':', StringSplitOptions.RemoveEmptyEntries ) )
            {
                var eq = pair.IndexOf( '=' );
                if ( eq <= 0 )
                    continue;

                var key = pair.Substring( 0, eq ).Trim();
                var value = pair.Substring( eq + 1 ).Trim();
                map[key] = value;
            }

            if ( map.Count > 0 )
                yield return map;
        }
    }

    // ---- emission ----------------------------------------------------------

    private static string EmitFromSections( Sections sections )
    {
        var sets = new SortedSet<(string Ns, string Set)>();
        if ( sections.Sets != null )
        {
            foreach ( var entry in ParseEntries( sections.Sets ) )
            {
                if ( !entry.TryGetValue( "ns", out var ns ) || !entry.TryGetValue( "set", out var set ) )
                    continue;
                sets.Add( (ns, set) );
            }
        }

        var indexes = new SortedSet<IndexKey>();
        if ( sections.SIndex != null )
        {
            foreach ( var entry in ParseEntries( sections.SIndex ) )
            {
                if ( !entry.TryGetValue( "ns", out var ns ) ||
                     !entry.TryGetValue( "indexname", out var name ) ||
                     !entry.TryGetValue( "set", out var set ) ||
                     !entry.TryGetValue( "bin", out var bin ) )
                    continue;

                var type = NormalizeIndexType( entry.TryGetValue( "type", out var t ) ? t : null );
                indexes.Add( new IndexKey( ns, name, set, bin, type ) );
            }
        }

        return Compose( sets, indexes );
    }

    private static string EmitFromStatements( string snapshot )
    {
        var sets = new SortedSet<(string Ns, string Set)>();
        var indexes = new SortedSet<IndexKey>();

        foreach ( var rawStatement in SplitStatements( snapshot ) )
        {
            var classified = AerospikeStatementClassifier.Classify( rawStatement );

            switch ( classified.Kind )
            {
                case AerospikeStatementKind.CreateSet:
                    sets.Add( (classified.Namespace, classified.SetName) );
                    break;

                case AerospikeStatementKind.CreateIndex:
                    var item = new AerospikeStatementParser().ParseStatement( rawStatement );
                    indexes.Add( new IndexKey(
                        item.Namespace,
                        item.IndexName,
                        item.SetName,
                        item.BinName,
                        NormalizeIndexType( item.IndexType.ToString().ToUpperInvariant() ) ) );
                    break;
            }
        }

        return Compose( sets, indexes );
    }

    internal static IEnumerable<string> SplitStatements( string script )
    {
        // Strip `--` / `//` comment lines BEFORE the splitter sees them so that
        // accumulated statement text never carries a leading comment line that
        // would defeat the per-statement filter below.
        var cleaned = StripCommentLines( script );

        var current = new StringBuilder();
        var inSingle = false;
        var inDouble = false;
        var inBacktick = false;

        foreach ( var ch in cleaned )
        {
            switch ( ch )
            {
                case '\'' when !inDouble && !inBacktick:
                    inSingle = !inSingle;
                    current.Append( ch );
                    break;
                case '"' when !inSingle && !inBacktick:
                    inDouble = !inDouble;
                    current.Append( ch );
                    break;
                case '`' when !inSingle && !inDouble:
                    inBacktick = !inBacktick;
                    current.Append( ch );
                    break;
                case ';' when !inSingle && !inDouble && !inBacktick:
                    var text = current.ToString().Trim();
                    if ( text.Length > 0 )
                        yield return text;
                    current.Clear();
                    break;
                default:
                    current.Append( ch );
                    break;
            }
        }

        var tail = current.ToString().Trim();
        if ( tail.Length > 0 )
            yield return tail;
    }

    private static string StripCommentLines( string script )
    {
        var sb = new StringBuilder( script.Length );

        foreach ( var rawLine in script.Split( '\n' ) )
        {
            var line = rawLine.TrimEnd( '\r' );
            var stripped = line.TrimStart();
            if ( stripped.StartsWith( "--", StringComparison.Ordinal ) ||
                 stripped.StartsWith( "//", StringComparison.Ordinal ) )
                continue;

            sb.Append( line ).Append( '\n' );
        }

        return sb.ToString();
    }

    private static string Compose(
        SortedSet<(string Ns, string Set)> sets,
        SortedSet<IndexKey> indexes )
    {
        var sb = new StringBuilder();
        sb.Append( "-- aerospike-squash v1\n" );
        sb.Append( '\n' );

        if ( sets.Count > 0 )
        {
            sb.Append( "-- sets\n" );
            foreach ( var (ns, set) in sets )
                sb.Append( CultureInfo.InvariantCulture, $"CREATE SET {ns}.{set};\n" );
            sb.Append( '\n' );
        }

        if ( indexes.Count > 0 )
        {
            sb.Append( "-- secondary indexes\n" );
            foreach ( var idx in indexes )
            {
                sb.Append( CultureInfo.InvariantCulture,
                    $"CREATE INDEX WAIT {idx.Name} ON {idx.Namespace}.{idx.Set}({idx.Bin}) {idx.Type};\n" );
            }
        }

        return sb.ToString();
    }

    private static string NormalizeIndexType( string raw )
    {
        if ( string.IsNullOrEmpty( raw ) )
            return "STRING";

        var upper = raw.ToUpperInvariant();
        return upper switch
        {
            "STRING" or "NUMERIC" or "GEO2DSPHERE" => upper,
            "DEFAULT" => "STRING",
            _ => upper
        };
    }

    private readonly record struct IndexKey( string Namespace, string Name, string Set, string Bin, string Type )
        : IComparable<IndexKey>
    {
        public int CompareTo( IndexKey other )
        {
            var c = string.CompareOrdinal( Namespace, other.Namespace );
            if ( c != 0 )
                return c;
            return string.CompareOrdinal( Name, other.Name );
        }
    }
}
