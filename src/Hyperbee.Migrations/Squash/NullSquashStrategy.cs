namespace Hyperbee.Migrations.Squash;

/// <summary>
/// Public extension-point <see cref="ISquashStrategy"/> for a provider that
/// registers a <see cref="SquashStrategyDescriptor"/> before its squash
/// codegen exists. <see cref="GenerateAsync"/> always returns
/// <see cref="SquashGenerationResult.Failed"/> with a message naming the
/// roadmap phase the operator should expect codegen in (per ADR-0019 A11).
/// </summary>
/// <remarks>
/// <para>
/// <b>No first-party provider uses this.</b> Per the all-5-providers
/// release rule every shipped provider (Postgres, Aerospike, Couchbase,
/// MongoDB, OpenSearch) ships a real <see cref="ISquashStrategy"/>. This
/// type is retained per ADR-0025 as a documented extension point for
/// future or third-party providers whose codegen is not yet implemented,
/// so the CLI fails loudly with a roadmap-pointing refusal rather than a
/// null / NotImplemented.
/// </para>
/// <para>
/// Per ADR-0019 amendment A11 the earlier <c>Unsupported</c> "hand-author"
/// guidance was removed. A consumer who tries to squash on a
/// codegen-less provider should get a clear refusal that points at the
/// roadmap, not a vague "you figure it out" hint.
/// </para>
/// <para>
/// Shipped paired with the provider's real
/// <see cref="ITopologySignature"/>, <see cref="IDataOpClassifier"/>,
/// <see cref="ISquashVerifier"/>, and <see cref="ISnapshotCanonicalizer"/>
/// implementations; the descriptor's validation layer ensures the
/// composite is well-formed even when only the generator is a no-op.
/// </para>
/// </remarks>
public sealed class NullSquashStrategy : ISquashStrategy
{
    public string ProviderId { get; }
    public string RoadmapPhase { get; }

    /// <param name="providerId">The provider this stub speaks for, e.g. <c>"mongodb"</c>.</param>
    /// <param name="roadmapPhase">The release line that will ship real codegen, e.g. <c>"v1.1"</c> or <c>"v1.2"</c>.</param>
    public NullSquashStrategy( string providerId, string roadmapPhase )
    {
        if ( string.IsNullOrWhiteSpace( providerId ) )
            throw new ArgumentException( "providerId is required.", nameof( providerId ) );
        if ( string.IsNullOrWhiteSpace( roadmapPhase ) )
            throw new ArgumentException( "roadmapPhase is required.", nameof( roadmapPhase ) );

        ProviderId = providerId;
        RoadmapPhase = roadmapPhase;
    }

    public Task<SquashGenerationResult> GenerateAsync(
        ISquashGenerationContext context,
        IReadOnlyList<MigrationDescriptor> descriptors,
        SquashGenerationOptions options,
        CancellationToken cancellationToken = default )
    {
        var detail =
            $"Squash codegen for `{ProviderId}` ships in {RoadmapPhase}; see release roadmap. " +
            "Current options: continue applying migrations individually.";

        return Task.FromResult<SquashGenerationResult>(
            new SquashGenerationResult.Failed( detail ) );
    }
}
