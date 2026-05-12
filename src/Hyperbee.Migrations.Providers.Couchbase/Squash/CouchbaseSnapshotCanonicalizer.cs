using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Hyperbee.Migrations.Squash;

namespace Hyperbee.Migrations.Providers.Couchbase.Squash;

/// <summary>
/// Canonicalizes a Couchbase snapshot blob into a deterministic, byte-stable
/// representation that satisfies the C12 determinism gate (per ADR-0019).
/// </summary>
/// <remarks>
/// <para>
/// Input contract (produced by <c>HybridStrategy</c> in Task 4.5): a multi-
/// line blob with section headers, where each section's body is the JSON
/// response captured from the corresponding Couchbase source (REST Management
/// API for bucket/scope/collection settings; N1QL <c>system:keyspaces</c> +
/// <c>system:indexes</c> for keyspaces + indexes; FTS REST for search index
/// definitions; Eventing REST for function source). Section headers are
/// case-insensitive, line-leading, in square brackets. Comments (lines
/// starting with <c>#</c>) and blank lines between sections are permitted
/// and ignored.
/// </para>
/// <para>
/// Recognized sections (capture strategy assembles these):
/// <list type="bullet">
///   <item><c>[buckets]</c> -- REST <c>/pools/default/buckets</c> projections:
///         name, bucketType, ramQuotaMB, replicaNumber, evictionPolicy,
///         conflictResolutionType, storageBackend, durabilityMinLevel,
///         flushEnabled, maxTTL, history settings, compression mode.</item>
///   <item><c>[scopes]</c> -- per-bucket scope list with max_ttl + history
///         options.</item>
///   <item><c>[collections]</c> -- per-scope collection list with max_ttl +
///         history options.</item>
///   <item><c>[indexes]</c> -- N1QL <c>system:indexes</c> projection:
///         keyspace_id, scope_id, name, index_key, condition, partition,
///         using (gsi), state (deferred preserved -- see below), with-clause.</item>
///   <item><c>[fts_indexes]</c> -- search index JSON definitions
///         (opaque-content per the cross-provider pattern).</item>
///   <item><c>[eventing_functions]</c> -- function metadata + .js source
///         (opaque-content -- the canonicalizer never parses JS).</item>
///   <item><c>[users]</c>, <c>[groups]</c> -- RBAC entries when in scope.</item>
/// </list>
/// Unknown section names pass through canonicalized so future server features
/// fail gracefully rather than erroring.
/// </para>
/// <para>
/// Canonicalization steps (same shape as OpenSearch Task 2.4 + MongoDB Task 3.4):
/// <list type="number">
///   <item>Parse each section body as JSON.</item>
///   <item>Recursively sort object keys via ordinal string comparison.</item>
///   <item>Strip ephemeral keys at every nesting level.</item>
///   <item>Re-emit with indented JSON, minimal string escaping, LF line
///         endings (cross-platform normalization).</item>
///   <item>Compose the section-headered output document with sections in
///         alphabetical order.</item>
/// </list>
/// </para>
/// <para>
/// <b>Ephemeral catalog</b> (matched by simple key name at every nesting level):
/// <list type="bullet">
///   <item><c>id</c> -- server-assigned index identifier. Index identity is
///         (keyspace + name); the id changes every recreate.</item>
///   <item><c>docCount</c>, <c>dataSize</c>, <c>memUsed</c>, <c>diskUsed</c>,
///         <c>opsPerSec</c>, <c>itemCount</c>, <c>lastUsed</c> -- runtime
///         statistics, not structural state.</item>
///   <item><c>lastRebalanceTimestamp</c>, <c>last_rebalance_timestamp</c>
///         -- cluster operations timestamps.</item>
///   <item><c>nodes</c> -- per-node placement returned by cluster details;
///         the index/bucket carries replica + node count via authorial
///         settings, not server placement. Stripping makes byte output
///         stable across rebalance.</item>
///   <item><c>nodeUUID</c>, <c>uuid</c> -- server-generated identifiers.</item>
///   <item><c>vBucketServerMap</c> -- ephemeral vbucket placement table.</item>
///   <item><c>quotaUsed</c>, <c>quotaPercentUsed</c>, <c>basicStats</c>
///         -- bucket runtime stats.</item>
/// </list>
/// </para>
/// <para>
/// <b>Deferred-build preservation (R-P3 OQ resolution):</b> N1QL indexes
/// can be created in <c>deferred</c> state and built later via
/// <c>BUILD INDEX</c>. The captured <c>state</c> field is <b>preserved</b> when
/// its value is <c>deferred</c> so the apply path can issue the BUILD INDEX
/// after all CREATE INDEX statements. When <c>state</c> is <c>online</c> the
/// value is dropped entirely (it's the default and adds no structural
/// information). Transient values (<c>building</c>, <c>pending</c>) are
/// errors at capture-time -- the strategy must wait until each index has
/// settled before producing the snapshot, so the canonicalizer rejects them
/// loudly.
/// </para>
/// <para>
/// <b>Cross-provider consistency:</b> follows the same opaque-content +
/// structural-canonical pattern established for OpenSearch (Phase 2 Task 2.4
/// painless spike) and reinforced for MongoDB (Phase 3 Task 3.4). For
/// Couchbase the "opaque content" is N1QL function definitions (eventing JS
/// source), FTS index JSON definitions, and N1QL <c>WHERE</c>/<c>WITH</c>
/// clauses on index entries. All ride through as JSON value content; the
/// canonicalizer never parses N1QL, JS, or FTS semantically.
/// </para>
/// </remarks>
public sealed class CouchbaseSnapshotCanonicalizer : ISnapshotCanonicalizer
{
    public string ProviderId => CouchbaseTopologySignature.ProviderIdValue;

    // Ephemerals stripped at every nesting level inside any JSON body.
    // Per the docstring; matched by simple-name regardless of JSON path.
    internal static readonly IReadOnlySet<string> Ephemerals = new HashSet<string>( StringComparer.Ordinal )
    {
        "id",
        "docCount",
        "dataSize",
        "memUsed",
        "diskUsed",
        "opsPerSec",
        "itemCount",
        "lastUsed",
        "lastRebalanceTimestamp",
        "last_rebalance_timestamp",
        "nodes",
        "nodeUUID",
        "uuid",
        "vBucketServerMap",
        "quotaUsed",
        "quotaPercentUsed",
        "basicStats"
    };

    // Index `state` values that signal capture-time transience -- the
    // strategy must wait for indexes to settle before snapshotting.
    private static readonly IReadOnlySet<string> TransientIndexStates = new HashSet<string>( StringComparer.OrdinalIgnoreCase )
    {
        "building",
        "pending",
        "scheduled",
        "deleted"
    };

    private static readonly JsonWriterOptions WriterOptions = new()
    {
        Indented = true,
        Encoder = JavaScriptEncoder.UnsafeRelaxedJsonEscaping
    };

    public string Canonicalize( string snapshot )
    {
        ArgumentNullException.ThrowIfNull( snapshot );

        var sections = ParseSections( snapshot );

        if ( !sections.HasAny )
            return EmitHeader();

        return EmitFromSections( sections );
    }

    public string EmitScript( string canonicalContent ) => Canonicalize( canonicalContent );

    // ---- section parsing ---------------------------------------------------

    internal sealed record Sections( IReadOnlyDictionary<string, string> Bodies )
    {
        public bool HasAny => Bodies.Count > 0;
    }

    internal static Sections ParseSections( string snapshot )
    {
        var bodies = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
        string currentSection = null;
        var buffer = new StringBuilder();

        foreach ( var rawLine in snapshot.Split( '\n' ) )
        {
            var line = rawLine.TrimEnd( '\r' );
            var trimmed = line.TrimStart();

            if ( currentSection == null && (trimmed.Length == 0 || trimmed.StartsWith( '#' )) )
                continue;

            if ( trimmed.StartsWith( '[' ) && trimmed.EndsWith( "]", StringComparison.Ordinal ) )
            {
                FlushSection( currentSection, buffer, bodies );
                currentSection = trimmed.Substring( 1, trimmed.Length - 2 ).Trim().ToLowerInvariant();
                buffer.Clear();
                continue;
            }

            if ( currentSection == null )
                continue;

            buffer.Append( line ).Append( '\n' );
        }

        FlushSection( currentSection, buffer, bodies );
        return new Sections( bodies );
    }

    private static void FlushSection( string section, StringBuilder buffer, Dictionary<string, string> bodies )
    {
        if ( section == null || buffer.Length == 0 )
            return;
        var content = buffer.ToString().Trim();
        if ( content.Length == 0 )
            return;
        bodies[section] = content;
    }

    // ---- emission ----------------------------------------------------------

    private static string EmitFromSections( Sections sections )
    {
        var sb = new StringBuilder();
        sb.Append( "# couchbase-squash v1\n\n" );

        foreach ( var sectionName in sections.Bodies.Keys.OrderBy( k => k, StringComparer.Ordinal ) )
        {
            sb.Append( '[' ).Append( sectionName ).Append( "]\n" );

            var body = sections.Bodies[sectionName];
            try
            {
                using var doc = JsonDocument.Parse( body );
                var canonicalJson = SerializeCanonical( doc.RootElement, sectionName );
                sb.Append( canonicalJson );
                if ( !canonicalJson.EndsWith( '\n' ) )
                    sb.Append( '\n' );
            }
            catch ( JsonException ex )
            {
                throw new MigrationException(
                    $"Couchbase snapshot section `[{sectionName}]` is not valid JSON: {ex.Message}" );
            }

            sb.Append( '\n' );
        }

        return sb.ToString();
    }

    private static string EmitHeader() => "# couchbase-squash v1\n";

    // ---- canonical JSON serialization --------------------------------------

    internal static string SerializeCanonical( JsonElement element, string sectionName = null )
    {
        using var stream = new MemoryStream();
        using ( var writer = new Utf8JsonWriter( stream, WriterOptions ) )
        {
            WriteCanonical( writer, element, sectionName );
        }

        // Cross-platform CRLF -> LF normalization (Phase 2 OpenSearch carry-
        // forward + Phase 3 MongoDB).
        var raw = Encoding.UTF8.GetString( stream.ToArray() );
        return raw.Contains( '\r' ) ? raw.Replace( "\r\n", "\n" ).Replace( "\r", "\n" ) : raw;
    }

    private static void WriteCanonical( Utf8JsonWriter writer, JsonElement element, string sectionName )
    {
        switch ( element.ValueKind )
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach ( var property in element.EnumerateObject().OrderBy( p => p.Name, StringComparer.Ordinal ) )
                {
                    if ( Ephemerals.Contains( property.Name ) )
                        continue;

                    // Deferred-build preservation per R-P3 OQ:
                    //   [indexes] section: state=online dropped, state=deferred
                    //   preserved, transient values (building/pending) error.
                    if ( string.Equals( sectionName, "indexes", StringComparison.OrdinalIgnoreCase )
                         && string.Equals( property.Name, "state", StringComparison.OrdinalIgnoreCase )
                         && property.Value.ValueKind == JsonValueKind.String )
                    {
                        var stateValue = property.Value.GetString();
                        if ( string.Equals( stateValue, "online", StringComparison.OrdinalIgnoreCase ) )
                            continue;
                        if ( TransientIndexStates.Contains( stateValue ?? "" ) )
                            throw new MigrationException(
                                $"Couchbase snapshot section `[indexes]` contains transient index state `{stateValue}` -- the capture strategy must wait for indexes to settle (deferred or online) before snapshotting." );
                    }

                    writer.WritePropertyName( property.Name );
                    WriteCanonical( writer, property.Value, sectionName );
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                foreach ( var item in element.EnumerateArray() )
                    WriteCanonical( writer, item, sectionName );
                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteStringValue( element.GetString() );
                break;

            case JsonValueKind.Number:
                writer.WriteRawValue( element.GetRawText() );
                break;

            case JsonValueKind.True:
                writer.WriteBooleanValue( true );
                break;
            case JsonValueKind.False:
                writer.WriteBooleanValue( false );
                break;
            case JsonValueKind.Null:
                writer.WriteNullValue();
                break;
        }
    }
}
