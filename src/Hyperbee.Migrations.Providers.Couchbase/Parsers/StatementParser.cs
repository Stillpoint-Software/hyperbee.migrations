using System;
using System.Text.RegularExpressions;

namespace Hyperbee.Migrations.Providers.Couchbase.Parsers;

public record StatementItem( StatementType StatementType, string Statement, KeyspaceRef Keyspace, string Name, string Expression );

public enum StatementType
{
    Index,
    PrimaryIndex,
    Scope,
    Collection,
    Build
}

public class StatementParser
{
    // THIS IS A *PARTIAL* STATEMENT PARSER. IT DOES *NOT* SUPPORT ALL N1QL STATEMENT SHAPES.
    //
    // It is intended for reading statements from statements.json resource files. 

    private readonly KeyspaceParser _keyspaceParser = new ();

    public StatementItem ParseStatement( string statement )
    {
        static string Unquote( ReadOnlySpan<char> value ) => value.Trim().Trim( '`' ).ToString();

        // create-index ::= CREATE INDEX index-name ON keyspace-ref '(' index-key [ index-order ] [ ',' index-key [ index-order ] ]* ')' [ where-clause ] [ index-using ] [ index-with ]

        var match = Regex.Match( statement, @"^\s*CREATE\s+INDEX\s*(?<name>.*)\s+ON\s*(?<on>.+)", RegexOptions.IgnoreCase );

        if ( match.Success )
        {
            var name = Unquote( match.Groups["name"].Value );
            var on = match.Groups["on"].ValueSpan;
            SplitExpression( on, out var k, out var e );
            return new StatementItem( StatementType.Index, statement, k, name, e.ToString() );
        }

        // create-primary-index ::= CREATE PRIMARY INDEX [ index-name ] ON keyspace-ref [ index-using ] [ index-with ]

        match = Regex.Match( statement, @"^\s*CREATE\s+PRIMARY\s+INDEX\s*(?<name>.*)?\s+ON\s*(?<on>.+)", RegexOptions.IgnoreCase );

        if ( match.Success )
        {
            var name = Unquote( match.Groups["name"].Value );
            var on = match.Groups["on"].ValueSpan;
            SplitExpression( on, out var k, out var e );
            return new StatementItem( StatementType.PrimaryIndex, statement, k, name, e.ToString() );
        }

        // create-collection ::= CREATE COLLECTION [ [ namespace ':' ] bucket '.' scope '.' ] collection

        match = Regex.Match( statement, @"^\s*CREATE\s+COLLECTION\s+(?<on>.+)", RegexOptions.IgnoreCase );

        if ( match.Success )
        {
            var on = match.Groups["on"].ValueSpan;
            SplitExpression( on, out var k, out var e, true );
            return new StatementItem( StatementType.Collection, statement, k, default, e.ToString() );
        }
        // create-scope ::= CREATE SCOPE [ namespace ':' ] bucket '.' scope

        match = Regex.Match( statement, @"^\s*CREATE\s+SCOPE\s+(?<on>.+)", RegexOptions.IgnoreCase );

        if ( match.Success )
        {
            var on = match.Groups["on"].ValueSpan;
            SplitExpression( on, out var k, out var e );
            return new StatementItem( StatementType.Scope, statement, k, default, e.ToString() );
        }

        // build-index ::= BUILD INDEX ON keyspace-ref '(' index-term [ ',' index-term ]* ')' [ index-using ]

        match = Regex.Match( statement, @"^\s*BUILD\s+INDEX\s+ON\s+(?<on>.+)", RegexOptions.IgnoreCase );

        if ( match.Success )
        {
            var on = match.Groups["on"].ValueSpan;
            SplitExpression( on, out var k, out var e );
            return new StatementItem( StatementType.Build, statement, k, default, e.ToString() );
        }

        // Ruh-Rough

        throw new NotSupportedException( $"Unknown statement format. `{statement}`" );
    }

    private void SplitExpression( ReadOnlySpan<char> expr, out KeyspaceRef keyspace, out ReadOnlySpan<char> expr1, bool partial = false )
    {
        var options = new KeySpaceParserOptions
        {
            Partial = partial
        };

        keyspace = _keyspaceParser.ParseExpression( expr, out var count, options );
        expr1 = expr[count..].Trim();
    }
}