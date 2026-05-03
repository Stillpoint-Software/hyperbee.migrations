#nullable enable
namespace Hyperbee.Migrations.Providers.OpenSearch.Internal.Ast;

// ALIAS REMOVE <alias> ON <idx>
//
// Single remove action. Fails (cluster returns 404 in actions[0].remove) if
// the alias does not currently point at the index — that's the desired
// safety: the runner doesn't silently no-op on a misconfigured remove.

public sealed record AliasRemoveAst(
    string Alias,
    string IndexName
) : StatementAst
{
    public override string Verb => "ALIAS REMOVE";
}
