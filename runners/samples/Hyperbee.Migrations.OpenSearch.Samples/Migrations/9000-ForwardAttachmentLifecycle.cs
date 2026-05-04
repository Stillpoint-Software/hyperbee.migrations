using Hyperbee.Migrations.Providers.OpenSearch.Resources;

namespace Hyperbee.Migrations.OpenSearch.Samples.Migrations;

// Sample 9: forward-attachment lifecycle for greenfield pipelines.
//
// Contrast with sample 4 (IsmPolicyAndApply), which demonstrates the
// runtime APPLY POLICY path — necessary when you need to attach a policy
// to indices that ALREADY exist (backfill).
//
// For pipelines starting clean — daily rollover indices for a new
// application, fresh log streams, anything where the migration runs
// before the indices exist — declarative attachment is preferable. The
// migration installs only the cluster-level scaffolding:
//
//   CREATE COMPONENT  — shared settings/mappings, declared once.
//   CREATE TEMPLATE   — `index_patterns` matches the rollover series; the
//                       template's `template.aliases` block wires the
//                       alias automatically when a matching index is
//                       created.
//   CREATE POLICY     — the policy body's `ism_template.index_patterns`
//                       block attaches the policy to any matching index
//                       at creation time.
//
// Note: there is NO runtime APPLY POLICY and NO runtime ALIAS ADD. The
// first index in the series — created later by the application, by daily
// rollover, or by a successor migration — picks up everything: settings,
// mappings, alias, lifecycle policy.
//
// When to use this pattern vs. sample 4:
//
//   - greenfield series (no existing indices)        -> sample 9 pattern
//   - existing indices that need a new policy        -> sample 4 pattern
//   - new policy applies to BOTH existing and future -> both: sample 4
//                                                      pattern PLUS an
//                                                      `ism_template`
//                                                      block in the policy
//
// Caveat: `ism_template` inside an ISM policy body is the modern endpoint
// (`_plugins/_ism/policies`). Older AWS-managed clusters served by the
// legacy `_opendistro/_ism` endpoint may not recognize it; the bootstrap
// `IsmEndpointDetectStep` resolves which endpoint is active, but the
// declarative `ism_template` shape itself is a property of the modern
// schema. If you target a legacy endpoint, fall back to sample 4's
// runtime APPLY for forward attachment.

[Migration( 9000 )]
public class ForwardAttachmentLifecycle( OpenSearchResourceRunner<ForwardAttachmentLifecycle> runner ) : Migration
{
    public override Task UpAsync( CancellationToken cancellationToken = default )
        => runner.StatementsFromAsync( "statements.json", cancellationToken );
}
