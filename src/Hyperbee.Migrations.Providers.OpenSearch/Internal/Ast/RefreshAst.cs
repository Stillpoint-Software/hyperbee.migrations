#nullable enable
namespace Hyperbee.Migrations.Providers.OpenSearch.Internal.Ast;

// REFRESH <name>
//
// Synchronous refresh on an index (or wildcard pattern). Cheap; primarily
// useful between bulk loads and read-after-write tests. The implicit
// wait middleware (R-12) does NOT auto-refresh; this is an explicit verb.

public sealed record RefreshAst(
    string IndexName
) : StatementAst
{
    public override string Verb => "REFRESH";
}
