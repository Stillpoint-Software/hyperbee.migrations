namespace Hyperbee.Migrations.Squash;

/// <summary>
/// Provider-supplied execution context for a squash generation invocation.
/// Carries the live database connection / cluster client, the resolved
/// migration assembly set, and any provider-specific runtime state the
/// strategy needs to capture snapshots and emit canonical content.
/// </summary>
/// <remarks>
/// This interface is intentionally minimal in the core; per-provider
/// generators downcast to a concrete context type
/// (e.g., <c>PostgresSquashGenerationContext</c>) that exposes
/// <c>NpgsqlConnection</c>, <c>ContainerHost</c>, etc. The framework
/// constructs the context once per generate request and passes it
/// through unchanged.
/// </remarks>
public interface ISquashGenerationContext
{
    /// <summary>Provider id matching the <see cref="ITopologySignature.ProviderId"/>.</summary>
    string ProviderId { get; }

    /// <summary>
    /// Logical migration name prefix (e.g. <c>"3000-CompactInitialSchema"</c>)
    /// used for the squash output's record id and resource discovery.
    /// Supplied by the CLI / caller.
    /// </summary>
    string SquashName { get; }

    /// <summary>Squash version that the new migration will declare via <c>[Migration]</c>.</summary>
    long SquashVersion { get; }
}
