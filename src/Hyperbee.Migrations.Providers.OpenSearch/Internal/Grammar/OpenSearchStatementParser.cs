#nullable enable
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Ast;
using Parlot.Fluent;
using static Parlot.Fluent.Parsers;

namespace Hyperbee.Migrations.Providers.OpenSearch.Internal.Grammar;

// OpenSearch statement parser. Foundation + Phase 2 verbs:
//   CREATE INDEX <name> [IF NOT EXISTS] [WITH BODY $body]
//   DROP INDEX <name> [IF EXISTS]
//   UPDATE MAPPING ON <idx> [WITH BODY $body]
//   UPDATE SETTINGS ON <idx> [CLOSE] [WITH BODY $body]
//   REFRESH <name>
//   WAIT FOR <green|yellow> [ON <idx>] [TIMEOUT <duration>]
//   WAIT UNTIL TASK <id> COMPLETE [TIMEOUT <duration>]
//   REINDEX [UNSAFE("<reason>")] FROM <src> TO <dst> [WITH BODY $body]
//   ALIAS SWAP <alias> FROM <old> TO <new>
//   ALIAS ADD <alias> ON <idx>
//   ALIAS REMOVE <alias> ON <idx>
//   CREATE TEMPLATE <name> [WITH BODY $body]
//   CREATE COMPONENT <name> [WITH BODY $body]
//   DROP TEMPLATE <name> [IF EXISTS]
//   DROP COMPONENT <name> [IF EXISTS]
//   CREATE POLICY <id> [WITH BODY $body]
//   APPLY POLICY <id> TO <pattern>
//   MIGRATE INDEX <old> TO <new> [WITH TEMPLATE <id> | WITH BODY $body]
//                                [VIA ALIAS <alias>] [TIMEOUT <duration>]
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
        var drop = Terms.Text( "DROP", caseInsensitive: true );
        var update = Terms.Text( "UPDATE", caseInsensitive: true );
        var index = Terms.Text( "INDEX", caseInsensitive: true );
        var mapping = Terms.Text( "MAPPING", caseInsensitive: true );
        var settings = Terms.Text( "SETTINGS", caseInsensitive: true );
        var refreshKw = Terms.Text( "REFRESH", caseInsensitive: true );
        var on = Terms.Text( "ON", caseInsensitive: true );
        var close = Terms.Text( "CLOSE", caseInsensitive: true );
        var @if = Terms.Text( "IF", caseInsensitive: true );
        var not = Terms.Text( "NOT", caseInsensitive: true );
        var exists = Terms.Text( "EXISTS", caseInsensitive: true );
        var with = Terms.Text( "WITH", caseInsensitive: true );
        var body = Terms.Text( "BODY", caseInsensitive: true );
        var reindex = Terms.Text( "REINDEX", caseInsensitive: true );
        var from = Terms.Text( "FROM", caseInsensitive: true );
        var to = Terms.Text( "TO", caseInsensitive: true );
        var unsafeKw = Terms.Text( "UNSAFE", caseInsensitive: true );
        var wait = Terms.Text( "WAIT", caseInsensitive: true );
        var @for = Terms.Text( "FOR", caseInsensitive: true );
        var until = Terms.Text( "UNTIL", caseInsensitive: true );
        var task = Terms.Text( "TASK", caseInsensitive: true );
        var complete = Terms.Text( "COMPLETE", caseInsensitive: true );
        var timeout = Terms.Text( "TIMEOUT", caseInsensitive: true );
        var greenKw = Terms.Text( "GREEN", caseInsensitive: true );
        var yellowKw = Terms.Text( "YELLOW", caseInsensitive: true );
        var alias = Terms.Text( "ALIAS", caseInsensitive: true );
        var swap = Terms.Text( "SWAP", caseInsensitive: true );
        var add = Terms.Text( "ADD", caseInsensitive: true );
        var remove = Terms.Text( "REMOVE", caseInsensitive: true );
        var template = Terms.Text( "TEMPLATE", caseInsensitive: true );
        var component = Terms.Text( "COMPONENT", caseInsensitive: true );
        var policy = Terms.Text( "POLICY", caseInsensitive: true );
        var apply = Terms.Text( "APPLY", caseInsensitive: true );
        var migrate = Terms.Text( "MIGRATE", caseInsensitive: true );
        var via = Terms.Text( "VIA", caseInsensitive: true );

        // identifier: plain, dashed, or backtick-quoted.
        // OpenSearch index names allow letters/digits/-/_/. but the parser is permissive
        // enough that the cluster will reject truly invalid names at execution.

        var plainIdentifier = Terms.Pattern( static c => char.IsLetterOrDigit( c ) || c == '_' || c == '-' || c == '.' );
        var quotedIdentifier = Between( Terms.Char( '`' ), Terms.Pattern( static c => c != '`' ), Terms.Char( '`' ) );
        var identifier = quotedIdentifier.Or( plainIdentifier ).Then( static x => x.ToString()! );

        // index pattern: identifier characters plus `*` for wildcards (used by APPLY POLICY).
        // The cluster rejects truly invalid patterns at execution; the grammar stays permissive.

        var plainPattern = Terms.Pattern( static c => char.IsLetterOrDigit( c ) || c == '_' || c == '-' || c == '.' || c == '*' );
        var indexPattern = quotedIdentifier.Or( plainPattern ).Then( static x => x.ToString()! );

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

        // DROP INDEX <name> [IF EXISTS]

        var ifExists = @if.SkipAnd( exists ).Then( static _ => true );

        var dropIndex = drop
            .SkipAnd( index )
            .SkipAnd( identifier )
            .And( ZeroOrOne( ifExists ) )
            .Then( static x => (StatementAst) new DropIndexAst(
                IndexName: x.Item1,
                IfExists: x.Item2
            ) );

        // UPDATE MAPPING ON <idx> [WITH BODY $body]

        var updateMapping = update
            .SkipAnd( mapping )
            .SkipAnd( on )
            .SkipAnd( identifier )
            .And( ZeroOrOne( bodyRef ) )
            .Then( static x => (StatementAst) new UpdateMappingAst(
                IndexName: x.Item1,
                Body: x.Item2
            ) );

        // UPDATE SETTINGS ON <idx> [CLOSE] [WITH BODY $body]

        var closeFlag = close.Then( static _ => true );

        var updateSettings = update
            .SkipAnd( settings )
            .SkipAnd( on )
            .SkipAnd( identifier )
            .And( ZeroOrOne( closeFlag ) )
            .And( ZeroOrOne( bodyRef ) )
            .Then( static x => (StatementAst) new UpdateSettingsAst(
                IndexName: x.Item1,
                Close: x.Item2,
                Body: x.Item3
            ) );

        // REFRESH <name>

        var refreshStmt = refreshKw
            .SkipAnd( identifier )
            .Then( static name => (StatementAst) new RefreshAst( IndexName: name ) );

        // duration: <integer><s|m|h>  (e.g., 30s, 5m, 2h)
        // Pure-integer numeric durations are rejected — explicit suffix required.

        var durationParser = Terms.Integer().And( Terms.Pattern( static c => c is 's' or 'm' or 'h', minSize: 1, maxSize: 1 ) )
            .Then( static x =>
            {
                var n = x.Item1;
                var suffix = x.Item2.ToString();
                return suffix switch
                {
                    "s" => TimeSpan.FromSeconds( n ),
                    "m" => TimeSpan.FromMinutes( n ),
                    "h" => TimeSpan.FromHours( n ),
                    _ => throw new InvalidOperationException( $"Unrecognized duration suffix `{suffix}`." )
                };
            } );

        var timeoutClause = timeout.SkipAnd( durationParser );

        // WAIT FOR <green|yellow> [ON <idx>] [TIMEOUT <duration>]

        var healthThreshold = OneOf(
            greenKw.Then( static _ => HealthStatus.Green ),
            yellowKw.Then( static _ => HealthStatus.Yellow )
        );

        var onIndex = on.SkipAnd( identifier );

        var waitForHealth = wait
            .SkipAnd( @for )
            .SkipAnd( healthThreshold )
            .And( ZeroOrOne( onIndex ) )
            .And( ZeroOrOne( timeoutClause ) )
            .Then( static x => (StatementAst) new WaitForHealthAst(
                Threshold: x.Item1,
                IndexName: x.Item2,
                Timeout: x.Item3 == TimeSpan.Zero ? null : x.Item3
            ) );

        // WAIT UNTIL TASK <id> COMPLETE [TIMEOUT <duration>]

        var waitUntilTask = wait
            .SkipAnd( until )
            .SkipAnd( task )
            .SkipAnd( identifier )
            .AndSkip( complete )
            .And( ZeroOrOne( timeoutClause ) )
            .Then( static x => (StatementAst) new WaitUntilTaskAst(
                TaskId: x.Item1,
                Timeout: x.Item2 == TimeSpan.Zero ? null : x.Item2
            ) );

        // ALIAS SWAP <alias> FROM <old> TO <new>

        var aliasSwap = alias
            .SkipAnd( swap )
            .SkipAnd( identifier )       // alias name
            .AndSkip( from )
            .And( identifier )           // old index
            .AndSkip( to )
            .And( identifier )           // new index
            .Then( static x => (StatementAst) new AliasSwapAst(
                Alias: x.Item1,
                OldIndex: x.Item2,
                NewIndex: x.Item3
            ) );

        // ALIAS ADD <alias> ON <idx>

        var aliasAdd = alias
            .SkipAnd( add )
            .SkipAnd( identifier )
            .AndSkip( on )
            .And( identifier )
            .Then( static x => (StatementAst) new AliasAddAst(
                Alias: x.Item1,
                IndexName: x.Item2
            ) );

        // ALIAS REMOVE <alias> ON <idx>

        var aliasRemove = alias
            .SkipAnd( remove )
            .SkipAnd( identifier )
            .AndSkip( on )
            .And( identifier )
            .Then( static x => (StatementAst) new AliasRemoveAst(
                Alias: x.Item1,
                IndexName: x.Item2
            ) );

        // CREATE TEMPLATE <name> [WITH BODY $body]

        var createTemplate = create
            .SkipAnd( template )
            .SkipAnd( identifier )
            .And( ZeroOrOne( bodyRef ) )
            .Then( static x => (StatementAst) new CreateTemplateAst(
                TemplateName: x.Item1,
                Body: x.Item2
            ) );

        // CREATE COMPONENT <name> [WITH BODY $body]

        var createComponent = create
            .SkipAnd( component )
            .SkipAnd( identifier )
            .And( ZeroOrOne( bodyRef ) )
            .Then( static x => (StatementAst) new CreateComponentAst(
                ComponentName: x.Item1,
                Body: x.Item2
            ) );

        // DROP TEMPLATE <name> [IF EXISTS]

        var dropTemplate = drop
            .SkipAnd( template )
            .SkipAnd( identifier )
            .And( ZeroOrOne( ifExists ) )
            .Then( static x => (StatementAst) new DropTemplateAst(
                TemplateName: x.Item1,
                IfExists: x.Item2
            ) );

        // DROP COMPONENT <name> [IF EXISTS]

        var dropComponent = drop
            .SkipAnd( component )
            .SkipAnd( identifier )
            .And( ZeroOrOne( ifExists ) )
            .Then( static x => (StatementAst) new DropComponentAst(
                ComponentName: x.Item1,
                IfExists: x.Item2
            ) );

        // CREATE POLICY <id> [WITH BODY $body]

        var createPolicy = create
            .SkipAnd( policy )
            .SkipAnd( identifier )
            .And( ZeroOrOne( bodyRef ) )
            .Then( static x => (StatementAst) new CreatePolicyAst(
                PolicyId: x.Item1,
                Body: x.Item2
            ) );

        // APPLY POLICY <id> TO <pattern>

        var applyPolicy = apply
            .SkipAnd( policy )
            .SkipAnd( identifier )
            .AndSkip( to )
            .And( indexPattern )
            .Then( static x => (StatementAst) new ApplyPolicyAst(
                PolicyId: x.Item1,
                IndexPattern: x.Item2
            ) );

        // MIGRATE INDEX <old> TO <new>
        //   [WITH TEMPLATE <id> | WITH BODY $body]
        //   [VIA ALIAS <alias>]
        //   [TIMEOUT <duration>]
        //
        // R-30 composite. Decomposes at parse time into:
        //   1. CREATE INDEX <new> with body resolved from WITH TEMPLATE (runtime
        //      `GET /_index_template/<id>`) or WITH BODY $body (sibling-property
        //      reference). dynamic:strict injection still applies per R-17.
        //   2. REINDEX FROM <old> TO <new> with auto-injected `op_type: create`.
        //   3. (optional) ALIAS SWAP <alias> FROM <old> TO <new> when VIA ALIAS
        //      is present. Without VIA ALIAS, the author retains responsibility
        //      for cutover (preserves migrations that intentionally retain both
        //      indices for read-traffic comparison).
        //
        // Per ADR-0015 the parser is offline-pure: WITH TEMPLATE produces a
        // TemplateBodyRef on the CREATE INDEX child; runtime middleware fetches
        // the template body immediately before CREATE INDEX dispatch.

        var withTemplate = with.SkipAnd( template ).SkipAnd( identifier )
            .Then( static name => new TemplateBodyRef( name ) );

        // either WITH TEMPLATE <id> or WITH BODY $body, not both. Modeled as a
        // tuple (TemplateBodyRef? template, BodyRef? body) where exactly one is
        // populated. Mutual exclusion is enforced by OneOf alternation.
        var migrateBodySource = OneOf(
            withTemplate.Then( static t => ((TemplateBodyRef?) t, (BodyRef?) null) ),
            bodyRef.Then( static b => ((TemplateBodyRef?) null, (BodyRef?) b) )
        );

        var viaAlias = via.SkipAnd( alias ).SkipAnd( identifier );

        var migrateIndex = migrate
            .SkipAnd( index )
            .SkipAnd( identifier )                           // src
            .AndSkip( to )
            .And( identifier )                               // dst
            .And( ZeroOrOne( migrateBodySource ) )
            .And( ZeroOrOne( viaAlias ) )
            .And( ZeroOrOne( timeoutClause ) )
            .Then( static x =>
            {
                var src = x.Item1;
                var dst = x.Item2;
                var bodySource = x.Item3;                    // tuple may be (null, null) if omitted
                var aliasName = x.Item4;                     // null if not present
                // timeout reserved for future async-polling slice; parsed for
                // forward-compatibility but not threaded through to children
                // in this slice (sync REINDEX uses cluster-side wait_for_completion).
                _ = x.Item5;

                // R-30: same-src-dst rejected at parse time (purely syntactic).
                if ( string.Equals( src, dst, StringComparison.Ordinal ) )
                {
                    throw new InvalidOperationException(
                        $"MIGRATE INDEX requires distinct source and destination; got `{src}` for both." );
                }

                var templateBody = bodySource.Item1;
                var inlineBody = bodySource.Item2;

                var children = new List<StatementAst>( capacity: 3 )
                {
                    new CreateIndexAst(
                        IndexName: dst,
                        IfNotExists: false,
                        Body: inlineBody,
                        InjectDynamicStrict: true,
                        TemplateBody: templateBody
                    ),
                    new ReindexAst(
                        Source: src,
                        Destination: dst,
                        Body: null,
                        InjectOpTypeCreate: true,
                        UnsafeJustification: null
                    )
                };

                if ( aliasName is not null )
                {
                    children.Add( new AliasSwapAst(
                        Alias: aliasName,
                        OldIndex: src,
                        NewIndex: dst
                    ) );
                }

                return (StatementAst) new CompositeStatementAst(
                    CompositeVerb: "MIGRATE INDEX",
                    Children: children.ToArray()
                );
            } );

        // Top-level OneOf — order matters when prefixes overlap.
        // CREATE TEMPLATE/COMPONENT/POLICY are listed BEFORE CREATE INDEX so the
        // more-specific second keyword wins; same for DROP TEMPLATE/COMPONENT
        // before DROP INDEX. UPDATE MAPPING before UPDATE SETTINGS (both
        // UPDATE); WAIT FOR vs WAIT UNTIL (Parlot's OneOf tries left-to-right;
        // both first dispatch on `wait`). ALIAS SWAP/ADD/REMOVE all dispatch on
        // `alias` — order within is mutually-exclusive sub-verb keywords so
        // any order works.

        return OneOf(
            createTemplate,
            createComponent,
            createPolicy,
            createIndex,
            dropTemplate,
            dropComponent,
            dropIndex,
            updateMapping,
            updateSettings,
            refreshStmt,
            waitForHealth,
            waitUntilTask,
            reindexCore,
            aliasSwap,
            aliasAdd,
            aliasRemove,
            applyPolicy,
            migrateIndex
        );
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
