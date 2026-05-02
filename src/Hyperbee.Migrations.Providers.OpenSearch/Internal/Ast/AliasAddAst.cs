#nullable enable
namespace Hyperbee.Migrations.Providers.OpenSearch.Internal.Ast;

// ALIAS ADD <alias> ON <idx>
//
// Single add action posted to /_aliases. If the alias already points at the
// target index, OpenSearch is idempotent (no error). If the alias points at
// a different index, the alias now points at BOTH (multi-target alias —
// supported but rarely the intent for migrations; authors who want exclusive
// pointing should use ALIAS SWAP).

public sealed record AliasAddAst(
    string Alias,
    string IndexName
) : StatementAst
{
    public override string Verb => "ALIAS ADD";
}
