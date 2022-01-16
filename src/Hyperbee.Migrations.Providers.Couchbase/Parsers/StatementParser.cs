using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Couchbase.Management.Buckets;

namespace Hyperbee.Migrations.Providers.Couchbase.Parsers;

public record StatementItem( StatementType StatementType, string Statement, KeyspaceRef Keyspace, string Name, string Expression )
{
    public BucketSettings BucketSettings { get; init; }
}

public enum StatementType
{
    CreateBucket,
    CreateIndex,
    CreatePrimaryIndex,
    CreateScope,
    CreateCollection,
    DropBucket,
    DropScope,
    DropCollection,
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
            ParseKeyspace( on, out var k, out var e );
            return new StatementItem( StatementType.CreateIndex, statement, k, name, e.ToString() );
        }

        // create-primary-index ::= CREATE PRIMARY INDEX [ index-name ] ON keyspace-ref [ index-using ] [ index-with ]

        match = Regex.Match( statement, @"^\s*CREATE\s+PRIMARY\s+INDEX\s*(?<name>.*)?\s+ON\s*(?<on>.+)", RegexOptions.IgnoreCase );

        if ( match.Success )
        {
            var name = Unquote( match.Groups["name"].Value );
            var on = match.Groups["on"].ValueSpan;
            ParseKeyspace( on, out var k, out var e );
            return new StatementItem( StatementType.CreatePrimaryIndex, statement, k, name, e.ToString() );
        }

        // create-bucket-extension ::= CREATE BUCKET [ namespace ':' ] bucket [TYPE Couchbase|Memcached|Ephemeral] [RAMQUOTA 256] [FLUSH ENABLED]
        // pseudo n1ql statement

        match = Regex.Match( statement, @"^\s*CREATE\s+BUCKET\s+(?<keyspace>.+)", RegexOptions.IgnoreCase );

        if ( match.Success )
        {
            var keyspace = match.Groups["keyspace"].ValueSpan;
            ParseKeyspace( keyspace, out var k, out var e );

            return new StatementItem( StatementType.CreateBucket, statement, k, default, e.ToString() )
            {
                BucketSettings = ParseBucketSettings( k, e )
            };
        }

        // create-collection ::= CREATE COLLECTION [ [ namespace ':' ] bucket '.' scope '.' ] collection

        match = Regex.Match( statement, @"^\s*CREATE\s+COLLECTION\s+(?<keyspace>.+)", RegexOptions.IgnoreCase );

        if ( match.Success )
        {
            var keyspace = match.Groups["keyspace"].ValueSpan;
            ParseKeyspace( keyspace, out var k, out var e, true );
            return new StatementItem( StatementType.CreateCollection, statement, k, default, e.ToString() );
        }
        
        // create-scope ::= CREATE SCOPE [ namespace ':' ] bucket '.' scope

        match = Regex.Match( statement, @"^\s*CREATE\s+SCOPE\s+(?<keyspace>.+)", RegexOptions.IgnoreCase );

        if ( match.Success )
        {
            var keyspace = match.Groups["keyspace"].ValueSpan;
            ParseKeyspace( keyspace, out var k, out var e );
            return new StatementItem( StatementType.CreateScope, statement, k, default, e.ToString() );
        }

        // drop-bucket-extension ::= DROP BUCKET [ namespace ':' ] bucket
        // pseudo n1ql statement

        match = Regex.Match( statement, @"^\s*DROP\s+BUCKET\s+(?<keyspace>.+)", RegexOptions.IgnoreCase );

        if ( match.Success )
        {
            var keyspace = match.Groups["keyspace"].ValueSpan;
            ParseKeyspace( keyspace, out var k, out _ );
            return new StatementItem( StatementType.DropBucket, statement, k, default, default );
        }

        // drop-collection ::= DROP COLLECTION [ [ namespace ':' ] bucket '.' scope '.' ] collection

        match = Regex.Match( statement, @"^\s*DROP\s+COLLECTION\s+(?<keyspace>.+)", RegexOptions.IgnoreCase );

        if ( match.Success )
        {
            var keyspace = match.Groups["keyspace"].ValueSpan;
            ParseKeyspace( keyspace, out var k, out var e, true );
            return new StatementItem( StatementType.DropCollection, statement, k, default, e.ToString() );
        }

        // drop-scope ::= DROP SCOPE [ namespace ':' ] bucket '.' scope

        match = Regex.Match( statement, @"^\s*DROP\s+SCOPE\s+(?<keyspace>.+)", RegexOptions.IgnoreCase );

        if ( match.Success )
        {
            var keyspace = match.Groups["keyspace"].ValueSpan;
            ParseKeyspace( keyspace, out var k, out var e );
            return new StatementItem( StatementType.DropScope, statement, k, default, e.ToString() );
        }

        // build-index ::= BUILD INDEX ON keyspace-ref '(' index-term [ ',' index-term ]* ')' [ index-using ]

        match = Regex.Match( statement, @"^\s*BUILD\s+INDEX\s+ON\s+(?<on>.+)", RegexOptions.IgnoreCase );

        if ( match.Success )
        {
            var on = match.Groups["on"].ValueSpan;
            ParseKeyspace( on, out var k, out var e );
            return new StatementItem( StatementType.Build, statement, k, default, e.ToString() );
        }

        // Ruh-Rough

        throw new NotSupportedException( $"Unknown statement or syntax error. `{statement}`" );
    }

    private void ParseKeyspace( ReadOnlySpan<char> expr, out KeyspaceRef keyspace, out ReadOnlySpan<char> trailingExpr, bool partial = false )
    {
        var options = new KeySpaceParserOptions
        {
            Partial = partial
        };

        keyspace = _keyspaceParser.ParseExpression( expr, out var count, options );
        trailingExpr = expr[count..].Trim();
    }

    private static readonly IReadOnlyDictionary<string,BucketType> BucketTypes = new Dictionary<string,BucketType>( StringComparer.OrdinalIgnoreCase )
    {
        ["Couchbase"] = BucketType.Couchbase,
        ["Ephemeral"] = BucketType.Ephemeral,
        ["Memcached"] = BucketType.Memcached
    };

    private static BucketSettings ParseBucketSettings( KeyspaceRef keyspace, ReadOnlySpan<char> expr )
    {
        var settings = new BucketSettings
        {
            Name = keyspace.BucketName,
            BucketType = BucketType.Couchbase,
            RamQuotaMB = 256,
            FlushEnabled = true
        };

        if ( expr.IsEmpty )
            return settings;

        var match = Regex.Match( expr.ToString(), @"^\s*(?:TYPE\s+\b(?<type>Couchbase|Memcached|Ephemeral)\b)?(?:\s+RAMQUOTA\s+(?<quota>\d+))?(?:\s+(?<flush>FLUSH ENABLED))?", RegexOptions.IgnoreCase );

        if ( !match.Success )
            return settings;

        settings.RamQuotaMB = !match.Groups["quota"].ValueSpan.IsEmpty
            ? int.Parse( match.Groups["quota"].ValueSpan )
            : 256;
                
        settings.FlushEnabled = !match.Groups["flush"].ValueSpan.IsEmpty;

        if ( BucketTypes.TryGetValue( match.Groups["type"].Value, out var bucketType ) )
            settings.BucketType = bucketType;

        return settings;
    }
}