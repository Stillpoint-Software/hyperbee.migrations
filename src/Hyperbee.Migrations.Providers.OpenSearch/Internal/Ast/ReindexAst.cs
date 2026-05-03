#nullable enable
namespace Hyperbee.Migrations.Providers.OpenSearch.Internal.Ast;

// REINDEX [UNSAFE("<reason>")] FROM <src> TO <dst> [WITH BODY $body]
//
// Safe-default flags resolved at parse:
//  - InjectOpTypeCreate: true unless UNSAFE("...") is present (which switches
//    to author-controlled re-write semantics per R-18). The runtime middleware
//    merges `op_type: create` into the request's `dest` object; if a body
//    explicitly sets a conflicting `op_type` value, the middleware throws a
//    SafeDefaultConflictException that surfaces before HTTP dispatch.
//  - UnsafeJustification: non-null only when InjectOpTypeCreate is false.
//    Phase 6 wires structured WARN logging on use.

public sealed record ReindexAst(
    string Source,
    string Destination,
    BodySource? Body,
    bool InjectOpTypeCreate,
    string? UnsafeJustification,
    string? NoWaitJustification = null
) : StatementAst
{
    public override string Verb => "REINDEX";
}
