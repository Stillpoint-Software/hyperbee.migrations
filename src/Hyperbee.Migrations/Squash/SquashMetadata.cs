namespace Hyperbee.Migrations.Squash;

/// <summary>
/// Sidecar metadata emitted alongside a generated squash migration (per
/// ADR-0019 A2). Generation-time audit record persisted next to the
/// migration source.
/// </summary>
/// <remarks>
/// <para>
/// Persisted as JSON to <c>Squash_M.metadata.json</c>. It captures what
/// the fleet + topology looked like when the squash was generated. There
/// is no deploy-time fleet-staleness gate (cut per ADR-0026): the
/// generation-time gate (<see cref="MidRangeFleetException"/>) is the
/// enforced safety control, and a mid-range environment that reaches a
/// squash is refused loudly at apply time by the wired
/// <c>MigrationRunner</c> <c>MidRangeSquashException</c> path. The fields
/// below are retained for the audit trail and metadata-shape stability.
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
    /// Topology axes captured at generation time (per A14). Recorded as
    /// part of the generation audit trail; topology compatibility is
    /// enforced during squash generation/verification, not at deploy time.
    /// </summary>
    public required IReadOnlyDictionary<string, string> Topology { get; init; }

    /// <summary>
    /// Canonicalizer version (provider-id + semver) used at generation. Bumped
    /// when the canonicalizer's normalization rules change in a backward-
    /// incompatible way (per ADR-0019 C12 determinism gate).
    /// </summary>
    public required string CanonicalizerVersion { get; init; }

    /// <summary>
    /// Per-environment last-applied version snapshot, captured at
    /// generation time. Audit trail of what the fleet looked like when
    /// the squash was created; not consulted at deploy time (the
    /// deploy-time gate was cut per ADR-0026).
    /// </summary>
    public required IReadOnlyDictionary<string, long> ExpectedFleetVersions { get; init; }

    /// <summary>
    /// Retained field (default 30 days, originally per A15). The
    /// deploy-time staleness gate that consumed it was cut per ADR-0026;
    /// kept for metadata-shape stability and a possible future revival
    /// under a new ADR.
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
