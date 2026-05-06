using System.Text;

namespace Hyperbee.Migrations.Spike.PostgresClassifier;

// Split a Postgres SQL script (e.g., pg_dump --schema-only output) into individual
// top-level statements. The splitter must respect:
//   - single-quoted strings ('it''s' = escape via doubled quote)
//   - double-quoted identifiers ("public"."order")
//   - line comments (-- ... end of line)
//   - block comments (/* ... */; Postgres allows nesting)
//   - dollar-quoted strings ($$...$$ and $tag$...$tag$ where tag is an optional identifier)
//
// Output is a list of trimmed statement strings (semicolon stripped).

public static class PostgresStatementSplitter
{
    public static IReadOnlyList<string> Split( string script )
    {
        ArgumentNullException.ThrowIfNull( script );

        var results = new List<string>();
        var sb = new StringBuilder();

        var i = 0;
        var n = script.Length;
        var blockDepth = 0;

        while ( i < n )
        {
            var c = script[i];

            // Block comment (with nesting per Postgres rules)
            if ( c == '/' && i + 1 < n && script[i + 1] == '*' )
            {
                sb.Append( '/' ).Append( '*' );
                i += 2;
                blockDepth = 1;
                while ( i < n && blockDepth > 0 )
                {
                    if ( script[i] == '/' && i + 1 < n && script[i + 1] == '*' )
                    {
                        sb.Append( '/' ).Append( '*' );
                        i += 2;
                        blockDepth++;
                    }
                    else if ( script[i] == '*' && i + 1 < n && script[i + 1] == '/' )
                    {
                        sb.Append( '*' ).Append( '/' );
                        i += 2;
                        blockDepth--;
                    }
                    else
                    {
                        sb.Append( script[i++] );
                    }
                }
                continue;
            }

            // Line comment
            if ( c == '-' && i + 1 < n && script[i + 1] == '-' )
            {
                while ( i < n && script[i] != '\n' )
                    sb.Append( script[i++] );
                continue;
            }

            // Single-quoted string
            if ( c == '\'' )
            {
                sb.Append( '\'' );
                i++;
                while ( i < n )
                {
                    if ( script[i] == '\'' )
                    {
                        // doubled '' is an escape; otherwise terminator
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

            // Double-quoted identifier
            if ( c == '"' )
            {
                sb.Append( '"' );
                i++;
                while ( i < n )
                {
                    if ( script[i] == '"' )
                    {
                        if ( i + 1 < n && script[i + 1] == '"' )
                        {
                            sb.Append( '"' ).Append( '"' );
                            i += 2;
                            continue;
                        }
                        sb.Append( '"' );
                        i++;
                        break;
                    }
                    sb.Append( script[i++] );
                }
                continue;
            }

            // Dollar-quoted string: $$...$$  or  $tag$...$tag$
            if ( c == '$' && TryReadDollarTag( script, i, out var tag, out var tagLen ) )
            {
                sb.Append( script, i, tagLen );
                i += tagLen;
                while ( i < n )
                {
                    if ( script[i] == '$' && MatchesDollarTag( script, i, tag ) )
                    {
                        sb.Append( script, i, tag.Length );
                        i += tag.Length;
                        break;
                    }
                    sb.Append( script[i++] );
                }
                continue;
            }

            // Statement terminator at top level
            if ( c == ';' )
            {
                var statement = sb.ToString().Trim();
                if ( statement.Length > 0 )
                    results.Add( statement );
                sb.Clear();
                i++;
                continue;
            }

            sb.Append( c );
            i++;
        }

        var tail = sb.ToString().Trim();
        if ( tail.Length > 0 )
            results.Add( tail );

        return results;
    }

    // Try to read a dollar-quote opening tag at position `i`. A tag is:
    //   $$            (empty tag)
    //   $ident$       (identifier; first char letter/underscore, rest letter/digit/underscore)
    //
    // Returns the tag string (e.g. "$$" or "$body$") and its length.
    // Returns false if the `$` here is not the start of a dollar quote (e.g. `$1` placeholder).
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

        // first char of identifier
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
}
