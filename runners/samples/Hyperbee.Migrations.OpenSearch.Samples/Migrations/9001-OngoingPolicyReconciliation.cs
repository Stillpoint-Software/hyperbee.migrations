using Hyperbee.Migrations.Providers.OpenSearch.Resources;

namespace Hyperbee.Migrations.OpenSearch.Samples.Migrations;

// Sample 9.1: ongoing policy reconciliation.
//
// The third of three temporal scopes for ISM attachment. Pair it with
// sample 4 (one-time backfill via runtime APPLY) and sample 9
// (greenfield via `ism_template`).
//
// Why this exists. Sample 9 installs a policy whose body has an
// `ism_template.index_patterns` block — new indices in the
// `sample_app_events-*` series auto-attach at creation. But when the
// policy DEFINITION later evolves (a new state added, transition
// criteria adjusted, retention reduced from 90d to 30d), existing
// indices that are already attached keep running on their cached copy
// of the policy until something explicitly re-attaches them.
//
// This migration runs `APPLY POLICY` against the same wildcard pattern
// the policy's `ism_template` covers — and it is journaled = false so
// it re-runs on every startup. The ISM `change_policy` API is
// idempotent: indices already on the current policy are a no-op, so
// re-running is cheap. The wildcard form is correct because the set of
// indices to reconcile changes as new ones roll over and old ones are
// deleted by the policy's own delete state.
//
// When NOT to use this pattern.
//
//   - Greenfield-only series with policies that never change: sample 9
//     alone is enough. Don't add reconciliation noise on every startup
//     for a thing that's already convergent.
//   - One-time backfill of indices that exist before the policy:
//     sample 4 (a normal `[Migration(N)]`) is the right tool. Don't
//     reach for journaled = false unless the migration genuinely needs
//     to run more than once.
//   - Authoring-time-only enumeration of "these specific indices get
//     this policy": just put the literal set in a normal migration; the
//     wildcard story is for cluster-state-driven sets.

[Migration( 9001, journal: false )]
public class OngoingPolicyReconciliation( OpenSearchResourceRunner<OngoingPolicyReconciliation> runner ) : Migration
{
    public override Task UpAsync( CancellationToken cancellationToken = default )
        => runner.StatementsFromAsync( "statements.json", cancellationToken );
}
