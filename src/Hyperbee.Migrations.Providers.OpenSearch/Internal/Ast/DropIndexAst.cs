#nullable enable
namespace Hyperbee.Migrations.Providers.OpenSearch.Internal.Ast;

// DROP INDEX <name> [IF EXISTS]
//
// IfExists toggles the parser-time idempotency marker per R-14: when true,
// the runtime middleware MUST issue a HEAD probe before DELETE and no-op
// (with INFO log) when the index is absent.

public sealed record DropIndexAst(
    string IndexName,
    bool IfExists
) : StatementAst
{
    public override string Verb => "DROP INDEX";
}
