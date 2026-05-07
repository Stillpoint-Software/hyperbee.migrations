namespace Hyperbee.Migrations.Squash;

/// <summary>
/// Provider-supplied strategy that generates a squash migration from a
/// contiguous range of prior migrations. Per ADR-0019 the squash is
/// destructive — the generator captures the equivalent end state of the
/// replaced migrations and emits a single new migration whose <c>UpAsync</c>
/// recreates that state on a fresh install. The runner reconciles via
/// <c>Replaces</c> auto-mark on mature environments.
/// </summary>
/// <remarks>
/// Postgres ships a real <c>PgDumpSnapshotStrategy</c> in v1 (Phase 6).
/// Aerospike, Couchbase, MongoDB, and OpenSearch ship
/// <see cref="NullSquashStrategy"/> in v1 — calls return
/// <see cref="SquashGenerationResult.Failed"/> with a roadmap-pointing
/// message naming the per-provider phase (v1.1 / v1.2).
/// </remarks>
public interface ISquashStrategy
{
    /// <summary>Provider id matching the strategy's topology signature.</summary>
    string ProviderId { get; }

    /// <summary>
    /// Generate a squash migration from the supplied descriptor range. The
    /// strategy is responsible for: snapshotting equivalent state, classifying
    /// data ops vs structural ops via the provider's
    /// <see cref="IDataOpClassifier"/>, surfacing non-determinism diagnostics,
    /// and emitting canonical content per the C12 determinism gate.
    /// </summary>
    Task<SquashGenerationResult> GenerateAsync(
        ISquashGenerationContext context,
        IReadOnlyList<MigrationDescriptor> descriptors,
        SquashGenerationOptions options,
        CancellationToken cancellationToken = default );
}
