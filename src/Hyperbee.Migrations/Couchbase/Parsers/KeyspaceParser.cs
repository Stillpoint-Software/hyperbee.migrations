using System;
using System.Text;

namespace Hyperbee.Migrations.Couchbase.Parsers;

public record KeyspaceRef( string Namespace, string BucketName, string ScopeName, string CollectionName )
{
    public override string ToString()
    {
        var builder = new StringBuilder();

        if ( !string.IsNullOrWhiteSpace( Namespace ) )
            builder.Append( Namespace + ":" );

        builder.AppendJoin( '.', BucketName, ScopeName, CollectionName );

        return builder.ToString();
    }
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

    public KeyspaceRef ParseExpression( ReadOnlySpan<char> expr, out int count, bool partial = false )
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
                            captureStart = i - 1;
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

                    keyspace[k++] = Capture( expr, captureStart, i );
                    count = i;
                    scanner = Scanner.Trailing;
                    break;

                case Scanner.Unescaped:
                    // [a-zA-Z_] ( [0-9a-zA-Z_$] )*

                    if ( IsValidIdentifierChar( c, false ) )
                        continue;

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
            keyspace[k] = Capture( expr, captureStart, i );
            count = i;
        }

        // return record

        return k switch
        {
            1 when ns => throw new InvalidOperationException( "A namespace was specified without any related keyspace values." ),
            1 when partial => new KeyspaceRef( default, default, default, keyspace[0] ),
            2 when partial && ns => new KeyspaceRef( keyspace[0], default, default, keyspace[1] ),
            _ when !ns => new KeyspaceRef( default, keyspace[0], keyspace[1], keyspace[2] ),
            _ => new KeyspaceRef( keyspace[0], keyspace[1], keyspace[2], keyspace[3] )
        };
    }
}