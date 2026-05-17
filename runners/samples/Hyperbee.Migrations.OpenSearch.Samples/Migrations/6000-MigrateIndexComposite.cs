using Hyperbee.Migrations.Providers.OpenSearch.Resources;

namespace Hyperbee.Migrations.OpenSearch.Samples.Migrations;

// Sample 6 (FEATURED): MIGRATE INDEX composite verb.
//
// THE CANONICAL ANSWER to "how do I apply a template/mapping change to
// existing data?" — template/mapping changes do NOT propagate to existing
// indices in OpenSearch. The composite verb makes the safe pattern
// (create new versioned index, reindex with op_type:create, atomic alias
// swap) the lazy path.
//
// This sample sets up:
//   1. The source index (sample_orders_v1) with the OLD shape
//   2. The alias the application reads through (sample_orders -> v1)
//   3. The index template that defines the NEW shape
// then executes:
//   4. MIGRATE INDEX sample_orders_v1 TO sample_orders_v2 WITH TEMPLATE
//      sample_orders_template VIA ALIAS sample_orders
//
// The composite expands at parse time to CREATE INDEX (body fetched at
// dispatch from the live template), REINDEX (with op_type:create
// auto-injected), ALIAS SWAP (in-body atomic precondition). Same
// end-state as the long-form sample 2; one verb. R-30 / ADR-0011 / ADR-0015.

[Migration( 6000 )]
public class MigrateIndexComposite( OpenSearchResourceRunner<MigrateIndexComposite> runner ) : Migration
{
    public override Task UpAsync( CancellationToken cancellationToken = default )
        => runner.StatementsFromAsync( "statements.pql", cancellationToken );
}
