using System.Text;
using System.Text.RegularExpressions;

namespace Hyperbee.Migrations.Providers.OpenSearch.Internal.Grammar;

/// <summary>
/// Pre-pass extractor for inline <c>WITH BODY { ... }</c> bodies in the
/// script-form input (per ADR-0022 + Phase 4 follow-up). Each inline body
/// is lifted into a synthetic BODIES entry and replaced with
/// <c>WITH BODY $synthetic_&lt;N&gt;</c> so the existing Parlot grammar can
/// parse the statement unchanged. The runner resolves the synthetic name
/// against the merged bodies dictionary at dispatch time.
/// </summary>
internal static class InlineBodyExtractor
{
    private static readonly Regex WithBodyOpenBrace = new(
        @"(?i:WITH\s+BODY)\s*\{",
        RegexOptions.Compiled );

    private const string SyntheticPrefix = "synthetic_inline_";

    public sealed class ExtractionResult
    {
        public string RewrittenScript { get; init; } = "";
        public IReadOnlyDictionary<string, BodiesHeaderExtractor.BodiesEntry> SyntheticBodies { get; init; } =
            new Dictionary<string, BodiesHeaderExtractor.BodiesEntry>( StringComparer.OrdinalIgnoreCase );
    }

    public static ExtractionResult Extract( string script )
    {
        ArgumentNullException.ThrowIfNull( script );

        // Skip extraction inside string literals and comments. We don't expect
        // inline `WITH BODY {` in those contexts, but the regex would still
        // match. The conservative approach: scan each match position and check
        // whether it's inside a string literal first.

        var matches = WithBodyOpenBrace.Matches( script );
        if ( matches.Count == 0 )
        {
            return new ExtractionResult { RewrittenScript = script };
        }

        var rewritten = new StringBuilder( script.Length );
        var synthetic = new Dictionary<string, BodiesHeaderExtractor.BodiesEntry>( StringComparer.OrdinalIgnoreCase );
        var lastEmitted = 0;
        var counter = 0;

        foreach ( Match match in matches )
        {
            // Position of `{` relative to script
            var braceIndex = match.Index + match.Length - 1;

            // Cheap context filter: if the brace position lies inside a quoted
            // string literal or a comment, skip the match. Walk from start.
            if ( IsInsideStringOrComment( script, braceIndex ) )
                continue;

            var endIdx = BraceBalancedConsumer.ConsumeBalanced( script, braceIndex, out var jsonLiteral );
            if ( endIdx == braceIndex )
                throw new OpenSearchParseException(
                    $"inline `WITH BODY {{ ... }}` at offset {braceIndex} has unbalanced JSON braces." );

            // Emit pre-match content unchanged
            rewritten.Append( script, lastEmitted, match.Index - lastEmitted );

            // Replace `WITH BODY {...}` with `WITH BODY $synthetic_N`
            counter++;
            var name = $"{SyntheticPrefix}{counter}";
            rewritten.Append( "WITH BODY $" ).Append( name );

            synthetic[name] = new BodiesHeaderExtractor.BodiesInlineEntry( jsonLiteral );

            lastEmitted = endIdx;
        }

        if ( lastEmitted < script.Length )
            rewritten.Append( script, lastEmitted, script.Length - lastEmitted );

        return new ExtractionResult
        {
            RewrittenScript = rewritten.ToString(),
            SyntheticBodies = synthetic
        };
    }

    /// <summary>
    /// Returns true if <paramref name="position"/> lies inside a single-quoted
    /// string, double-quoted string, line comment (<c>--</c> or <c>//</c>),
    /// or block comment (<c>/* */</c>) starting at the beginning of
    /// <paramref name="text"/>.
    /// </summary>
    private static bool IsInsideStringOrComment( string text, int position )
    {
        var i = 0;
        while ( i < position )
        {
            var c = text[i];

            // /* block comment */
            if ( c == '/' && i + 1 < text.Length && text[i + 1] == '*' )
            {
                i += 2;
                while ( i + 1 < text.Length && !(text[i] == '*' && text[i + 1] == '/') )
                {
                    if ( i >= position ) return true;
                    i++;
                }
                if ( i + 1 < text.Length ) i += 2;
                continue;
            }

            // -- line comment
            if ( c == '-' && i + 1 < text.Length && text[i + 1] == '-' )
            {
                while ( i < text.Length && text[i] != '\n' )
                {
                    if ( i >= position ) return true;
                    i++;
                }
                continue;
            }

            // // line comment
            if ( c == '/' && i + 1 < text.Length && text[i + 1] == '/' )
            {
                while ( i < text.Length && text[i] != '\n' )
                {
                    if ( i >= position ) return true;
                    i++;
                }
                continue;
            }

            // single-quoted string
            if ( c == '\'' )
            {
                i++;
                while ( i < text.Length )
                {
                    if ( text[i] == '\'' )
                    {
                        if ( i + 1 < text.Length && text[i + 1] == '\'' ) { i += 2; continue; }
                        i++; break;
                    }
                    if ( i >= position ) return true;
                    i++;
                }
                continue;
            }

            // double-quoted string
            if ( c == '"' )
            {
                i++;
                while ( i < text.Length )
                {
                    if ( text[i] == '\\' && i + 1 < text.Length ) { i += 2; continue; }
                    if ( text[i] == '"' ) { i++; break; }
                    if ( i >= position ) return true;
                    i++;
                }
                continue;
            }

            i++;
        }
        return false;
    }
}
