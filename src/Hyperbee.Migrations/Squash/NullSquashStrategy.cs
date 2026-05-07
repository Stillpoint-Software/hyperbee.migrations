namespace Hyperbee.Migrations.Squash;

/// <summary>
/// Stand-in <see cref="ISquashStrategy"/> for providers whose codegen is not
/// yet shipped (Aerospike, Couchbase, MongoDB, OpenSearch in v1).
/// <see cref="GenerateAsync"/> always returns
/// <see cref="SquashGenerationResult.Failed"/> with a message naming the
/// roadmap phase the operator should expect codegen in (per ADR-0019 A11).
/// </summary>
/// <remarks>
/// <para>
/// Per ADR-0019 amendment A11 the earlier <c>Unsupported</c> "hand-author"
/// guidance was removed. Operators who try to squash on a non-v1 provider
/// should get a clear refusal that points at the roadmap, not a vague
/// "you figure it out" hint.
/// </para>
/// <para>
/// Per ADR-0019 amendment A11 this strategy is shipped paired with the
/// provider's real <see cref="ITopologySignature"/>,
/// <see cref="IDataOpClassifier"/>, <see cref="ISquashVerifier"/>, and
/// <see cref="ISnapshotCanonicalizer"/> implementations; the descriptor's
/// validation layer ensures the composite is well-formed even when only
/// the generator is a no-op.
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
