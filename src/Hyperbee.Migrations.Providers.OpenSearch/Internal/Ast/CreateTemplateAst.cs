#nullable enable
namespace Hyperbee.Migrations.Providers.OpenSearch.Internal.Ast;

// CREATE TEMPLATE <name> WITH BODY $body
//
// Composable index template (PUT /_index_template/<name>). Idempotent; PUT
// replaces the existing template definition if present. The body uses
// OpenSearch's composable template shape including `index_patterns`,
// `template`, `composed_of`, `priority`, `version`, `_meta`.
//
// Note: PM-4's component-template-aware injection logic in
// SafeDefaultMergeMiddleware applies to CREATE INDEX bodies, not here —
// templates are MEANT to carry composed_of, so the `dynamic: strict`
// injection should NOT happen on this path.

public sealed record CreateTemplateAst(
    string TemplateName,
    BodyRef? Body
) : StatementAst
{
    public override string Verb => "CREATE TEMPLATE";
}
