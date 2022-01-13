using System;
using Couchbase.Core.Exceptions;

namespace Hyperbee.Migrations.Couchbase.Parsers;

public class KeySpaceParserOptions
{
    public bool Partial { get; set; }
    public bool PreserveQuotes { get; set; }
    public bool Strict { get; set; }
}

public class KeyspaceParser
{
    private enum Scanner
    {
        Start,
        Escaped,
        Unescaped,
        Trailing,
        Final
    }

    public static KeyspaceRef GetKeyspaceRef( ReadOnlySpan<char> expr )
    {
        expr = expr.Trim();

        var parser = new KeyspaceParser();
        var result = parser.ParseExpression( expr, out var count );

        if ( count != expr.Length )
            throw new InvalidArgumentException( "The keyspace expression contains invalid extra characters." );

        return result;
    }

    public KeyspaceRef ParseExpression( ReadOnlySpan<char> expr, KeySpaceParserOptions options = default )
    {
        return ParseExpression( expr, out _, options );
    }

    public KeyspaceRef ParseExpression( ReadOnlySpan<char> expr, out int count, KeySpaceParserOptions options = default )
    {
        // parses a keyspace from the *start* of a span. leading keywords must have been removed.

        // keyspace-ref ::= keyspace-path | keyspace-partial
        //  keyspace-path ::= [ namespace ':' ] bucket [ '.' scope '.' collection ]
        //  keyspace-partial ::= collection
        //
        // identifier ::= unescaped-identifier | escaped-identifier
        //  unescaped-identifier ::= [a-zA-Z_] ( [0-9a-zA-Z_$] )*
        //  escaped-identifier ::= ` characters `

        static bool IsValidIdentifierChar( char c, bool first )
        {
            // [a-zA-Z_] ( [0-9a-zA-Z_$] )*
            return first
                ? char.IsLetter( c )
                : char.IsLetterOrDigit( c ) || c == '_' || c == '$';
        }

        static string Capture( ReadOnlySpan<char> buffer, int start, int stop )
        {
            var length = stop - start;
            return length <= 0 ? null : buffer.Slice( start, length ).Trim().ToString();
        }

        // scan keyspace
        count = 0;
        options ??= new KeySpaceParserOptions();

        const int keylimit = 4;
        var keyspace = new string[keylimit];
        var k = 0;
        var ns = false;

        var scanner = Scanner.Start;

        var i = 0;
        var n = expr.Length;
        var captureStart = 0;

        do
        {
            if ( scanner == Scanner.Final )
                break;

            var c = expr[i++];

            switch ( scanner )
            {
                case Scanner.Start:
                    switch ( c )
                    {
                        case ' ':
                        case '\t':
                            break;
                        case '`':
                            captureStart = i - (options.PreserveQuotes ? 1 : 0); // adjust for backtick capture or discard
                            scanner = Scanner.Escaped;
                            break;
                        default:
                            if ( !IsValidIdentifierChar( c, true ) )
                                throw new NotSupportedException( $"Invalid identifier character `{c}`." );

                            captureStart = i - 1;
                            scanner = Scanner.Unescaped;
                            break;
                    }
                    break;

                case Scanner.Escaped:
                    if ( c != '`' )
                        continue;

                    keyspace[k++] = Capture( expr, captureStart, i - (options.PreserveQuotes ? 0: 1) ); // adjust for backtick capture or discard
                    count = i;
                    scanner = Scanner.Trailing;
                    break;

                case Scanner.Unescaped:
                    // [a-zA-Z_] ( [0-9a-zA-Z_$] )*

                    if ( IsValidIdentifierChar( c, false ) )
                        continue;

                    i--; // backup to not capture the '.'
                    keyspace[k++] = Capture( expr, captureStart, i );
                    count = i;
                    scanner = Scanner.Trailing;
                    break;

                case Scanner.Trailing:
                    switch ( c )
                    {
                        case ' ':
                        case '\t':
                            break;
                        case ':':
                            if ( k != 1 )
                                throw new NotSupportedException( "Invalid namespace delimiter." );
                            ns = true;
                            scanner = Scanner.Start;
                            break;
                        case '.':
                            if ( k == keylimit )
                                throw new NotSupportedException( "Invalid keyspace count." );
                            scanner = Scanner.Start;
                            break;
                        default:
                            scanner = Scanner.Final;
                            break;
                    }
                    break;

                default:
                    throw new ArgumentOutOfRangeException();
            }

        } while ( i < n );

        // handle the trailing bits

        if ( scanner == Scanner.Escaped )
            throw new InvalidOperationException( "Escaped identifier is missing its closing delimiter." );

        if ( captureStart < i && scanner == Scanner.Unescaped )
        {
            keyspace[k++] = Capture( expr, captureStart, i );
            count = i;
        }

        // return record

        return k switch
        {
            1 when ns => throw new InvalidOperationException( "A namespace was specified without any related keyspace values." ),
            1 when options.Partial => new KeyspaceRef( default, default, default, keyspace[0] ),            // collection
            2 when options.Partial && ns => new KeyspaceRef( keyspace[0], default, default, keyspace[1] ),  // namespace:collection
            2 when !options.Strict && !ns => new KeyspaceRef( default, keyspace[0], default, keyspace[1] ), // [!strict] bucket.collection
            _ when !ns => new KeyspaceRef( default, keyspace[0], keyspace[1], keyspace[2] ),                // bucket.scope.collection
            _ => new KeyspaceRef( keyspace[0], keyspace[1], keyspace[2], keyspace[3] )                      // namespace:bucket.scope.collection
        };
    }
}