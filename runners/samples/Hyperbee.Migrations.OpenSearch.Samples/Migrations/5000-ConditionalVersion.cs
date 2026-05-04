using Hyperbee.Migrations.Providers.OpenSearch.Resources;

namespace Hyperbee.Migrations.OpenSearch.Samples.Migrations;

// Sample 5: WHEN VERSION conditional execution.
//
// Per R-15a, the wrapper uses semantic version comparison — '2.9' < '2.10'
// (which is FALSE under naive string comparison). The cluster version is
// fetched once per dispatcher (cached), so wrapping many statements has
// no extra HTTP cost.
//
// v1 supports MAJOR.MINOR[.PATCH]. -SNAPSHOT, -rc<N>, and AWS
// `OpenSearch_<x>` prefixes are rejected at parse time with a remediation
// message — partial-suffix support is worse than loud rejection.

[Migration( 5000 )]
public class ConditionalVersion( OpenSearchResourceRunner<ConditionalVersion> runner ) : Migration
{
    public override Task UpAsync( CancellationToken cancellationToken = default )
        => runner.StatementsFromAsync( "statements.json", cancellationToken );
}
