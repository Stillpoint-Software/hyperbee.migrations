namespace Hyperbee.Migrations.Squash.Cli;

/// <summary>
/// Provisions an ephemeral cluster fixture for squash codegen (per ADR-0019
/// A18 + ADR-0024 Week 2). Implementations encapsulate the container
/// lifecycle (Testcontainers default; sibling-container variant for the
/// CI-inside-Docker case) so the per-provider squash capture orchestrator
/// is decoupled from how the container is acquired.
/// </summary>
/// <remarks>
/// <para>
/// Hints are provider-agnostic key/value pairs the operator (or default
/// configuration) supplies via <see cref="SquashCliContext.ProviderOptions"/>.
/// Provider-specific provisioners read the keys they care about (e.g.,
/// Postgres reads <c>server-major</c>; OpenSearch reads <c>image</c>).
/// Unknown keys are ignored.
/// </para>
/// <para>
/// Returned fixtures are <see cref="IAsyncDisposable"/>; the caller MUST
/// await disposal so the underlying container tears down (ADR-0019 A18).
/// </para>
/// </remarks>
public interface IEphemeralProvisioner
{
    /// <summary>
    /// Spin up an ephemeral cluster fixture matching the supplied hints.
    /// </summary>
    Task<IEphemeralFixture> ProvisionAsync(
        IReadOnlyDictionary<string, string> hints,
        CancellationToken cancellationToken );
}

/// <summary>
/// One ephemeral cluster, owned by the caller. Disposal tears down the
/// container. Provider-specific provisioners may return a derived fixture
/// type carrying additional metadata (management port, container handle)
/// that the provider's capture code casts to.
/// </summary>
public interface IEphemeralFixture : IAsyncDisposable
{
    /// <summary>
    /// Operator-visible connection string for the ephemeral cluster. The
    /// migration host configures itself with this value; the snapshot
    /// capture phase also opens its own client against it.
    /// </summary>
    string ConnectionString { get; }

    /// <summary>
    /// Free-form metadata about the fixture (e.g., <c>mgmt-port</c> for
    /// Couchbase, <c>hostname</c> + <c>port</c> for Aerospike). Provider-
    /// specific capture code reads what it needs; unknown keys are
    /// ignored.
    /// </summary>
    IReadOnlyDictionary<string, string> Metadata { get; }
}
