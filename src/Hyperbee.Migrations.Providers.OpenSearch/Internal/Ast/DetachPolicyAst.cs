#nullable enable
namespace Hyperbee.Migrations.Providers.OpenSearch.Internal.Ast;

// DETACH POLICY FROM INDEX <index-pattern>
//
// Removes an ISM policy attachment from indices matching the pattern.
// POST on _plugins/_ism/remove/<pattern> (or _opendistro/_ism/remove/<pattern>
// on legacy clusters - resolved at dispatch time by IsmEndpointCapability).
//
// Counterpart to APPLY POLICY. Required before DROP POLICY can succeed when
// any index still references the policy. Also useful for migrating indices
// off a deprecated policy onto a successor.

public sealed record DetachPolicyAst(
    string IndexPattern,
    string? NoWaitJustification = null
) : StatementAst
{
    public override string Verb => "DETACH POLICY";
}
