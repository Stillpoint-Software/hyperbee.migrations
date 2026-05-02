#nullable enable
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Ast;
using Parlot.Fluent;
using static Parlot.Fluent.Parsers;

namespace Hyperbee.Migrations.Providers.OpenSearch.Internal.Grammar;

// PARTIAL OpenSearch statement parser. Phase 0 spike scope:
//   CREATE INDEX <name> [IF NOT EXISTS] [WITH BODY $body]
//   REINDEX [UNSAFE("<reason>")] FROM <src> TO <dst> [WITH BODY $body]
//
// Per ADR-0011: parser owns intent. AST nodes carry safe-default flags;
// runtime middleware applies them during JSON tree merge.
//
// Per ADR-0015: parser is offline-pure. No I/O at parse time. BodyRef carries
// only the sibling-property name; the body itself is resolved by the caller.
//
// Grammar style mirrors Couchbase StatementParser (ADR-0001 house pattern):
// static parser cache, `Terms.Text(..., caseInsensitive: true)` for keywords,
// backtick-or-plain identifiers, ordered OneOf at the top level.

public sealed class OpenSearchStatementParser
{
    private static readonly Parser<StatementAst> ParlotParser = BuildParser();

    private static Parser<StatementAst> BuildParser()
    {
        // keywords (case-insensitive)

        var create = Terms.Text( "CREATE", caseInsensitive: true );
        var index = Terms.Text( "INDEX", caseInsensitive: true );
        var @if = Terms.Text( "IF", caseInsensitive: true );
        var not = Terms.Text( "NOT", caseInsensitive: true );
        var exists = Terms.Text( "EXISTS", caseInsensitive: true );
        var with = Terms.Text( "WITH", caseInsensitive: true );
        var body = Terms.Text( "BODY", caseInsensitive: true );
        var reindex = Terms.Text( "REINDEX", caseInsensitive: true );
        var from = Terms.Text( "FROM", caseInsensitive: true );
        var to = Terms.Text( "TO", caseInsensitive: true );
        var unsafeKw = Terms.Text( "UNSAFE", caseInsensitive: true );

        // identifier: plain, dashed, or backtick-quoted.
        // OpenSearch index names allow letters/digits/-/_/. but the parser is permissive
        // enough that the cluster will reject truly invalid names at execution.

        var plainIdentifier = Terms.Pattern( static c => char.IsLetterOrDigit( c ) || c == '_' || c == '-' || c == '.' );
        var quotedIdentifier = Between( Terms.Char( '`' ), Terms.Pattern( static c => c != '`' ), Terms.Char( '`' ) );
        var identifier = quotedIdentifier.Or( plainIdentifier ).Then( static x => x.ToString()! );

        // body reference: `WITH BODY $name` resolves against sibling JSON properties

        var dollar = Terms.Char( '$' );
        var bodyRef = with.SkipAnd( body ).SkipAnd( dollar ).SkipAnd( identifier )
            .Then( static name => new BodyRef( name ) );

        // CREATE INDEX <name> [IF NOT EXISTS] [WITH BODY $body]
        // IF NOT EXISTS comes BEFORE WITH BODY in canonical form

        var ifNotExists = @if.SkipAnd( not ).SkipAnd( exists ).Then( static _ => true );

        var createIndex = create
            .SkipAnd( index )
            .SkipAnd( identifier )
            .And( ZeroOrOne( ifNotExists ) )
            .And( ZeroOrOne( bodyRef ) )
            .Then( static x => (StatementAst) new CreateIndexAst(
                IndexName: x.Item1,
                IfNotExists: x.Item2,
                Body: x.Item3,
                InjectDynamicStrict: true
            ) );

        // REINDEX [UNSAFE("<reason>")] FROM <src> TO <dst> [WITH BODY $body]
        //
        // UNSAFE requires a non-empty justification. Bare `UNSAFE` (without parentheses
        // and a string literal) fails at parse time with a remediation message.

        var quotedString = Between(
            Terms.Char( '"' ),
            Terms.Pattern( static c => c != '"' ),
            Terms.Char( '"' )
        ).Then( static x =>
        {
            var s = x.ToString()!;
            if ( string.IsNullOrWhiteSpace( s ) )
                throw new InvalidOperationException( "UNSAFE/NO WAIT justification must be a non-empty string." );
            return s;
        } );

        var unsafeWithJustification = unsafeKw
            .SkipAnd( Terms.Char( '(' ) )
            .SkipAnd( quotedString )
            .AndSkip( Terms.Char( ')' ) );

        var reindexCore = reindex
            .SkipAnd( ZeroOrOne( unsafeWithJustification ) )
            .AndSkip( from )
            .And( identifier )
            .AndSkip( to )
            .And( identifier )
            .And( ZeroOrOne( bodyRef ) )
            .Then( static x =>
            {
                var unsafeReason = x.Item1; // null if not present
                var src = x.Item2;
                var dst = x.Item3;
                var bodyR = x.Item4;
                return (StatementAst) new ReindexAst(
                    Source: src,
                    Destination: dst,
                    Body: bodyR,
                    InjectOpTypeCreate: unsafeReason == null,
                    UnsafeJustification: unsafeReason
                );
            } );

        return OneOf( createIndex, reindexCore );
    }

    /// <summary>
    /// Parses a single statement string into a typed AST.
    /// </summary>
    /// <exception cref="OpenSearchParseException">
    /// Thrown when the statement does not match any supported verb or fails grammar
    /// validation. Message includes the offending statement.
    /// </exception>
    public StatementAst Parse( string statement )
    {
        ArgumentException.ThrowIfNullOrWhiteSpace( statement );

        if ( !ParlotParser.TryParse( statement, out var result, out var error ) )
        {
            var hint = error?.Message ?? "no recognized verb prefix";
            throw new OpenSearchParseException(
                $"Unable to parse statement: `{statement}`. {hint}." );
        }

        return result;
    }
}

public sealed class OpenSearchParseException : Exception
{
    public OpenSearchParseException( string message ) : base( message ) { }
    public OpenSearchParseException( string message, Exception inner ) : base( message, inner ) { }
}
