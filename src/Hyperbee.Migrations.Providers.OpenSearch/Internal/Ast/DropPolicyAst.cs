#nullable enable
namespace Hyperbee.Migrations.Providers.OpenSearch.Internal.Ast;

// DROP POLICY <id> [IF EXISTS]
//
// Removes an ISM (Index State Management) policy definition. DELETE on
// _plugins/_ism/policies/<id> (or _opendistro/_ism/policies/<id> on legacy
// clusters - resolved at dispatch time by IsmEndpointCapability).
//
// The cluster rejects the delete if any index still has the policy attached
// (HTTP 409). Authors who need to delete a policy that's still attached must
// DETACH POLICY FROM INDEX first.

public sealed record DropPolicyAst(
    string PolicyId,
    bool IfExists
) : StatementAst
{
    public override string Verb => "DROP POLICY";
}
