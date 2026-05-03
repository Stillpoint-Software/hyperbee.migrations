#nullable enable
namespace Hyperbee.Migrations.Providers.OpenSearch.Internal.Ast;

public enum HealthStatus
{
    Yellow,
    Green
}

// WAIT FOR <green|yellow> [ON <idx>] [TIMEOUT <duration>]
//
// Explicit cluster-health wait per R-13. Distinct from R-12's implicit
// wait middleware — this verb runs as a standalone statement and does
// NOT inherit the per-environment threshold from R-29. Authors use it
// to assert stronger guarantees at specific points in a migration
// (e.g., "before alias-swapping, ensure GREEN").
//
// IndexName is optional: when present, scopes the wait to that index;
// when null, waits for cluster-wide status (avoids stalls on permanently-
// yellow plugin indices like .opendistro_security per NF-3).

public sealed record WaitForHealthAst(
    HealthStatus Threshold,
    string? IndexName,
    TimeSpan? Timeout
) : StatementAst
{
    public override string Verb => "WAIT FOR";
}
