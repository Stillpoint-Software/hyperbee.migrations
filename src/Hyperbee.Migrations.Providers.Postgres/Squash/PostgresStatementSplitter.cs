using System.Text;

namespace Hyperbee.Migrations.Providers.Postgres.Squash;

// Postgres-specific script splitter that extends the universal
// `Hyperbee.Migrations.Resources.ScriptStatementSplitter` with two surfaces
// the universal one cannot handle:
//
//   1. Dollar-quoted strings ($$...$$ and $tag$...$tag$). Function bodies,
//      DO blocks, and procedure bodies use this syntax. Per spike finding F6
//      (ADR-0022 amendment A1), the body content is opaque — `--` and `/* */`
//      do NOT terminate or close-out the dollar-quote.
//   2. psql `\restrict` / `\unrestrict` directives that pg_dump 16+ wraps
//      around the dump body (per spike finding F1). These are stripped before
//      splitting so the surrounding statements aren't bundled with them.
//
// Handles the universal splitter surfaces too (single/double quoted strings,
// backtick identifiers, `--` line comments, `/* */` nested block comments).

public static class PostgresStatementSplitter
{
    public static IReadOnlyList<string> Split( string script )
    {
        ArgumentNullException.ThrowIfNull( script );

        // Pre-pass: strip pg_dump 16+ psql `\restrict` / `\unrestrict` directives.
        // These are line-oriented (no `;`) and would otherwise bundle into the
        // surrounding statement, causing the splitter to fail classification.
        var preprocessed = StripPsqlDirectives( script );

        var results = new List<string>();
        var sb = new StringBuilder();

        var i = 0;
        var n = preprocessed.Length;

        while ( i < n )
        {
            var c = preprocessed[i];

            // /* block comment */ — nested. Stripped to a single space so
            // adjacent tokens stay separated for downstream parsing.
            if ( c == '/' && i + 1 < n && preprocessed[i + 1] == '*' )
            {
                i += 2;
                var depth = 1;
                while ( i < n && depth > 0 )
                {
                    if ( preprocessed[i] == '/' && i + 1 < n && preprocessed[i + 1] == '*' )
                    {
                        i += 2;
                        depth++;
                    }
                    else if ( preprocessed[i] == '*' && i + 1 < n && preprocessed[i + 1] == '/' )
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

            // -- line comment (stripped; preserve newline as token break)
            if ( c == '-' && i + 1 < n && preprocessed[i + 1] == '-' )
            {
                while ( i < n && preprocessed[i] != '\n' )
                    i++;
                continue;
            }

            // Single-quoted string with '' escape
            if ( c == '\'' )
            {
                sb.Append( '\'' );
                i++;
                while ( i < n )
                {
                    if ( preprocessed[i] == '\'' )
                    {
                        if ( i + 1 < n && preprocessed[i + 1] == '\'' )
                        {
                            sb.Append( '\'' ).Append( '\'' );
                            i += 2;
                            continue;
                        }
                        sb.Append( '\'' );
                        i++;
                        break;
                    }
                    sb.Append( preprocessed[i++] );
                }
                continue;
            }

            // Double-quoted identifier with "" escape
            if ( c == '"' )
            {
                sb.Append( '"' );
                i++;
                while ( i < n )
                {
                    if ( preprocessed[i] == '"' )
                    {
                        if ( i + 1 < n && preprocessed[i + 1] == '"' )
                        {
                            sb.Append( '"' ).Append( '"' );
                            i += 2;
                            continue;
                        }
                        sb.Append( '"' );
                        i++;
                        break;
                    }
                    sb.Append( preprocessed[i++] );
                }
                continue;
            }

            // Dollar-quoted string: $$...$$  or  $tag$...$tag$
            // Per ADR-0022 A1 the body is opaque — comments and quotes inside
            // it do NOT have their normal lexer meaning; only the matching
            // close tag terminates.
            if ( c == '$' && TryReadDollarTag( preprocessed, i, out var tag, out var tagLen ) )
            {
                sb.Append( preprocessed, i, tagLen );
                i += tagLen;
                while ( i < n )
                {
                    if ( preprocessed[i] == '$' && MatchesDollarTag( preprocessed, i, tag ) )
                    {
                        sb.Append( preprocessed, i, tag.Length );
                        i += tag.Length;
                        break;
                    }
                    sb.Append( preprocessed[i++] );
                }
                continue;
            }

            // Top-level statement terminator
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

    private static string StripPsqlDirectives( string script )
    {
        // Line-oriented: a line that, after leading whitespace, starts with `\`
        // is a psql client directive. Strip the entire line (including its
        // newline) so the surrounding statements aren't bundled with it.
        if ( !script.Contains( '\\' ) )
            return script;

        var sb = new StringBuilder( script.Length );
        var i = 0;
        var n = script.Length;
        while ( i < n )
        {
            // skip leading whitespace on the current line
            var lineStart = i;
            while ( i < n && (script[i] == ' ' || script[i] == '\t') )
                i++;

            if ( i < n && script[i] == '\\' )
            {
                // psql directive — consume to end-of-line (including newline)
                while ( i < n && script[i] != '\n' )
                    i++;
                if ( i < n ) i++;
                continue;
            }

            // not a directive — emit from lineStart to end-of-line + newline
            while ( i < n && script[i] != '\n' )
                i++;
            if ( i < n ) i++;
            sb.Append( script, lineStart, i - lineStart );
        }
        return sb.ToString();
    }

    private static bool TryReadDollarTag( string s, int i, out string tag, out int len )
    {
        tag = "";
        len = 0;
        if ( i >= s.Length || s[i] != '$' )
            return false;

        // empty tag: $$
        if ( i + 1 < s.Length && s[i + 1] == '$' )
        {
            tag = "$$";
            len = 2;
            return true;
        }

        // $identifier$
        var j = i + 1;
        if ( j >= s.Length )
            return false;

        if ( !(char.IsLetter( s[j] ) || s[j] == '_') )
            return false;
        j++;
        while ( j < s.Length && (char.IsLetterOrDigit( s[j] ) || s[j] == '_') )
            j++;

        if ( j >= s.Length || s[j] != '$' )
            return false;

        len = (j - i) + 1;
        tag = s.Substring( i, len );
        return true;
    }

    private static bool MatchesDollarTag( string s, int i, string tag )
    {
        if ( i + tag.Length > s.Length )
            return false;
        for ( var k = 0; k < tag.Length; k++ )
        {
            if ( s[i + k] != tag[k] )
                return false;
        }
        return true;
    }

    private static void AppendIfNotBlank( List<string> results, StringBuilder sb )
    {
        var text = sb.ToString().Trim();
        if ( text.Length == 0 )
            return;
        results.Add( text );
    }
}
