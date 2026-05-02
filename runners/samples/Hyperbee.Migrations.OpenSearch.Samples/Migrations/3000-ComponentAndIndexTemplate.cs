using Hyperbee.Migrations.Providers.OpenSearch.Resources;

namespace Hyperbee.Migrations.OpenSearch.Samples.Migrations;

// Sample 3: component template + composable index template.
//
// The OpenSearch composition pattern: factor out reusable mapping/setting
// fragments into component templates (CREATE COMPONENT) and reference them
// from index templates via composed_of (CREATE TEMPLATE). When a new index
// matches the template's index_patterns, the cluster merges components
// in order, then the template's own template block, then any explicit
// settings on the create call.
//
// Note: the dispatcher detects composed_of on a CREATE TEMPLATE and the
// MIGRATE INDEX path skips dynamic:strict injection on the resolved body
// (R-17 component-template-aware refinement) — the components are
// expected to declare their own dynamic semantics.

[Migration( 3000 )]
public class ComponentAndIndexTemplate( OpenSearchResourceRunner<ComponentAndIndexTemplate> runner ) : Migration
{
    public override Task UpAsync( CancellationToken cancellationToken = default )
        => runner.StatementsFromAsync( "statements.json", cancellationToken );
}
