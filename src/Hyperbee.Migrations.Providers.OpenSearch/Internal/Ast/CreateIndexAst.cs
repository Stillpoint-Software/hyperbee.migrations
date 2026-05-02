#nullable enable
namespace Hyperbee.Migrations.Providers.OpenSearch.Internal.Ast;

// CREATE INDEX <name> [IF NOT EXISTS] [WITH BODY $body]
//
// Safe-default flags resolved at parse:
//  - InjectDynamicStrict: true unless the verb is opt-out qualified (future).
//    The runtime middleware (SafeDefaultMergeMiddleware) honors this flag
//    AND skips injection if the resolved body contains `composed_of` (per R-17,
//    component-template-aware). Bodies with explicit `mappings.dynamic` are
//    preserved (user-explicit always wins).

public sealed record CreateIndexAst(
    string IndexName,
    bool IfNotExists,
    BodyRef? Body,
    bool InjectDynamicStrict
) : StatementAst
{
    public override string Verb => "CREATE INDEX";
}
