using Hyperbee.Migrations.Providers.OpenSearch.Resources;

namespace Hyperbee.Migrations.OpenSearch.Samples.Migrations;

// Sample 8: REINDEX UNSAFE("...") — the explicit-justification idiom.
//
// By default REINDEX auto-injects op_type: create so a retried reindex
// doesn't silently overwrite documents that succeeded on the first run
// (R-08a / NF-7). Authors who genuinely need overwrite semantics — usually
// because they're seeding into a known-empty destination — opt out via
// the UNSAFE("<reason>") modifier with a NON-EMPTY justification.
//
// The justification is a high-signal grep target for PR review and
// incident postmortems. Bare `UNSAFE` (no parentheses, no string) fails
// at parse time. The provider also emits a structured WARN log
// `migration.unsafe_bypass{reason, statementIdx, ...}` on every bypass
// so it's auditable in production telemetry.
//
// Operations that may require UNSAFE in v1 (per R-18 syntactic enumeration):
//   - REINDEX UNSAFE("<reason>") FROM ... TO ...   (skips op_type:create)
//
// NO WAIT("<reason>") is documented but not yet implemented; lands in a
// later slice alongside WaitMode.PerMigration.

[Migration( 8000 )]
public class UnsafeReindex( OpenSearchResourceRunner<UnsafeReindex> runner ) : Migration
{
    public override Task UpAsync( CancellationToken cancellationToken = default )
        => runner.StatementsFromAsync( "statements.json", cancellationToken );
}
