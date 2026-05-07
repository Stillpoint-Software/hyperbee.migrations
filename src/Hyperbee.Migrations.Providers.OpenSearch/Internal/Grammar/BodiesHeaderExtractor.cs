using System.Text.RegularExpressions;

namespace Hyperbee.Migrations.Providers.OpenSearch.Internal.Grammar;

/// <summary>
/// Pre-pass extractor for the <c>BODIES { ... }</c> header block at the top
/// of an OpenSearch <c>.statements</c> script (per ADR-0022 + Phase 4
/// follow-up). The header lifts named body references (Form 2/3 in the
/// JSON-array shape) into a script-form-compatible map.
/// </summary>
/// <remarks>
/// <para>
/// Header grammar:
/// <code>
/// BODIES {
///   name1: @path/to/body.json
///   name2: { "settings": { "number_of_shards": 1 } }
///   name3: @another/path.json
/// }
/// </code>
/// </para>
/// <para>
/// Each entry is <c>name : VALUE</c> where VALUE is either a <c>@path</c>
/// string or a brace-balanced JSON literal. Returns the parsed map plus the
/// script with the header (and any leading comments/whitespace) stripped so
/// the splitter can run on the body unchanged.
/// </para>
/// </remarks>
internal static class BodiesHeaderExtractor
{
    private static readonly Regex BodiesKeyword = new(
        @"^\s*(?:(?:--[^\n]*\n)|(?:/\*[\s\S]*?\*/)|\s)*BODIES\s*\{",
        RegexOptions.Compiled );

    public sealed class HeaderResult
    {
        public IReadOnlyDictionary<string, BodiesEntry> Bodies { get; init; } =
            new Dictionary<string, BodiesEntry>( StringComparer.OrdinalIgnoreCase );
        public string RemainingScript { get; init; } = "";
    }

    public abstract record BodiesEntry;
    public sealed record BodiesPathEntry( string Path ) : BodiesEntry;
    public sealed record BodiesInlineEntry( string Json ) : BodiesEntry;

    public static HeaderResult Extract( string script )
    {
        ArgumentNullException.ThrowIfNull( script );

        var match = BodiesKeyword.Match( script );
        if ( !match.Success )
        {
            return new HeaderResult { RemainingScript = script };
        }

        // Position right after the opening brace of BODIES { ... }
        var headerStart = match.Index;
        var bodyStart = match.Index + match.Length - 1; // position of `{`

        var headerEnd = BraceBalancedConsumer.ConsumeBalanced( script, bodyStart, out var headerBlock );
        if ( headerEnd == bodyStart )
        {
            throw new OpenSearchParseException(
                "BODIES header has unbalanced braces. Each `BODIES { ... }` block must close with a matching `}`." );
        }

        // headerBlock = "{ name1: @path1\n name2: {...} }"
        // Strip the outer braces.
        var inner = headerBlock.Substring( 1, headerBlock.Length - 2 ).Trim();
        var bodies = ParseEntries( inner );

        var remaining = script.Substring( 0, headerStart ) + script.Substring( headerEnd );
        return new HeaderResult
        {
            Bodies = bodies,
            RemainingScript = remaining
        };
    }

    private static IReadOnlyDictionary<string, BodiesEntry> ParseEntries( string inner )
    {
        var result = new Dictionary<string, BodiesEntry>( StringComparer.OrdinalIgnoreCase );

        var i = 0;
        while ( i < inner.Length )
        {
            // skip whitespace + line comments + block comments + commas
            while ( i < inner.Length )
            {
                var c = inner[i];
                if ( char.IsWhiteSpace( c ) || c == ',' ) { i++; continue; }
                if ( c == '-' && i + 1 < inner.Length && inner[i + 1] == '-' )
                {
                    while ( i < inner.Length && inner[i] != '\n' ) i++;
                    continue;
                }
                if ( c == '/' && i + 1 < inner.Length && inner[i + 1] == '*' )
                {
                    i += 2;
                    while ( i + 1 < inner.Length && !(inner[i] == '*' && inner[i + 1] == '/') ) i++;
                    if ( i + 1 < inner.Length ) i += 2;
                    continue;
                }
                break;
            }

            if ( i >= inner.Length )
                break;

            // name: identifier
            var nameStart = i;
            while ( i < inner.Length && (char.IsLetterOrDigit( inner[i] ) || inner[i] == '_' || inner[i] == '-' ) )
                i++;
            if ( i == nameStart )
                throw new OpenSearchParseException(
                    $"BODIES header parse error at position {i}: expected an identifier." );
            var name = inner.Substring( nameStart, i - nameStart );

            // skip whitespace then ':' then whitespace
            while ( i < inner.Length && char.IsWhiteSpace( inner[i] ) ) i++;
            if ( i >= inner.Length || inner[i] != ':' )
                throw new OpenSearchParseException(
                    $"BODIES header parse error at position {i}: expected ':' after entry name `{name}`." );
            i++;
            while ( i < inner.Length && char.IsWhiteSpace( inner[i] ) ) i++;

            // value: either @path or { ... }
            if ( i >= inner.Length )
                throw new OpenSearchParseException(
                    $"BODIES header parse error: entry `{name}` has no value." );

            BodiesEntry entry;
            if ( inner[i] == '@' )
            {
                i++; // consume '@'
                var pathStart = i;
                while ( i < inner.Length
                        && !char.IsWhiteSpace( inner[i] )
                        && inner[i] != ','
                        && inner[i] != '\n' )
                {
                    i++;
                }
                var path = inner.Substring( pathStart, i - pathStart );
                if ( path.Length == 0 )
                    throw new OpenSearchParseException(
                        $"BODIES header entry `{name}` has empty `@path`." );
                entry = new BodiesPathEntry( path );
            }
            else if ( inner[i] == '{' )
            {
                var endIdx = BraceBalancedConsumer.ConsumeBalanced( inner, i, out var jsonLiteral );
                if ( endIdx == i )
                    throw new OpenSearchParseException(
                        $"BODIES header entry `{name}` has unbalanced JSON braces." );
                entry = new BodiesInlineEntry( jsonLiteral );
                i = endIdx;
            }
            else
            {
                throw new OpenSearchParseException(
                    $"BODIES header entry `{name}`: expected `@path` or `{{ ... }}` value, got `{inner[i]}`." );
            }

            if ( result.ContainsKey( name ) )
                throw new OpenSearchParseException(
                    $"BODIES header has duplicate entry name `{name}`." );
            result[name] = entry;
        }

        return result;
    }
}
