using Parlot;
using Parlot.Fluent;
using static Parlot.Fluent.Parsers;

namespace Hyperbee.Migrations.Providers.Aerospike.Parsers;

// THIS IS A *PARTIAL* STATEMENT PARSER. IT DOES *NOT* SUPPORT ALL AQL STATEMENT SHAPES.
//
// It is intended for reading statements from statements.json resource files.

public class AerospikeStatementParser
{
    private static readonly Parser<AerospikeStatementItem> StatementParser = BuildParser();

    private static Parser<AerospikeStatementItem> BuildParser()
    {
        // keywords (case-insensitive)

        var create = Terms.Text( "CREATE", caseInsensitive: true );
        var drop = Terms.Text( "DROP", caseInsensitive: true );
        var index = Terms.Text( "INDEX", caseInsensitive: true );
        var set = Terms.Text( "SET", caseInsensitive: true );
        var insert = Terms.Text( "INSERT", caseInsensitive: true );
        var into = Terms.Text( "INTO", caseInsensitive: true );
        var delete = Terms.Text( "DELETE", caseInsensitive: true );
        var from = Terms.Text( "FROM", caseInsensitive: true );
        var on = Terms.Text( "ON", caseInsensitive: true );
        var values = Terms.Text( "VALUES", caseInsensitive: true );
        var where = Terms.Text( "WHERE", caseInsensitive: true );

        // index type keywords

        var stringType = Terms.Text( "STRING", caseInsensitive: true );
        var numericType = Terms.Text( "NUMERIC", caseInsensitive: true );
        var geoType = Terms.Text( "GEO2DSPHERE", caseInsensitive: true );

        // terminals

        var openParen = Terms.Char( '(' );
        var closeParen = Terms.Char( ')' );
        var dot = Terms.Char( '.' );

        // identifier: plain or backtick-quoted

        var plainIdentifier = Terms.Pattern( static c => char.IsLetterOrDigit( c ) || c == '_' || c == '-' );
        var quotedIdentifier = Between( Terms.Char( '`' ), Terms.Pattern( static c => c != '`' ), Terms.Char( '`' ) );
        var identifier = quotedIdentifier.Or( plainIdentifier );

        // namespace.set reference

        var dottedRef = identifier
            .AndSkip( dot )
            .And( identifier )
            .Then( static x => (Namespace: x.Item1.ToString(), Set: x.Item2.ToString()) );

        // bin name in parentheses: (bin_name)

        var parenBinName = Between( openParen, identifier, closeParen )
            .Then( static x => x.ToString() );

        // index type: STRING | NUMERIC | GEO2DSPHERE (optional)

        var indexType = OneOf(
            stringType.Then( static _ => AerospikeIndexType.String ),
            numericType.Then( static _ => AerospikeIndexType.Numeric ),
            geoType.Then( static _ => AerospikeIndexType.Geo2DSphere )
        );

        // CREATE INDEX index_name ON namespace.set (bin_name) [STRING|NUMERIC|GEO2DSPHERE]

        var createIndex = create
            .SkipAnd( index )
            .SkipAnd( identifier )
            .AndSkip( on )
            .And( dottedRef )
            .And( parenBinName )
            .And( ZeroOrOne( indexType ) )
            .Then( static x => new AerospikeStatementItem(
                AerospikeStatementType.CreateIndex,
                default,
                x.Item2.Namespace,
                x.Item2.Set,
                x.Item1.ToString(),
                x.Item3,
                x.Item4 == default ? AerospikeIndexType.String : x.Item4
            ) );

        // DROP INDEX namespace index_name
        // Note: AQL uses `DROP INDEX namespace index_name` (no dot, no ON)

        var dropIndex = drop
            .SkipAnd( index )
            .SkipAnd( identifier )
            .And( identifier )
            .Then( static x => new AerospikeStatementItem(
                AerospikeStatementType.DropIndex,
                default,
                x.Item1.ToString(),
                null,
                x.Item2.ToString()
            ) );

        // CREATE SET namespace.set

        var createSet = create
            .SkipAnd( set )
            .SkipAnd( dottedRef )
            .Then( static x => new AerospikeStatementItem(
                AerospikeStatementType.CreateSet,
                default,
                x.Namespace,
                x.Set
            ) );

        // INSERT INTO namespace.set (PK, bin1, bin2, ...) VALUES ('key', 'val1', 123, ...)
        // We parse the basic structure but capture everything after namespace.set as expression

        var insertInto = insert
            .SkipAnd( into )
            .SkipAnd( dottedRef )
            .Then( static x => new AerospikeStatementItem(
                AerospikeStatementType.Insert,
                default,
                x.Namespace,
                x.Set
            ) );

        // DELETE FROM namespace.set WHERE PK = 'key'

        var deleteFrom = delete
            .SkipAnd( from )
            .SkipAnd( dottedRef )
            .Then( static x => new AerospikeStatementItem(
                AerospikeStatementType.Delete,
                default,
                x.Namespace,
                x.Set
            ) );

        // top-level parser: try each statement type
        // ORDER MATTERS: createIndex before createSet (both start with CREATE)

        return OneOf(
            createIndex,
            createSet,
            dropIndex,
            deleteFrom,
            insertInto
        );
    }

    public AerospikeStatementItem ParseStatement( string statement )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace( statement );

        if ( !StatementParser.TryParse( statement, out var result ) )
        {
            throw new NotSupportedException( $"Unknown statement or syntax error. `{statement}`" );
        }

        // Attach the original statement text
        return result with { Statement = statement };
    }
}
