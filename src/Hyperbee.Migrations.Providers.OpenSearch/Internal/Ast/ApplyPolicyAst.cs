#nullable enable
namespace Hyperbee.Migrations.Providers.OpenSearch.Internal.Ast;

// APPLY POLICY <policy-id> TO <index-pattern>
//
// Attaches an ISM policy to existing indices matching the pattern.
// POST /_plugins/_ism/add/<pattern> with body `{ "policy_id": "<id>" }`.
//
// Existing indices are NOT covered by `ism_template` matching — that only
// applies at index creation. Authors who want to apply a policy to
// already-created indices use this verb.

public sealed record ApplyPolicyAst(
    string PolicyId,
    string IndexPattern,
    string? NoWaitJustification = null
) : StatementAst
{
    public override string Verb => "APPLY POLICY";
}
