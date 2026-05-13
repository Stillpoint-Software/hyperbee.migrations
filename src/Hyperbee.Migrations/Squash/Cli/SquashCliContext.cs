namespace Hyperbee.Migrations.Squash.Cli;

/// <summary>
/// Operator-supplied inputs for one squash-generation invocation, passed by
/// the CLI verb to <see cref="ISquashCliProvider.GenerateAsync"/>. Holds
/// every value the provider needs to provision an ephemeral fixture, apply
/// migrations through the requested upper bound, capture, and emit -- with
/// no provider-specific shape (provider-specific config lives in
/// <see cref="ProviderOptions"/>).
/// </summary>
public sealed record SquashCliContext
{
    public required string SquashName { get; init; }
    public required long FromVersion { get; init; }
    public required long ToVersion { get; init; }

    /// <summary>
    /// Live operator connection string. Used for the fleet-readiness probe
    /// against this environment plus any provider-specific bootstrapping
    /// the strategy needs (e.g. Couchbase live-bucket inspection). Not used
    /// to apply migrations -- those run against the ephemeral fixture.
    /// </summary>
    public required string ConnectionString { get; init; }

    /// <summary>
    /// Subsumed migration descriptors, ordered by version. The provider
    /// hands these to its <see cref="ISquashStrategy.GenerateAsync"/>.
    /// </summary>
    public required IReadOnlyList<MigrationDescriptor> Descriptors { get; init; }

    /// <summary>
    /// Discovered migration host (per ADR-0024 <see cref="IMigrationHost"/>),
    /// already activated by <see cref="MigrationHostDiscovery"/>. The provider
    /// calls <see cref="IMigrationHost.ConfigureAsync"/> against the ephemeral
    /// fixture's connection string to apply migrations.
    /// </summary>
    public required IMigrationHost MigrationHost { get; init; }

    /// <summary>Generation knobs (range bounds, etc.).</summary>
    public required SquashGenerationOptions Options { get; init; }

    /// <summary>
    /// Per-provider overrides (e.g. <c>aerospike-namespace</c>,
    /// <c>opensearch-base-url</c>). Provider decides which keys it honors.
    /// Empty when the operator passed nothing.
    /// </summary>
    public IReadOnlyDictionary<string, string> ProviderOptions { get; init; }
        = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
}
