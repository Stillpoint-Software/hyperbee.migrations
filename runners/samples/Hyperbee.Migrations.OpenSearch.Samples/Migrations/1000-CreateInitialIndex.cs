using Hyperbee.Migrations.Providers.OpenSearch.Resources;

namespace Hyperbee.Migrations.OpenSearch.Samples.Migrations;

// Sample 1: CREATE INDEX with body, REFRESH, WAIT FOR yellow.
//
// Demonstrates the simplest "create a fresh index with a known shape"
// pattern. Notes:
//  - WITH BODY $usersIndex resolves against the sibling JSON property
//    on the same statement object (ADR-0002 / R-09).
//  - The provider auto-injects `mappings.dynamic: strict` so unexpected
//    fields are rejected at write time (R-17). Authors who use composed_of
//    or set `dynamic` themselves opt out automatically.
//  - IF NOT EXISTS makes the migration idempotent against a manually-
//    pre-created destination — useful when authors are migrating an
//    existing cluster that already has the target shape.

[Migration( 1000 )]
public class CreateInitialIndex( OpenSearchResourceRunner<CreateInitialIndex> runner ) : Migration
{
    public override Task UpAsync( CancellationToken cancellationToken = default )
        => runner.StatementsFromAsync( "statements.json", cancellationToken );
}
