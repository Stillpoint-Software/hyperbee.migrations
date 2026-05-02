#nullable enable
namespace Hyperbee.Migrations.Providers.OpenSearch.Internal.Ast;

// CREATE POLICY <id> WITH BODY $body
//
// ISM (Index State Management) policy. PUT /_plugins/_ism/policies/<id>.
// The body wraps `policy.description`, `policy.default_state`,
// `policy.states`, `policy.ism_template` (for auto-attaching to indices
// matching a pattern at creation time).
//
// Per R-30 / Phase 2: APPLY POLICY (separate verb) attaches a policy to
// EXISTING indices via _plugins/_ism/add — `ism_template` only matches
// future indices.

public sealed record CreatePolicyAst(
    string PolicyId,
    BodySource? Body
) : StatementAst
{
    public override string Verb => "CREATE POLICY";
}
