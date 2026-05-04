#nullable enable
namespace Hyperbee.Migrations.Providers.OpenSearch.Internal.Ast;

// CREATE COMPONENT <name> WITH BODY $body
//
// Component template (PUT /_component_template/<name>). Reusable building
// blocks referenced by composable index templates via `composed_of`.

public sealed record CreateComponentAst(
    string ComponentName,
    BodySource? Body
) : StatementAst
{
    public override string Verb => "CREATE COMPONENT";
}
