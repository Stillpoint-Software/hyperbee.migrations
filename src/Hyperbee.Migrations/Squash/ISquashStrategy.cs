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
/// All five first-party providers (Postgres, Aerospike, Couchbase,
/// MongoDB, OpenSearch) ship a real <see cref="ISquashStrategy"/>
/// (per the all-5-providers release rule).
/// <see cref="NullSquashStrategy"/> is a retained public extension point
/// (ADR-0025) for future / third-party providers whose codegen is not
/// yet implemented -- calls return
/// <see cref="SquashGenerationResult.Failed"/> with a roadmap-pointing
/// message. No first-party provider uses it.
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
