using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Hyperbee.Migrations.Squash;

namespace Hyperbee.Migrations.Providers.MongoDB.Squash;

/// <summary>
/// Canonicalizes a MongoDB snapshot blob into a deterministic, byte-stable
/// representation that satisfies the C12 determinism gate (per ADR-0019).
/// </summary>
/// <remarks>
/// <para>
/// Input contract (produced by <c>IntrospectionSnapshotStrategy</c> in
/// Task 3.5): a multi-line blob with section headers, where each section's
/// body is the JSON response captured from the corresponding MongoDB
/// command. Section headers are case-insensitive, line-leading, in square
/// brackets. Comments (lines starting with <c>#</c>) and blank lines between
/// sections are permitted and ignored.
/// </para>
/// <para>
/// Recognized sections:
/// <list type="bullet">
///   <item><c>[collections]</c> -- output of <c>listCollections</c> (filterable
///         to include collections, views, time-series).</item>
///   <item><c>[indexes]</c> -- per-collection <c>getIndexes</c> results,
///         keyed by collection name in the top-level object.</item>
///   <item><c>[validators]</c> -- JSON schema validators per collection
///         (the <c>options.validator</c> sub-field hoisted out for clearer
///         diffs; the canonicalizer is path-agnostic so this section is
///         optional and tolerated).</item>
///   <item><c>[views]</c> -- view-type entries from listCollections; pipeline
///         + viewOn target.</item>
/// </list>
/// Unknown section names are pass-through canonicalized (sorted + ephemeral-
/// stripped) so future server features fail gracefully rather than erroring.
/// </para>
/// <para>
/// Canonicalization steps (same shape as OpenSearch Task 2.4):
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
///   <item><c>uuid</c> -- server-generated collection identifier
///         (<c>info.uuid</c>). Changes each time a collection is recreated.</item>
///   <item><c>readOnly</c> -- runtime flag (<c>info.readOnly</c>), not
///         structural state.</item>
///   <item><c>v</c> -- index version (<c>1</c> or <c>2</c>). Server-managed
///         and version-dependent. Stripping makes canonical output stable
///         across MongoDB releases; the server assigns the current default
///         on recreate. Applies to both <c>idIndex.v</c> and per-index <c>v</c>.</item>
///   <item><c>ns</c> -- legacy server-injected namespace string
///         (deprecated in MongoDB 4.4+ but still emitted by some hosted
///         offerings).</item>
///   <item><c>_meta.lastUpdated</c> / similar timestamps -- not stripped
///         globally because operator-authored <c>_meta</c> can carry
///         structural intent; document as a known limitation that
///         operators using <c>_meta</c> with timestamps must include the
///         field in [StructuralOnly] migrations.</item>
/// </list>
/// </para>
/// <para>
/// <b>Cross-provider consistency:</b> follows the same opaque-content +
/// structural-canonical pattern established for OpenSearch (Phase 2 Task 2.4
/// painless spike). For MongoDB the "opaque content" is BSON-encoded values
/// within JSON envelopes -- aggregation pipelines in view definitions,
/// JSON schema validators, partialFilterExpression queries on indexes. All
/// ride through as JSON value content; the canonicalizer never parses BSON
/// semantically.
/// </para>
/// </remarks>
public sealed class MongoDBSnapshotCanonicalizer : ISnapshotCanonicalizer
{
    public string ProviderId => MongoDBTopologySignature.ProviderIdValue;

    // Ephemerals stripped at every nesting level inside any JSON body.
    // Per the docstring above; matched by simple-name regardless of JSON path.
    internal static readonly IReadOnlySet<string> Ephemerals = new HashSet<string>( StringComparer.Ordinal )
    {
        "uuid",
        "readOnly",
        "v",
        "ns"
    };

    // Same writer options as OpenSearch: UnsafeRelaxedJsonEscaping for minimal
    // escaping, indented for diff-friendliness.
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
        sb.Append( "# mongodb-squash v1\n\n" );

        foreach ( var sectionName in sections.Bodies.Keys.OrderBy( k => k, StringComparer.Ordinal ) )
        {
            sb.Append( '[' ).Append( sectionName ).Append( "]\n" );

            var body = sections.Bodies[sectionName];
            try
            {
                using var doc = JsonDocument.Parse( body );
                var canonicalJson = SerializeCanonical( doc.RootElement );
                sb.Append( canonicalJson );
                if ( !canonicalJson.EndsWith( '\n' ) )
                    sb.Append( '\n' );
            }
            catch ( JsonException ex )
            {
                throw new MigrationException(
                    $"MongoDB snapshot section `[{sectionName}]` is not valid JSON: {ex.Message}" );
            }

            sb.Append( '\n' );
        }

        return sb.ToString();
    }

    private static string EmitHeader() => "# mongodb-squash v1\n";

    // ---- canonical JSON serialization --------------------------------------

    internal static string SerializeCanonical( JsonElement element )
    {
        using var stream = new MemoryStream();
        using ( var writer = new Utf8JsonWriter( stream, WriterOptions ) )
        {
            WriteCanonical( writer, element );
        }

        // Cross-platform CRLF -> LF normalization (Phase 2 OpenSearch
        // carry-forward). Utf8JsonWriter with Indented=true uses
        // Environment.NewLine on older targets.
        var raw = Encoding.UTF8.GetString( stream.ToArray() );
        return raw.Contains( '\r' ) ? raw.Replace( "\r\n", "\n" ).Replace( "\r", "\n" ) : raw;
    }

    private static void WriteCanonical( Utf8JsonWriter writer, JsonElement element )
    {
        switch ( element.ValueKind )
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                foreach ( var property in element.EnumerateObject().OrderBy( p => p.Name, StringComparer.Ordinal ) )
                {
                    if ( Ephemerals.Contains( property.Name ) )
                        continue;
                    writer.WritePropertyName( property.Name );
                    WriteCanonical( writer, property.Value );
                }
                writer.WriteEndObject();
                break;

            case JsonValueKind.Array:
                writer.WriteStartArray();
                // Arrays preserve order. Indexes and views are returned in
                // server-defined order but should be sorted by name for
                // byte-stability. The strategy can pre-sort arrays by a
                // stable key (e.g., index `name`) before passing to the
                // canonicalizer; doing it generically here would either
                // require type-specific sort keys or risk reordering
                // arrays that depend on declared order
                // (e.g., aggregation pipeline stages).
                foreach ( var item in element.EnumerateArray() )
                    WriteCanonical( writer, item );
                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                writer.WriteStringValue( element.GetString() );
                break;

            case JsonValueKind.Number:
                // GetRawText preserves the operator's numeric representation
                // exactly. Critical for BSON-flavored JSON where integer
                // sizes are sometimes encoded as $numberLong/$numberInt or
                // as plain numbers.
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
