namespace Hyperbee.Migrations.Squash.Cli;

/// <summary>
/// Per-provider extensibility contract for the <c>hyperbee-migrations squash</c>
/// CLI verb (per ADR-0024 + ADR-0019 audit follow-up Week 2). Each provider
/// package (<c>Hyperbee.Migrations.Providers.Postgres</c>,
/// <c>Hyperbee.Migrations.Providers.Aerospike</c>, etc.) implements this
/// interface so the CLI can dispatch by <c>--provider</c> value without
/// hardcoding a per-provider list. Discovery is a single reflection scan of
/// the migration assembly's reference closure (see
/// <see cref="CliProviderRegistry"/>) -- NuGet package presence IS the
/// registration.
/// </summary>
/// <remarks>
/// <para>
/// The provider OWNS the full squash-generation pipeline for its provider:
/// ephemeral fixture provisioning, migration apply through the requested
/// upper bound, snapshot capture, canonicalization, and content emission.
/// The CLI is a thin dispatch shell that holds operator-supplied arguments
/// (name, version, range, connection string, scan results) and routes them
/// to the right <see cref="ISquashCliProvider"/>.
/// </para>
/// <para>
/// <see cref="ScanSource"/> exposes the Roslyn-based source scanner that
/// enforces the <c>[DataMigration]</c> / <c>[StructuralOnly]</c> annotation
/// requirement (per ADR-0019 amendment A5). The shape is provider-specific
/// because each provider's data-op heuristics differ; the CLI walks the
/// returned verdicts and refuses generation when any subsumed class still
/// requires annotation.
/// </para>
/// <para>
/// <see cref="ProbeLastAppliedVersionAsync"/> is called per fleet manifest
/// entry by <see cref="FleetReadinessProbe"/>; the per-provider implementation
/// reads the operator-configured schema / table / index names from
/// <paramref name="providerOptions"/> rather than hardcoding defaults
/// (per RB-3).
/// </para>
/// </remarks>
public interface ISquashCliProvider
{
    /// <summary>
    /// Canonical lowercase provider identifier (e.g. <c>postgres</c>,
    /// <c>aerospike</c>, <c>mongodb</c>, <c>couchbase</c>, <c>opensearch</c>).
    /// The CLI's <c>--provider</c> argument is matched case-insensitively
    /// against this value.
    /// </summary>
    string ProviderId { get; }

    /// <summary>
    /// File extension (including the leading dot) for the emitted squash
    /// content: <c>.sql</c> for Postgres, <c>.statements</c> for the four
    /// NoSQL providers (per ADR-0022 script-form). Drives the squash output
    /// filename and replaces the prior hardcoded <c>.sql</c> assumption
    /// (per R-5).
    /// </summary>
    string SquashFileExtension { get; }

    /// <summary>
    /// Generates the squash artifact for the supplied context. The provider
    /// is responsible for the full pipeline: ephemeral fixture provision,
    /// migration apply via the discovered <see cref="IMigrationHost"/>,
    /// snapshot capture, canonicalization, and emission.
    /// </summary>
    Task<SquashGenerationResult> GenerateAsync(
        SquashCliContext context,
        CancellationToken cancellationToken );

    /// <summary>
    /// Runs the provider's source scanner against the supplied directory.
    /// Each verdict describes one migration class and whether it requires
    /// a <c>[DataMigration]</c> or <c>[StructuralOnly]</c> annotation.
    /// </summary>
    IReadOnlyList<MigrationSourceVerdict> ScanSource( string sourceDirectory );

    /// <summary>
    /// Probes the live (operator-supplied) connection for the highest
    /// applied migration version (per ADR-0019 A2 fleet readiness gate).
    /// Reads schema / table / collection / index names from
    /// <paramref name="providerOptions"/> rather than hardcoding defaults
    /// (per RB-3).
    /// </summary>
    /// <param name="liveConnectionString">Operator's fleet member connection string.</param>
    /// <param name="providerOptions">
    /// Optional per-environment overrides from the fleet manifest's
    /// <c>topology</c> section; provider decides which keys it honors
    /// (e.g. Postgres reads <c>schema-name</c> + <c>table-name</c>).
    /// </param>
    Task<long> ProbeLastAppliedVersionAsync(
        string liveConnectionString,
        IReadOnlyDictionary<string, string> providerOptions,
        CancellationToken cancellationToken );
}
