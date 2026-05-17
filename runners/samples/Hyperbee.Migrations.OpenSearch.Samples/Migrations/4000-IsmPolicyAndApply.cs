using Hyperbee.Migrations.Providers.OpenSearch.Resources;

namespace Hyperbee.Migrations.OpenSearch.Samples.Migrations;

// Sample 4: ISM policy creation + attachment to existing indices.
//
// Two phases of policy management:
//  - CREATE POLICY uploads the policy definition to _plugins/_ism/policies.
//  - APPLY POLICY attaches the policy to existing indices matching a
//    pattern via _plugins/_ism/add. (For indices created in the future,
//    the policy's `ism_template.index_patterns` would auto-attach at
//    creation time.)
//
// The dispatcher inspects the apply response body and surfaces logical
// failures: ISM's add returns HTTP 200 even when zero indices match,
// so a `0 indices updated` response is mapped to Failed (not silent OK).

[Migration( 4000 )]
public class IsmPolicyAndApply( OpenSearchResourceRunner<IsmPolicyAndApply> runner ) : Migration
{
    public override Task UpAsync( CancellationToken cancellationToken = default )
        => runner.StatementsFromAsync( "statements.pql", cancellationToken );
}
