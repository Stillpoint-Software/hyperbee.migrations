namespace Hyperbee.Migrations.Providers.OpenSearch.Internal.Grammar;

/// <summary>
/// Consumes a brace-balanced JSON literal starting at a given index, respecting
/// JSON string escaping (a <c>}</c> inside a quoted string does not close the
/// literal). Used by the script-form parser to lift inline
/// <c>WITH BODY { ... }</c> bodies and the <c>BODIES { name: { ... } }</c>
/// header values per ADR-0022.
/// </summary>
internal static class BraceBalancedConsumer
{
    /// <summary>
    /// Consume a JSON object starting at <paramref name="start"/> (which must
    /// point at <c>{</c>). Returns the index just past the matching <c>}</c>.
    /// </summary>
    /// <returns>The exclusive end index. Equal to <paramref name="start"/> on failure.</returns>
    public static int ConsumeBalanced( string text, int start, out string capturedSubstring )
    {
        capturedSubstring = "";
        if ( text == null || start >= text.Length || text[start] != '{' )
            return start;

        var i = start;
        var depth = 0;

        while ( i < text.Length )
        {
            var c = text[i];

            if ( c == '"' )
            {
                // skip string literal with backslash escape
                i++;
                while ( i < text.Length )
                {
                    if ( text[i] == '\\' && i + 1 < text.Length )
                    {
                        i += 2;
                        continue;
                    }
                    if ( text[i] == '"' )
                    {
                        i++;
                        break;
                    }
                    i++;
                }
                continue;
            }

            if ( c == '{' )
            {
                depth++;
                i++;
                continue;
            }

            if ( c == '}' )
            {
                depth--;
                i++;
                if ( depth == 0 )
                {
                    capturedSubstring = text.Substring( start, i - start );
                    return i;
                }
                continue;
            }

            i++;
        }

        // unbalanced — caller treats as parse failure
        return start;
    }
}
