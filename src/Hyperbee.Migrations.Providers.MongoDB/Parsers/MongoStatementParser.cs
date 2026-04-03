using Parlot;
using Parlot.Fluent;
using static Parlot.Fluent.Parsers;

namespace Hyperbee.Migrations.Providers.MongoDB.Parsers;

// THIS IS A *PARTIAL* STATEMENT PARSER. IT DOES *NOT* SUPPORT ALL MONGODB OPERATIONS.
//
// It is intended for reading statements from statements.json resource files.

public class MongoStatementParser
{
    private static readonly Parser<MongoStatementItem> StatementParser = BuildParser();

    private static Parser<MongoStatementItem> BuildParser()
    {
        // keywords (case-insensitive)

        var create = Terms.Text( "CREATE", caseInsensitive: true );
        var drop = Terms.Text( "DROP", caseInsensitive: true );
        var collection = Terms.Text( "COLLECTION", caseInsensitive: true );
        var index = Terms.Text( "INDEX", caseInsensitive: true );
        var unique = Terms.Text( "UNIQUE", caseInsensitive: true );
        var on = Terms.Text( "ON", caseInsensitive: true );
        var insert = Terms.Text( "INSERT", caseInsensitive: true );
        var into = Terms.Text( "INTO", caseInsensitive: true );

        // terminals

        var openParen = Terms.Char( '(' );
        var closeParen = Terms.Char( ')' );
        var comma = Terms.Char( ',' );
        var dot = Terms.Char( '.' );

        // identifier: plain or backtick-quoted

        var plainIdentifier = Terms.Pattern( static c => char.IsLetterOrDigit( c ) || c == '_' || c == '-' );
        var quotedIdentifier = Between( Terms.Char( '`' ), Terms.Pattern( static c => c != '`' ), Terms.Char( '`' ) );
        var identifier = quotedIdentifier.Or( plainIdentifier );

        // database.collection reference

        var dottedRef = identifier
            .AndSkip( dot )
            .And( identifier )
            .Then( static x => (Database: x.Item1.ToString(), Collection: x.Item2.ToString()) );

        // field list: (field1, field2, ...)

        var fieldList = Between(
            openParen,
            Separated( comma, identifier.Then( static x => x.ToString() ) ),
            closeParen
        );

        // CREATE COLLECTION database.collection

        var createCollection = create
            .SkipAnd( collection )
            .SkipAnd( dottedRef )
            .Then( static x => new MongoStatementItem(
                MongoStatementType.CreateCollection,
                default,
                x.Database,
                x.Collection
            ) );

        // DROP COLLECTION database.collection

        var dropCollection = drop
            .SkipAnd( collection )
            .SkipAnd( dottedRef )
            .Then( static x => new MongoStatementItem(
                MongoStatementType.DropCollection,
                default,
                x.Database,
                x.Collection
            ) );

        // CREATE UNIQUE INDEX index_name ON database.collection(field1, field2)

        var createUniqueIndex = create
            .SkipAnd( unique )
            .SkipAnd( index )
            .SkipAnd( identifier )
            .AndSkip( on )
            .And( dottedRef )
            .And( fieldList )
            .Then( static x => new MongoStatementItem(
                MongoStatementType.CreateUniqueIndex,
                default,
                x.Item2.Database,
                x.Item2.Collection,
                x.Item1.ToString(),
                x.Item3.ToArray()
            ) );

        // CREATE INDEX index_name ON database.collection(field1, field2)

        var createIndex = create
            .SkipAnd( index )
            .SkipAnd( identifier )
            .AndSkip( on )
            .And( dottedRef )
            .And( fieldList )
            .Then( static x => new MongoStatementItem(
                MongoStatementType.CreateIndex,
                default,
                x.Item2.Database,
                x.Item2.Collection,
                x.Item1.ToString(),
                x.Item3.ToArray()
            ) );

        // DROP INDEX index_name ON database.collection

        var dropIndex = drop
            .SkipAnd( index )
            .SkipAnd( identifier )
            .AndSkip( on )
            .And( dottedRef )
            .Then( static x => new MongoStatementItem(
                MongoStatementType.DropIndex,
                default,
                x.Item2.Database,
                x.Item2.Collection,
                x.Item1.ToString()
            ) );

        // INSERT INTO database.collection

        var insertInto = insert
            .SkipAnd( into )
            .SkipAnd( dottedRef )
            .Then( static x => new MongoStatementItem(
                MongoStatementType.Insert,
                default,
                x.Database,
                x.Collection
            ) );

        // top-level parser: try each statement type
        // ORDER MATTERS: createUniqueIndex must come before createIndex,
        // createCollection must come before createIndex (both start with CREATE)

        return OneOf(
            createUniqueIndex,
            createIndex,
            createCollection,
            dropIndex,
            dropCollection,
            insertInto
        );
    }

    public MongoStatementItem ParseStatement( string statement )
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
