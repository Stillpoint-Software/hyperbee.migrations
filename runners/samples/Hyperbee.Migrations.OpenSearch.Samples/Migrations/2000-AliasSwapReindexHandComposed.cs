using Hyperbee.Migrations.Providers.OpenSearch.Resources;

namespace Hyperbee.Migrations.OpenSearch.Samples.Migrations;

// Sample 2: hand-composed zero-downtime reindex-and-swap.
//
// Five statements: create source, attach alias, create destination, reindex,
// atomic alias swap. This is the LONG form — sample 6 (MIGRATE INDEX) is
// the recommended pattern that collapses to a single verb. Read both side
// by side: the long form makes each step inspectable; the composite makes
// the safe pattern the lazy path.
//
// The ALIAS SWAP atomicity guarantee (R-16, NF-2): the cluster receives a
// single _aliases body containing both the remove and add actions, so
// either the alias moves entirely from old to new or it doesn't move at
// all. Never a partial state where the alias resolves to both indices.

[Migration( 2000 )]
public class AliasSwapReindexHandComposed( OpenSearchResourceRunner<AliasSwapReindexHandComposed> runner ) : Migration
{
    public override Task UpAsync( CancellationToken cancellationToken = default )
        => runner.StatementsFromAsync( "statements.pql", cancellationToken );
}
