#nullable enable
namespace Hyperbee.Migrations.Providers.OpenSearch.Internal.Ast;

// DROP TEMPLATE <name> [IF EXISTS]

public sealed record DropTemplateAst(
    string TemplateName,
    bool IfExists
) : StatementAst
{
    public override string Verb => "DROP TEMPLATE";
}
