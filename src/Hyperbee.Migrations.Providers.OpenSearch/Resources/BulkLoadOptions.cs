#nullable enable
namespace Hyperbee.Migrations.Providers.OpenSearch.Resources;

// R-20 — bulk-load tuning surface. Defaults match the requirement spec
// (8x parallelism, 5 retries, 1s starting backoff, RefreshOnCompleted=true).
//
// Spec note: R-20 calls for "5MB batches" but OpenSearch.Client's
// BulkAllDescriptor.Size accepts a document count, not a byte size. The
// default doc count below targets approximately 5MB at typical document
// shapes (~5KB per doc). Authors with very large or very small documents
// should override BatchSize explicitly.

public sealed class BulkLoadOptions
{
    /// <summary>
    /// Documents per bulk request. R-20 default: 1000 documents
    /// (approximately 5MB at typical document shapes — override for very
    /// large or very small documents).
    /// </summary>
    public int BatchSize { get; set; } = 1000;

    /// <summary>
    /// Concurrent in-flight bulk requests. R-20 default: 8x parallelism.
    /// Lower this on small clusters where 8 concurrent bulks trigger
    /// self-induced 429s (PA-6 from assessment 0002).
    /// </summary>
    public int MaxDegreeOfParallelism { get; set; } = 8;

    /// <summary>
    /// Number of retries on retriable failures (notably 429 throttles).
    /// R-20 default: 5 retries with exponential backoff.
    /// </summary>
    public int BackOffRetries { get; set; } = 5;

    /// <summary>
    /// Initial backoff duration; doubled on each retry. R-20 default: 1s
    /// (yielding 1s -> 2s -> 4s -> 8s -> 16s with the default 5 retries).
    /// </summary>
    public TimeSpan InitialBackOff { get; set; } = TimeSpan.FromSeconds( 1 );

    /// <summary>
    /// Whether to issue a single `_refresh` on the index once the bulk
    /// load completes. R-20 default: true. Per-batch refreshes are always
    /// disabled (refresh=false on each bulk request) — refreshing per
    /// batch under 8x parallelism is the documented anti-pattern that
    /// triggers segment-merge storms.
    /// </summary>
    public bool RefreshOnCompleted { get; set; } = true;
}
