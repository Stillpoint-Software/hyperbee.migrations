namespace Hyperbee.Migrations.Squash;

/// <summary>
/// Composite descriptor bundling all five squash components a provider must
/// supply (per ADR-0019 A11). Registered as a single unit at DI configuration
/// time; the framework refuses registration if any component is null or
/// throws <see cref="NotImplementedException"/> from a probe call.
/// </summary>
/// <remarks>
/// <para>
/// The five components — <see cref="TopologySignature"/>,
/// <see cref="DataOpClassifier"/>, <see cref="Generator"/>,
/// <see cref="Verifier"/>, and <see cref="Canonicalizer"/> — must agree on
/// <see cref="ITopologySignature.ProviderId"/>. The descriptor's
/// <see cref="EnsureValid"/> method enforces that invariant at construction.
/// </para>
/// <para>
/// Per ADR-0019: providers ship either a real composite (Postgres v1) or a
/// composite where <see cref="Generator"/> is a <see cref="NullSquashStrategy"/>
/// returning <see cref="SquashGenerationResult.Failed"/> with a roadmap-pointing
/// message (Aerospike, Couchbase, MongoDB, OpenSearch in v1).
/// </para>
/// </remarks>
public sealed record SquashStrategyDescriptor(
    ITopologySignature TopologySignature,
    IDataOpClassifier DataOpClassifier,
    ISquashStrategy Generator,
    ISquashVerifier Verifier,
    ISnapshotCanonicalizer Canonicalizer )
{
    /// <summary>
    /// Validates that all five components are non-null and agree on
    /// <see cref="ITopologySignature.ProviderId"/>. Throws
    /// <see cref="MigrationException"/> on any violation.
    /// </summary>
    public void EnsureValid()
    {
        if ( TopologySignature == null )
            throw new MigrationException( "SquashStrategyDescriptor.TopologySignature is null." );
        if ( DataOpClassifier == null )
            throw new MigrationException( "SquashStrategyDescriptor.DataOpClassifier is null." );
        if ( Generator == null )
            throw new MigrationException( "SquashStrategyDescriptor.Generator is null." );
        if ( Verifier == null )
            throw new MigrationException( "SquashStrategyDescriptor.Verifier is null." );
        if ( Canonicalizer == null )
            throw new MigrationException( "SquashStrategyDescriptor.Canonicalizer is null." );

        var providerId = TopologySignature.ProviderId;
        if ( !string.Equals( Generator.ProviderId, providerId, StringComparison.Ordinal ) )
            throw new MigrationException(
                $"SquashStrategyDescriptor.Generator provider id '{Generator.ProviderId}' " +
                $"does not match TopologySignature.ProviderId '{providerId}'." );
        if ( !string.Equals( Verifier.ProviderId, providerId, StringComparison.Ordinal ) )
            throw new MigrationException(
                $"SquashStrategyDescriptor.Verifier provider id '{Verifier.ProviderId}' " +
                $"does not match TopologySignature.ProviderId '{providerId}'." );
        if ( !string.Equals( Canonicalizer.ProviderId, providerId, StringComparison.Ordinal ) )
            throw new MigrationException(
                $"SquashStrategyDescriptor.Canonicalizer provider id '{Canonicalizer.ProviderId}' " +
                $"does not match TopologySignature.ProviderId '{providerId}'." );
    }
}
