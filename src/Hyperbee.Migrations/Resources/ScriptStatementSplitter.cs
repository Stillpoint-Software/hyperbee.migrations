using System.Text;

namespace Hyperbee.Migrations.Resources;

/// <summary>
/// Splits a script-form resource into individual top-level statements at
/// semicolon boundaries while respecting common SQL/N1QL/AQL/MongoDB-shell
/// lexical features: single-quoted strings, double-quoted strings, backtick
/// identifiers, <c>--</c> line comments, <c>//</c> line comments, and
/// <c>/* */</c> block comments (with nesting).
/// </summary>
/// <remarks>
/// <para>
/// Per ADR-0022 grammar decision. Three lexical features the universal
/// splitter does <b>NOT</b> handle (provider-specific extensions opt in
/// where needed):
/// </para>
/// <list type="bullet">
///   <item>Postgres dollar-quoted strings (<c>$$...$$</c>, <c>$tag$...$tag$</c>) —
///         see <c>PostgresStatementSplitter</c> in spikes.</item>
///   <item>OpenSearch inline brace-balanced JSON bodies
///         (<c>WITH BODY {...}</c>) — must consume <c>{...}</c> as opaque.</item>
///   <item>BODIES header blocks (<c>BODIES { name: ... }</c>) — should be
///         pre-extracted before passing to the splitter, or the body block
///         consumed via brace-balanced parsing.</item>
/// </list>
/// <para>
/// Output statements are trimmed and have their terminating semicolon removed.
/// Empty segments (whitespace-only or comment-only) are dropped.
/// </para>
/// </remarks>
public static class ScriptStatementSplitter
{
    public static IReadOnlyList<string> Split( string script )
    {
        ArgumentNullException.ThrowIfNull( script );

        var results = new List<string>();
        var sb = new StringBuilder();

        var i = 0;
        var n = script.Length;

        while ( i < n )
        {
            var c = script[i];

            // /* block comment */ (with nesting). Stripped — replaced with a
            // single space so adjacent tokens stay separated for the parser.
            if ( c == '/' && i + 1 < n && script[i + 1] == '*' )
            {
                i += 2;
                var depth = 1;
                while ( i < n && depth > 0 )
                {
                    if ( script[i] == '/' && i + 1 < n && script[i + 1] == '*' )
                    {
                        i += 2;
                        depth++;
                    }
                    else if ( script[i] == '*' && i + 1 < n && script[i + 1] == '/' )
                    {
                        i += 2;
                        depth--;
                    }
                    else
                    {
                        i++;
                    }
                }
                sb.Append( ' ' );
                continue;
            }

            // -- line comment. Stripped — preserve the trailing newline so the
            // parser still sees a token separator on the next line.
            if ( c == '-' && i + 1 < n && script[i + 1] == '-' )
            {
                while ( i < n && script[i] != '\n' )
                    i++;
                continue;
            }

            // // line comment. Same handling as `--`.
            if ( c == '/' && i + 1 < n && script[i + 1] == '/' )
            {
                while ( i < n && script[i] != '\n' )
                    i++;
                continue;
            }

            // single-quoted string with '' escape
            if ( c == '\'' )
            {
                sb.Append( '\'' );
                i++;
                while ( i < n )
                {
                    if ( script[i] == '\'' )
                    {
                        if ( i + 1 < n && script[i + 1] == '\'' )
                        {
                            sb.Append( '\'' ).Append( '\'' );
                            i += 2;
                            continue;
                        }
                        sb.Append( '\'' );
                        i++;
                        break;
                    }
                    sb.Append( script[i++] );
                }
                continue;
            }

            // double-quoted string with backslash escape (Mongo-shell / N1QL)
            if ( c == '"' )
            {
                sb.Append( '"' );
                i++;
                while ( i < n )
                {
                    if ( script[i] == '\\' && i + 1 < n )
                    {
                        sb.Append( script[i++] );
                        sb.Append( script[i++] );
                        continue;
                    }
                    if ( script[i] == '"' )
                    {
                        sb.Append( '"' );
                        i++;
                        break;
                    }
                    sb.Append( script[i++] );
                }
                continue;
            }

            // backtick identifier (N1QL, AQL)
            if ( c == '`' )
            {
                sb.Append( '`' );
                i++;
                while ( i < n )
                {
                    if ( script[i] == '`' )
                    {
                        sb.Append( '`' );
                        i++;
                        break;
                    }
                    sb.Append( script[i++] );
                }
                continue;
            }

            // top-level statement terminator
            if ( c == ';' )
            {
                AppendIfNotBlank( results, sb );
                sb.Clear();
                i++;
                continue;
            }

            sb.Append( c );
            i++;
        }

        AppendIfNotBlank( results, sb );
        return results;
    }

    private static void AppendIfNotBlank( List<string> results, StringBuilder sb )
    {
        var text = sb.ToString().Trim();
        if ( text.Length == 0 )
            return;
        // drop comment-only segments — strip leading comments and check for any
        // real token left over.
        if ( IsAllCommentsAndWhitespace( text ) )
            return;
        results.Add( text );
    }

    private static bool IsAllCommentsAndWhitespace( string text )
    {
        var i = 0;
        var n = text.Length;
        while ( i < n )
        {
            while ( i < n && char.IsWhiteSpace( text[i] ) )
                i++;
            if ( i >= n ) break;

            if ( i + 1 < n && text[i] == '-' && text[i + 1] == '-' )
            {
                while ( i < n && text[i] != '\n' ) i++;
                continue;
            }
            if ( i + 1 < n && text[i] == '/' && text[i + 1] == '/' )
            {
                while ( i < n && text[i] != '\n' ) i++;
                continue;
            }
            if ( i + 1 < n && text[i] == '/' && text[i + 1] == '*' )
            {
                i += 2;
                var depth = 1;
                while ( i < n && depth > 0 )
                {
                    if ( i + 1 < n && text[i] == '/' && text[i + 1] == '*' ) { i += 2; depth++; }
                    else if ( i + 1 < n && text[i] == '*' && text[i + 1] == '/' ) { i += 2; depth--; }
                    else i++;
                }
                continue;
            }
            return false; // a real token
        }
        return true;
    }
}
