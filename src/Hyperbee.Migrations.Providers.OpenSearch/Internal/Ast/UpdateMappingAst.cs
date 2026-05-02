#nullable enable
namespace Hyperbee.Migrations.Providers.OpenSearch.Internal.Ast;

// UPDATE MAPPING ON <idx> WITH BODY $body
//
// Per R-08a: mapping update is additive-only; the SafeDefaultMergeMiddleware
// does not inject `dynamic: strict` here (that's a CREATE INDEX concern).
// R-18 syntactic detection (Phase 2) will reject bodies that attempt
// field-type changes or field removals — those operations require REINDEX
// via ALIAS SWAP, not UPDATE MAPPING.

public sealed record UpdateMappingAst(
    string IndexName,
    BodySource? Body
) : StatementAst
{
    public override string Verb => "UPDATE MAPPING";
}
