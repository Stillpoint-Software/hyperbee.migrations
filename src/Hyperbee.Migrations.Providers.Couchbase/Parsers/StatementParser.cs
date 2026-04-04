using System;
using System.Collections.Generic;
using System.Text.RegularExpressions;
using Couchbase.Management.Buckets;
using Parlot;
using Parlot.Fluent;
using static Parlot.Fluent.Parsers;

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
    Update,
    Build
}

public class StatementParser
{
    // THIS IS A *PARTIAL* STATEMENT PARSER. IT DOES *NOT* SUPPORT ALL N1QL STATEMENT SHAPES.
    //
    // It is intended for reading statements from statements.json resource files.

    private static readonly Parser<StatementItem> ParlotParser = BuildParser();

    private static Parser<StatementItem> BuildParser()
    {
        // keywords (case-insensitive)

        var create = Terms.Text( "CREATE", caseInsensitive: true );
        var drop = Terms.Text( "DROP", caseInsensitive: true );
        var primary = Terms.Text( "PRIMARY", caseInsensitive: true );
        var index = Terms.Text( "INDEX", caseInsensitive: true );
        var bucket = Terms.Text( "BUCKET", caseInsensitive: true );
        var scope = Terms.Text( "SCOPE", caseInsensitive: true );
        var collection = Terms.Text( "COLLECTION", caseInsensitive: true );
        var on = Terms.Text( "ON", caseInsensitive: true );
        var update = Terms.Text( "UPDATE", caseInsensitive: true );
        var build = Terms.Text( "BUILD", caseInsensitive: true );

        // terminals

        var dot = Terms.Char( '.' );
        var colon = Terms.Char( ':' );

        // identifier: plain or backtick-quoted

        var plainIdentifier = Terms.Pattern( static c => char.IsLetterOrDigit( c ) || c == '_' || c == '$' );
        var quotedIdentifier = Between( Terms.Char( '`' ), Terms.Pattern( static c => c != '`' ), Terms.Char( '`' ) );
        var identifier = quotedIdentifier.Or( plainIdentifier );

        // keyspace components - build up from identifiers

        // namespace:  (optional prefix ending with colon)
        var namespacePrefix = identifier.AndSkip( colon )
            .Then( static x => x.ToString() );

        // dotted parts: a.b or a.b.c
        var oneIdent = identifier.Then( static x => x.ToString() );

        var twoPart = identifier.AndSkip( dot ).And( identifier )
            .Then( static x => (x.Item1.ToString(), x.Item2.ToString()) );

        var threePart = identifier.AndSkip( dot ).And( identifier ).AndSkip( dot ).And( identifier )
            .Then( static x => (x.Item1.ToString(), x.Item2.ToString(), x.Item3.ToString()) );

        // keyspace-ref: [namespace:]bucket[.scope.collection]
        // Parse the most specific form first

        var keyspaceNs3 = namespacePrefix.And( threePart )
            .Then( static x => new KeyspaceRef( x.Item1, x.Item2.Item1, x.Item2.Item2, x.Item2.Item3 ) );

        var keyspace3 = threePart
            .Then( static x => new KeyspaceRef( null, x.Item1, x.Item2, x.Item3 ) );

        var keyspaceNs2 = namespacePrefix.And( twoPart )
            .Then( static x => new KeyspaceRef( x.Item1, x.Item2.Item1, null, x.Item2.Item2 ) );

        var keyspace2 = twoPart
            .Then( static x => new KeyspaceRef( null, x.Item1, null, x.Item2 ) );

        var keyspaceNs1 = namespacePrefix.And( oneIdent )
            .Then( static x => new KeyspaceRef( x.Item1, x.Item2, null, null ) );

        var keyspace1 = oneIdent
            .Then( static x => new KeyspaceRef( null, x, null, null ) );

        var keyspaceRef = OneOf( keyspaceNs3, keyspace3, keyspaceNs2, keyspace2, keyspaceNs1, keyspace1 );

        // partial keyspace for collections (can be just a collection name)

        var partialKeyspace = OneOf( keyspaceNs3, keyspace3, keyspaceNs2, keyspace2, keyspaceNs1, keyspace1 );

        // CREATE PRIMARY INDEX [name] ON keyspace
        // Must come before CREATE INDEX
        // The optional name must not match the keyword ON

        var indexNameNotOn = identifier
            .Then( static x => x.ToString() )
            .When( static ( _, v ) => !v.Equals( "ON", StringComparison.OrdinalIgnoreCase ) );

        var createPrimaryIndex = create
            .SkipAnd( primary )
            .SkipAnd( index )
            .SkipAnd( ZeroOrOne( indexNameNotOn ) )
            .AndSkip( on )
            .And( keyspaceRef )
            .Then( static x => new StatementItem(
                StatementType.CreatePrimaryIndex,
                default,
                x.Item2,
                x.Item1?.Trim( '`' ),
                null
            ) );

        // CREATE INDEX name ON keyspace(...)

        var createIndex = create
            .SkipAnd( index )
            .SkipAnd( identifier )
            .AndSkip( on )
            .And( keyspaceRef )
            .Then( static x => new StatementItem(
                StatementType.CreateIndex,
                default,
                x.Item2,
                x.Item1.ToString().Trim( '`' ),
                null
            ) );

        // CREATE BUCKET keyspace [TYPE ...] [RAMQUOTA ...] [FLUSH ...] [REPLICAS ...]

        var createBucket = create
            .SkipAnd( bucket )
            .SkipAnd( keyspaceRef )
            .Then( static x => new StatementItem(
                StatementType.CreateBucket,
                default,
                x,
                null,
                null
            ) );

        // CREATE SCOPE keyspace

        var createScope = create
            .SkipAnd( scope )
            .SkipAnd( keyspaceRef )
            .Then( static x => new StatementItem(
                StatementType.CreateScope,
                default,
                x,
                null,
                null
            ) );

        // CREATE COLLECTION keyspace

        var createCollection = create
            .SkipAnd( collection )
            .SkipAnd( partialKeyspace )
            .Then( static x => new StatementItem(
                StatementType.CreateCollection,
                default,
                x,
                null,
                null
            ) );

        // DROP BUCKET keyspace

        var dropBucket = drop
            .SkipAnd( bucket )
            .SkipAnd( keyspaceRef )
            .Then( static x => new StatementItem(
                StatementType.DropBucket,
                default,
                x,
                null,
                null
            ) );

        // DROP SCOPE keyspace

        var dropScope = drop
            .SkipAnd( scope )
            .SkipAnd( keyspaceRef )
            .Then( static x => new StatementItem(
                StatementType.DropScope,
                default,
                x,
                null,
                null
            ) );

        // DROP COLLECTION keyspace

        var dropCollection = drop
            .SkipAnd( collection )
            .SkipAnd( partialKeyspace )
            .Then( static x => new StatementItem(
                StatementType.DropCollection,
                default,
                x,
                null,
                null
            ) );

        // BUILD INDEX ON keyspace(...)

        var buildIndex = build
            .SkipAnd( index )
            .SkipAnd( on )
            .SkipAnd( keyspaceRef )
            .Then( static x => new StatementItem(
                StatementType.Build,
                default,
                x,
                null,
                null
            ) );

        // UPDATE keyspace SET ...

        var updateStmt = update
            .SkipAnd( keyspaceRef )
            .Then( static x => new StatementItem(
                StatementType.Update,
                default,
                x,
                null,
                null
            ) );

        // top-level: ORDER MATTERS
        // createPrimaryIndex before createIndex (both start with CREATE INDEX-ish)
        // createIndex before createBucket/createScope/createCollection

        return OneOf(
            createPrimaryIndex,
            createIndex,
            createBucket,
            createScope,
            createCollection,
            dropBucket,
            dropScope,
            dropCollection,
            buildIndex,
            updateStmt
        );
    }

    public StatementItem ParseStatement( string statement )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace( statement );

        if ( !ParlotParser.TryParse( statement, out var result ) )
        {
            throw new NotSupportedException( $"Unknown statement or syntax error. `{statement}`" );
        }

        // Attach the original statement and parse bucket settings if needed
        result = result with { Statement = statement };

        if ( result.StatementType == StatementType.CreateBucket )
        {
            result = result with { BucketSettings = ParseBucketSettings( result.Keyspace, statement ) };
        }

        return result;
    }

    private static readonly IReadOnlyDictionary<string, BucketType> BucketTypes = new Dictionary<string, BucketType>( StringComparer.OrdinalIgnoreCase )
    {
        ["Couchbase"] = BucketType.Couchbase,
        ["Ephemeral"] = BucketType.Ephemeral,
        ["Memcached"] = BucketType.Memcached
    };

    private static BucketSettings ParseBucketSettings( KeyspaceRef keyspace, string statement )
    {
        var settings = new BucketSettings
        {
            Name = keyspace.BucketName,
            BucketType = BucketType.Couchbase,
            RamQuotaMB = 256,
            FlushEnabled = true
        };

        // match TYPE, RAMQUOTA, FLUSH ENABLED, REPLICAS anywhere in the statement
        var typeMatch = Regex.Match( statement, @"\bTYPE\s+(?<type>COUCHBASE|MEMCACHED|EPHEMERAL)\b", RegexOptions.IgnoreCase );
        if ( typeMatch.Success && BucketTypes.TryGetValue( typeMatch.Groups["type"].Value, out var bucketType ) )
            settings.BucketType = bucketType;

        var quotaMatch = Regex.Match( statement, @"\bRAMQUOTA\s+(?<quota>\d+)", RegexOptions.IgnoreCase );
        if ( quotaMatch.Success )
            settings.RamQuotaMB = int.Parse( quotaMatch.Groups["quota"].ValueSpan );

        if ( Regex.IsMatch( statement, @"\bFLUSH\s+ENABLED\b", RegexOptions.IgnoreCase ) )
            settings.FlushEnabled = true;

        var replicasMatch = Regex.Match( statement, @"\bREPLICAS\s+(?<replicas>\d+)", RegexOptions.IgnoreCase );
        if ( replicasMatch.Success )
            settings.NumReplicas = int.Parse( replicasMatch.Groups["replicas"].ValueSpan );

        return settings;
    }
}
