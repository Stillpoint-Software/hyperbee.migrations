#nullable enable
namespace Hyperbee.Migrations.Providers.OpenSearch.Internal.Ast;

// DROP COMPONENT <name> [IF EXISTS]
//
// Component templates can't be dropped if currently referenced by an index
// template — the cluster will return a 400 in that case. The provider
// surfaces the error verbatim; authors who want forced cleanup must drop
// the referencing index templates first.

public sealed record DropComponentAst(
    string ComponentName,
    bool IfExists
) : StatementAst
{
    public override string Verb => "DROP COMPONENT";
}
