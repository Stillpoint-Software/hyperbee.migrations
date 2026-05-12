#nullable enable
using System.Text;
using System.Text.Encodings.Web;
using System.Text.Json;
using Hyperbee.Migrations.Squash;

namespace Hyperbee.Migrations.Providers.OpenSearch.Squash;

/// <summary>
/// Canonicalizes an OpenSearch snapshot blob into a deterministic, byte-stable
/// representation that satisfies the C12 determinism gate (per ADR-0019).
/// </summary>
/// <remarks>
/// <para>
/// Input contract (produced by <c>RestStateDiffStrategy</c> in Task 2.5): a
/// multi-line blob with section headers, where each section's body is the
/// JSON response captured from the corresponding OpenSearch REST endpoint.
/// Section headers are case-insensitive, line-leading, in square brackets.
/// Comments (lines starting with <c>#</c>) and blank lines between sections
/// are permitted and ignored.
/// </para>
/// <para>
/// Recognized sections:
/// <list type="bullet">
///   <item><c>[index_template]</c> -- body is the <c>GET /_index_template/*</c> response.</item>
///   <item><c>[component_template]</c> -- body is the <c>GET /_component_template/*</c> response.</item>
///   <item><c>[index_metadata]</c> -- body is a per-index settings+mapping+aliases composite.</item>
///   <item><c>[alias]</c> -- body is the <c>GET /_alias</c> response.</item>
///   <item><c>[ism_policy]</c> -- body is the <c>GET /_plugins/_ism/policies</c> response (or the
///         legacy <c>_opendistro</c> variant; the canonicalizer is path-agnostic).</item>
///   <item><c>[ingest_pipeline]</c> -- body is the <c>GET /_ingest/pipeline</c> response.</item>
/// </list>
/// Unknown section names are pass-through canonicalized (sorted + ephemeral-stripped
/// against the global catalog) so future server features fail gracefully rather
/// than erroring.
/// </para>
/// <para>
/// Canonicalization steps (per Task 2.0 spike conclusion):
/// <list type="number">
///   <item>Parse each section body as JSON.</item>
///   <item>Recursively sort object keys at every nesting level using ordinal
///         string comparison.</item>
///   <item>Strip ephemeral keys at every nesting level. Ephemerals are matched
///         by simple key name (not JSON path) so the same name appearing at
///         multiple depths is stripped consistently.</item>
///   <item>Re-emit with indented JSON (deterministic 2-space indentation, LF
///         line endings, minimal string escaping). The indentation is part of
///         the canonical form; reformatting the canonical output to a
///         different shape would break C12 byte-equality.</item>
///   <item>Compose the section-headered output document.</item>
/// </list>
/// </para>
/// <para>
/// <b>Painless preservation</b> (per Task 2.0 spike): painless script source
/// rides through as opaque JSON string content. The canonicalizer does NOT
/// parse, normalize, or modify painless source code. String value escaping
/// is delegated to <see cref="JavaScriptEncoder.UnsafeRelaxedJsonEscaping"/>
/// which only escapes the JSON-required characters (<c>\\</c>, <c>\"</c>,
/// control chars). Whitespace, comments, and language constructs inside the
/// painless string round-trip byte-for-byte.
/// </para>
/// <para>
/// <b>Cross-provider precedent:</b> opaque-content + structural-canonical
/// split. MongoDB Phase 3 (BSON aggregation pipelines, <c>partialFilterExpression</c>
/// queries) and Couchbase Phase 4 (N1QL function definitions, FTS JSON)
/// will follow the same rule: structure canonicalized, content opaque.
/// </para>
/// </remarks>
public sealed class OpenSearchSnapshotCanonicalizer : ISnapshotCanonicalizer
{
    public string ProviderId => OpenSearchTopologySignature.ProviderIdValue;

    // Ephemerals stripped at every nesting level inside any JSON body.
    //
    // The catalog matches Phase 0 Appendix C plus a small set of additions
    // observed in real OpenSearch responses:
    //   creation_date, uuid, version       -- index/template metadata
    //   provided_name                       -- index settings (canonicalized away because the index NAME is the dictionary key)
    //   policy_version, last_updated_time   -- ISM policy state metadata
    //   seq_no, primary_term                -- ISM policy CAS tokens
    //   _meta (when contains a server-injected timestamp)  -- not stripped by default; provider can extend
    //
    // Stripped by simple-name match regardless of JSON path. The cost is that
    // an operator's body containing a field of the same name is also stripped;
    // documented as a v3.0 limitation. Path-specific overrides can be added
    // post-v3.0 if operator feedback warrants.
    internal static readonly IReadOnlySet<string> Ephemerals = new HashSet<string>( StringComparer.Ordinal )
    {
        "creation_date",
        "uuid",
        "version",
        "provided_name",
        "policy_version",
        "last_updated_time",
        "seq_no",
        "primary_term"
    };

    // JSON writer encoder: UnsafeRelaxedJsonEscaping only escapes the
    // minimal JSON-required characters (\\, \", control chars). Without this,
    // the default encoder would escape characters like '+' or '/' which would
    // make canonical output differ from operator-authored body files even
    // when they're semantically identical. Painless source containing
    // arithmetic operators or paths benefits directly.
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
        {
            // No section headers present. Either the input is empty or
            // already in canonical statement-form. For v3.0 the canonical
            // form IS section-headered (no script-statement form yet --
            // ADR-0022 inline-body grammar is a follow-up). Return an
            // empty canonical header so callers see the snapshot has no
            // structural content.
            return EmitHeader();
        }

        return EmitFromSections( sections );
    }

    public string EmitScript( string canonicalContent ) => Canonicalize( canonicalContent );

    // ---- section parsing ---------------------------------------------------
    //
    // Mirrors AerospikeSnapshotCanonicalizer's ParseSections shape so cross-
    // provider consistency is obvious. The Aerospike canonicalizer (Phase 1
    // Task 1.4) established this section-parsing pattern; OpenSearch reuses
    // it with the OpenSearch-specific section name set.

    internal sealed record Sections(
        IReadOnlyDictionary<string, string> Bodies )
    {
        public bool HasAny => Bodies.Count > 0;
    }

    internal static Sections ParseSections( string snapshot )
    {
        var bodies = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
        string? currentSection = null;
        var buffer = new StringBuilder();

        foreach ( var rawLine in snapshot.Split( '\n' ) )
        {
            var line = rawLine.TrimEnd( '\r' );
            var trimmed = line.TrimStart();

            // Comment lines (starting with '#') and blank lines between
            // sections are ignored. Comments INSIDE a section's JSON body
            // would be invalid JSON, so the JSON parser will reject them
            // downstream -- this filter only applies before the first '['
            // and between sections.
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

    private static void FlushSection( string? section, StringBuilder buffer, Dictionary<string, string> bodies )
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
        sb.Append( "# opensearch-squash v1\n\n" );

        // Emit sections in canonical order (alphabetical) so different
        // capture orderings produce identical output.
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
                    $"OpenSearch snapshot section `[{sectionName}]` is not valid JSON: {ex.Message}" );
            }

            sb.Append( '\n' );
        }

        return sb.ToString();
    }

    private static string EmitHeader() => "# opensearch-squash v1\n";

    // ---- canonical JSON serialization --------------------------------------

    /// <summary>
    /// Emits the given JSON element in canonical form: sorted object keys
    /// (ordinal), stripped ephemerals at every nesting level, indented for
    /// readability, minimal string escaping.
    /// </summary>
    internal static string SerializeCanonical( JsonElement element )
    {
        using var stream = new MemoryStream();
        using ( var writer = new Utf8JsonWriter( stream, WriterOptions ) )
        {
            WriteCanonical( writer, element );
        }

        // Cross-platform byte-stability: Utf8JsonWriter with Indented=true
        // uses Environment.NewLine on older targets (CRLF on Windows). For
        // the C12 determinism gate to hold across developer machines and
        // CI runners with different line-ending defaults, we normalize to
        // LF here. The replace is cheap (no allocation if no CRLF present).
        var raw = Encoding.UTF8.GetString( stream.ToArray() );
        return raw.Contains( '\r' ) ? raw.Replace( "\r\n", "\n" ).Replace( "\r", "\n" ) : raw;
    }

    private static void WriteCanonical( Utf8JsonWriter writer, JsonElement element )
    {
        switch ( element.ValueKind )
        {
            case JsonValueKind.Object:
                writer.WriteStartObject();
                // Stable ordinal sort so the same logical content always
                // produces the same byte sequence regardless of server
                // serialization order.
                foreach ( var property in element.EnumerateObject()
                              .OrderBy( p => p.Name, StringComparer.Ordinal ) )
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
                // Arrays preserve order. Server responses may shuffle objects
                // within an array (rare for the OpenSearch endpoints we
                // consume), so future evolution may sort certain arrays by
                // a stable key. v3.0 preserves array order; per-endpoint
                // overrides can be added later if a real determinism
                // failure surfaces.
                foreach ( var item in element.EnumerateArray() )
                    WriteCanonical( writer, item );
                writer.WriteEndArray();
                break;

            case JsonValueKind.String:
                // Painless source rides through as opaque string content
                // per the Task 2.0 spike conclusion. The writer encodes
                // standard JSON string escapes; painless syntax (//, /* */,
                // whitespace, quotes-in-strings) round-trips byte-for-byte.
                writer.WriteStringValue( element.GetString() );
                break;

            case JsonValueKind.Number:
                // GetRawText preserves the operator's numeric representation
                // exactly. We do not normalize 1.0 -> 1 or 1e3 -> 1000 because
                // OpenSearch's mapping configs sometimes encode integer-typed
                // fields as floats (and round-tripping through double loses
                // precision in edge cases). Server-side responses are
                // already in a canonical form for that endpoint.
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

            default:
                // Undefined should not appear inside parsed JSON.
                break;
        }
    }
}
