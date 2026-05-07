namespace Hyperbee.Migrations.Squash;

/// <summary>
/// Sidecar metadata emitted alongside a generated squash migration (per
/// ADR-0019 A2 + Phase 7 Task 7.4). Loaded by the runner at deploy time to
/// enforce the two-phase fleet readiness gate.
/// </summary>
/// <remarks>
/// <para>
/// Persisted as JSON to <c>Squash_M.metadata.json</c> next to the migration
/// source. The runner refuses deploy when the live environment is either:
/// <list type="bullet">
///   <item>not present in <see cref="ExpectedFleetVersions"/>
///         → <see cref="UnregisteredEnvironmentException"/>;</item>
///   <item>at a version below its recorded minimum AND the squash's
///         <see cref="MaxStalenessWindow"/> has elapsed since
///         <see cref="GeneratedAt"/>
///         → <see cref="StaleFleetMemberException"/>.</item>
/// </list>
/// </para>
/// </remarks>
public sealed record SquashMetadata
{
    /// <summary>
    /// Inclusive low-end of the version range this squash subsumes.
    /// </summary>
    public required long ReplacesFromVersion { get; init; }

    /// <summary>
    /// Inclusive high-end of the version range this squash subsumes.
    /// </summary>
    public required long ReplacesToVersion { get; init; }

    /// <summary>
    /// Provider id (matches <see cref="ITopologySignature.ProviderId"/>).
    /// </summary>
    public required string ProviderId { get; init; }

    /// <summary>
    /// Topology axes captured at generation time (per A14). The deploy-time
    /// gate matches this against the live environment's signature; mismatch
    /// is surfaced as a deploy-blocking diagnostic.
    /// </summary>
    public required IReadOnlyDictionary<string, string> Topology { get; init; }

    /// <summary>
    /// Canonicalizer version (provider-id + semver) used at generation. Bumped
    /// when the canonicalizer's normalization rules change in a backward-
    /// incompatible way (per ADR-0019 C12 determinism gate).
    /// </summary>
    public required string CanonicalizerVersion { get; init; }

    /// <summary>
    /// Per-environment minimum version: the squash refuses to deploy if the
    /// environment is not in this map (UnregisteredEnvironmentException) or
    /// if the env is below this minimum and outside the staleness window
    /// (StaleFleetMemberException).
    /// </summary>
    public required IReadOnlyDictionary<string, long> ExpectedFleetVersions { get; init; }

    /// <summary>
    /// Maximum acceptable staleness between when this squash was generated
    /// and when an environment that's below its <see cref="ExpectedFleetVersions"/>
    /// minimum can still deploy without raising
    /// <see cref="StaleFleetMemberException"/>. Default 30 days per A15.
    /// </summary>
    public TimeSpan MaxStalenessWindow { get; init; } = TimeSpan.FromDays( 30 );

    /// <summary>
    /// Squash-overrides block from the fleet manifest at generation time
    /// (per A9 structured fields). Empty in v1 if no overrides were declared.
    /// Carried forward so audits can see what stranding / topology-target
    /// overrides were active when the squash was generated.
    /// </summary>
    public IReadOnlyList<SquashOverrideEntry> SquashOverrides { get; init; } = Array.Empty<SquashOverrideEntry>();

    /// <summary>
    /// Tool version that emitted this squash (e.g.
    /// <c>"hyperbee-migrations/1.0.0"</c>).
    /// </summary>
    public required string CodegenToolVersion { get; init; }

    /// <summary>UTC instant the squash was generated.</summary>
    public required DateTimeOffset GeneratedAt { get; init; }
}

/// <summary>
/// One entry in the fleet manifest's <c>squash-overrides.accept-stranding</c>
/// per-env list. Per ADR-0019 A9 (structured override fields) and A15
/// (30-day default expiry).
/// </summary>
public sealed record SquashOverrideEntry
{
    public required string EnvironmentName { get; init; }
    public required string TicketId { get; init; }
    public required string Owner { get; init; }
    public required string Reason { get; init; }
    public required DateTimeOffset Expires { get; init; }

    /// <summary>True if <see cref="Expires"/> is in the past relative to <paramref name="now"/>.</summary>
    public bool IsExpired( DateTimeOffset now ) => now >= Expires;
}
