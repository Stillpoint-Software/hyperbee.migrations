#nullable enable
namespace Hyperbee.Migrations.Providers.OpenSearch.Internal.Dispatch;

public enum StatementOutcome
{
    Executed,    // statement dispatched and completed successfully
    Skipped,     // IF [NOT] EXISTS guard skipped the dispatch (no-op)
    Failed       // dispatch returned a non-success response
}

// Per-statement result returned by the dispatcher. Detail carries a
// human-readable summary for logging; OpenSearchResponseStatus records
// the cluster's HTTP status when applicable (null for skipped statements
// that never made a wire call).

public sealed record StatementResult(
    StatementOutcome Outcome,
    string Verb,
    string? Detail = null,
    int? OpenSearchResponseStatus = null,
    Exception? Exception = null
)
{
    public bool IsSuccess => Outcome != StatementOutcome.Failed;
}
