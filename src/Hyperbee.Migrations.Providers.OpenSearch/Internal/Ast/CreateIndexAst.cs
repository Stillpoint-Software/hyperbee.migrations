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
//
// Body sources are mutually exclusive:
//  - Body: sibling-property reference resolved offline by the resource runner
//    (per R-09)
//  - TemplateBody: index template reference resolved at dispatch time via
//    `GET /_index_template/<name>` (used by the MIGRATE INDEX composite
//    expansion per R-30; ADR-0015 keeps parsing offline-pure)

public sealed record CreateIndexAst(
    string IndexName,
    bool IfNotExists,
    BodySource? Body,
    bool InjectDynamicStrict,
    TemplateBodyRef? TemplateBody = null
) : StatementAst
{
    public override string Verb => "CREATE INDEX";
}
