using System.Text.RegularExpressions;

namespace Hyperbee.Migrations.Spike.PostgresClassifier;

// Classify a Postgres statement (already split by PostgresStatementSplitter) into a
// typed record. Supports the universe of statement shapes that pg_dump --schema-only
// is expected to emit. Anything we don't recognize falls into Unknown — those are
// the hot spots the spike is trying to find.
//
// The classifier intentionally keeps things lightweight: it inspects the leading
// keywords (after stripping comments) and uses focused regexes per shape rather than
// a full parser. This is the same level of fidelity needed for v1 generation/diffing.

public enum PostgresStatementKind
{
    Unknown,
    SetParameter,
    CreateExtension,
    CreateSchema,
    CreateType,
    CreateDomain,
    CreateSequence,
    AlterSequence,
    CreateTable,
    CreateTablePartitionOf,
    AlterTable,
    AlterTableEnableRls,
    AlterTableAttachPartition,
    CreateIndex,
    CreateUniqueIndex,
    CreateView,
    CreateMaterializedView,
    CreateFunction,
    CreateProcedure,
    CreateTrigger,
    CreatePolicy,
    AlterPolicy,
    CreateRole,
    GrantRevoke,
    Comment,
    SelectPgCatalog
}

public sealed record ClassifiedStatement(
    PostgresStatementKind Kind,
    string? SchemaName,
    string? ObjectName,
    string Body,
    string? Detail = null );

public static class PostgresStatementClassifier
{
    private static readonly Regex CreateExtension = new(
        @"^CREATE\s+EXTENSION\s+(?:IF\s+NOT\s+EXISTS\s+)?(?<name>""[^""]+""|\w+)",
        RegexOptions.IgnoreCase | RegexOptions.Singleline );

    private static readonly Regex CreateSchema = new(
        @"^CREATE\s+SCHEMA\s+(?:IF\s+NOT\s+EXISTS\s+)?(?<name>""[^""]+""|\w+)",
        RegexOptions.IgnoreCase );

    // CREATE TABLE [IF NOT EXISTS] [schema.]name ... [PARTITION OF parent ...]
    private static readonly Regex CreateTable = new(
        @"^CREATE\s+(?:UNLOGGED\s+|TEMP(?:ORARY)?\s+)?TABLE(?:\s+IF\s+NOT\s+EXISTS)?\s+(?<schema>""[^""]+""|\w+)(?:\.(?<name>""[^""]+""|\w+))?",
        RegexOptions.IgnoreCase );

    private static readonly Regex IsPartitionOf = new(
        @"\bPARTITION\s+OF\b", RegexOptions.IgnoreCase );

    private static readonly Regex CreateIndex = new(
        @"^CREATE\s+(?<unique>UNIQUE\s+)?INDEX(?:\s+CONCURRENTLY)?(?:\s+IF\s+NOT\s+EXISTS)?\s+(?<name>""[^""]+""|\w+)",
        RegexOptions.IgnoreCase );

    private static readonly Regex CreateView = new(
        @"^CREATE\s+(?:OR\s+REPLACE\s+)?VIEW\s+(?<schema>""[^""]+""|\w+)(?:\.(?<name>""[^""]+""|\w+))?",
        RegexOptions.IgnoreCase );

    private static readonly Regex CreateMatView = new(
        @"^CREATE\s+MATERIALIZED\s+VIEW\s+(?:IF\s+NOT\s+EXISTS\s+)?(?<schema>""[^""]+""|\w+)(?:\.(?<name>""[^""]+""|\w+))?",
        RegexOptions.IgnoreCase );

    private static readonly Regex CreateType = new(
        @"^CREATE\s+TYPE\s+(?<schema>""[^""]+""|\w+)(?:\.(?<name>""[^""]+""|\w+))?",
        RegexOptions.IgnoreCase );

    private static readonly Regex CreateDomain = new(
        @"^CREATE\s+DOMAIN\s+(?<schema>""[^""]+""|\w+)(?:\.(?<name>""[^""]+""|\w+))?",
        RegexOptions.IgnoreCase );

    private static readonly Regex CreateSequence = new(
        @"^CREATE\s+SEQUENCE\s+(?:IF\s+NOT\s+EXISTS\s+)?(?<schema>""[^""]+""|\w+)(?:\.(?<name>""[^""]+""|\w+))?",
        RegexOptions.IgnoreCase );

    private static readonly Regex AlterSequence = new(
        @"^ALTER\s+SEQUENCE\s+(?<schema>""[^""]+""|\w+)(?:\.(?<name>""[^""]+""|\w+))?",
        RegexOptions.IgnoreCase );

    private static readonly Regex AlterTable = new(
        @"^ALTER\s+TABLE\s+(?:ONLY\s+)?(?<schema>""[^""]+""|\w+)(?:\.(?<name>""[^""]+""|\w+))?",
        RegexOptions.IgnoreCase );

    private static readonly Regex EnableRls = new(
        @"\bENABLE\s+ROW\s+LEVEL\s+SECURITY\b", RegexOptions.IgnoreCase );

    private static readonly Regex AttachPartition = new(
        @"\bATTACH\s+PARTITION\b", RegexOptions.IgnoreCase );

    private static readonly Regex CreateFunction = new(
        @"^CREATE\s+(?:OR\s+REPLACE\s+)?FUNCTION\s+(?<schema>""[^""]+""|\w+)(?:\.(?<name>""[^""]+""|\w+))?",
        RegexOptions.IgnoreCase );

    private static readonly Regex CreateProcedure = new(
        @"^CREATE\s+(?:OR\s+REPLACE\s+)?PROCEDURE\s+(?<schema>""[^""]+""|\w+)(?:\.(?<name>""[^""]+""|\w+))?",
        RegexOptions.IgnoreCase );

    private static readonly Regex CreateTrigger = new(
        @"^CREATE\s+(?:OR\s+REPLACE\s+)?(?:CONSTRAINT\s+)?TRIGGER\s+(?<name>""[^""]+""|\w+)",
        RegexOptions.IgnoreCase );

    private static readonly Regex CreatePolicy = new(
        @"^CREATE\s+POLICY\s+(?<name>""[^""]+""|\w+)\s+ON\s+(?<schema>""[^""]+""|\w+)(?:\.(?<table>""[^""]+""|\w+))?",
        RegexOptions.IgnoreCase );

    private static readonly Regex AlterPolicy = new(
        @"^ALTER\s+POLICY\s+(?<name>""[^""]+""|\w+)", RegexOptions.IgnoreCase );

    private static readonly Regex CommentOn = new(
        @"^COMMENT\s+ON\s+(?<kind>\w+(?:\s+\w+)?)\s+(?<rest>.*)$",
        RegexOptions.IgnoreCase | RegexOptions.Singleline );

    private static readonly Regex SetParam = new(
        @"^(?:SET|RESET)\b", RegexOptions.IgnoreCase );

    private static readonly Regex SelectPgCatalog = new(
        @"^SELECT\s+pg_catalog\.", RegexOptions.IgnoreCase );

    private static readonly Regex GrantRevoke = new(
        @"^(?:GRANT|REVOKE)\b", RegexOptions.IgnoreCase );

    private static readonly Regex CreateRole = new(
        @"^CREATE\s+(?:ROLE|USER|GROUP)\s+(?<name>""[^""]+""|\w+)", RegexOptions.IgnoreCase );

    public static ClassifiedStatement Classify( string statement )
    {
        ArgumentNullException.ThrowIfNull( statement );

        var head = StripLeadingNoise( statement );

        Match m;

        if ( SetParam.IsMatch( head ) )
            return new ClassifiedStatement( PostgresStatementKind.SetParameter, null, null, statement );

        if ( SelectPgCatalog.IsMatch( head ) )
            return new ClassifiedStatement( PostgresStatementKind.SelectPgCatalog, null, null, statement );

        if ( (m = CreateExtension.Match( head )).Success )
            return new ClassifiedStatement( PostgresStatementKind.CreateExtension, null, Unquote( m.Groups["name"].Value ), statement );

        if ( (m = CreateSchema.Match( head )).Success )
            return new ClassifiedStatement( PostgresStatementKind.CreateSchema, null, Unquote( m.Groups["name"].Value ), statement );

        if ( (m = CreateMatView.Match( head )).Success )
            return new ClassifiedStatement( PostgresStatementKind.CreateMaterializedView, GetSchema( m ), GetName( m ), statement );

        if ( (m = CreateView.Match( head )).Success )
            return new ClassifiedStatement( PostgresStatementKind.CreateView, GetSchema( m ), GetName( m ), statement );

        if ( (m = CreateTable.Match( head )).Success )
        {
            var kind = IsPartitionOf.IsMatch( head ) ? PostgresStatementKind.CreateTablePartitionOf : PostgresStatementKind.CreateTable;
            return new ClassifiedStatement( kind, GetSchema( m ), GetName( m ), statement );
        }

        if ( (m = CreateIndex.Match( head )).Success )
        {
            var kind = m.Groups["unique"].Success ? PostgresStatementKind.CreateUniqueIndex : PostgresStatementKind.CreateIndex;
            return new ClassifiedStatement( kind, null, Unquote( m.Groups["name"].Value ), statement );
        }

        if ( (m = CreateType.Match( head )).Success )
            return new ClassifiedStatement( PostgresStatementKind.CreateType, GetSchema( m ), GetName( m ), statement );

        if ( (m = CreateDomain.Match( head )).Success )
            return new ClassifiedStatement( PostgresStatementKind.CreateDomain, GetSchema( m ), GetName( m ), statement );

        if ( (m = CreateSequence.Match( head )).Success )
            return new ClassifiedStatement( PostgresStatementKind.CreateSequence, GetSchema( m ), GetName( m ), statement );

        if ( (m = AlterSequence.Match( head )).Success )
            return new ClassifiedStatement( PostgresStatementKind.AlterSequence, GetSchema( m ), GetName( m ), statement );

        if ( (m = AlterTable.Match( head )).Success )
        {
            PostgresStatementKind kind;
            if ( EnableRls.IsMatch( head ) )
                kind = PostgresStatementKind.AlterTableEnableRls;
            else if ( AttachPartition.IsMatch( head ) )
                kind = PostgresStatementKind.AlterTableAttachPartition;
            else
                kind = PostgresStatementKind.AlterTable;
            return new ClassifiedStatement( kind, GetSchema( m ), GetName( m ), statement );
        }

        if ( (m = CreateFunction.Match( head )).Success )
            return new ClassifiedStatement( PostgresStatementKind.CreateFunction, GetSchema( m ), GetName( m ), statement );

        if ( (m = CreateProcedure.Match( head )).Success )
            return new ClassifiedStatement( PostgresStatementKind.CreateProcedure, GetSchema( m ), GetName( m ), statement );

        if ( (m = CreateTrigger.Match( head )).Success )
            return new ClassifiedStatement( PostgresStatementKind.CreateTrigger, null, Unquote( m.Groups["name"].Value ), statement );

        if ( (m = CreatePolicy.Match( head )).Success )
            return new ClassifiedStatement(
                PostgresStatementKind.CreatePolicy,
                Unquote( m.Groups["schema"].Value ),
                Unquote( m.Groups["name"].Value ),
                statement,
                m.Groups["table"].Success ? Unquote( m.Groups["table"].Value ) : null );

        if ( (m = AlterPolicy.Match( head )).Success )
            return new ClassifiedStatement( PostgresStatementKind.AlterPolicy, null, Unquote( m.Groups["name"].Value ), statement );

        if ( (m = CommentOn.Match( head )).Success )
            return new ClassifiedStatement( PostgresStatementKind.Comment, null, m.Groups["kind"].Value.ToUpperInvariant(), statement );

        if ( GrantRevoke.IsMatch( head ) )
            return new ClassifiedStatement( PostgresStatementKind.GrantRevoke, null, null, statement );

        if ( (m = CreateRole.Match( head )).Success )
            return new ClassifiedStatement( PostgresStatementKind.CreateRole, null, Unquote( m.Groups["name"].Value ), statement );

        return new ClassifiedStatement( PostgresStatementKind.Unknown, null, null, statement );
    }

    private static string? GetSchema( Match m )
    {
        if ( !m.Groups["schema"].Success )
            return null;
        // when there's no `name` group, the captured "schema" is actually the bare object name
        return m.Groups["name"].Success ? Unquote( m.Groups["schema"].Value ) : null;
    }

    private static string? GetName( Match m )
    {
        if ( m.Groups["name"].Success )
            return Unquote( m.Groups["name"].Value );
        return m.Groups["schema"].Success ? Unquote( m.Groups["schema"].Value ) : null;
    }

    private static string Unquote( string s ) =>
        s.Length >= 2 && s[0] == '"' && s[^1] == '"' ? s[1..^1].Replace( "\"\"", "\"" ) : s;

    // Strip leading SQL whitespace + comments so the regex can match the first real keyword.
    private static string StripLeadingNoise( string s )
    {
        var i = 0;
        var n = s.Length;
        while ( i < n )
        {
            // whitespace
            while ( i < n && char.IsWhiteSpace( s[i] ) )
                i++;

            // line comment
            if ( i + 1 < n && s[i] == '-' && s[i + 1] == '-' )
            {
                while ( i < n && s[i] != '\n' )
                    i++;
                continue;
            }

            // block comment (with nesting)
            if ( i + 1 < n && s[i] == '/' && s[i + 1] == '*' )
            {
                i += 2;
                var depth = 1;
                while ( i < n && depth > 0 )
                {
                    if ( i + 1 < n && s[i] == '/' && s[i + 1] == '*' ) { i += 2; depth++; }
                    else if ( i + 1 < n && s[i] == '*' && s[i + 1] == '/' ) { i += 2; depth--; }
                    else i++;
                }
                continue;
            }

            break;
        }

        return s.Substring( i );
    }
}
