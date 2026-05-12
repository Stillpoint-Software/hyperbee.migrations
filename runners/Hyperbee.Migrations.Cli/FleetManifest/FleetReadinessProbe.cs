using Hyperbee.Migrations.Squash;
using Hyperbee.Migrations.Squash.Cli;

namespace Hyperbee.Migrations.Cli.FleetManifest;

/// <summary>
/// Provider-agnostic fleet readiness probe (per ADR-0024 audit Week 2).
/// Walks every environment in the manifest in parallel, dispatches the
/// last-applied-version probe to the supplied <see cref="ISquashCliProvider"/>,
/// then invokes <see cref="SquashFleetGate.EnsureGenerable"/> to refuse if
/// any member is mid-range. Replaces the v1 Postgres-only
/// <c>FleetReadinessCheck</c> -- RB-3 + per-provider dispatch are folded in
/// here.
/// </summary>
public static class FleetReadinessProbe
{
    private const int MaxParallelism = 8;

    public static async Task<IReadOnlyDictionary<string, long>> EnsureGenerableAsync(
        ISquashCliProvider cliProvider,
        FleetManifestModel manifest,
        long proposedFromVersion,
        long proposedToVersion,
        CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( cliProvider );
        ArgumentNullException.ThrowIfNull( manifest );
        if ( manifest.Fleet.Count == 0 )
            throw new MigrationException( "fleet manifest has no environments to probe." );

        var results = new Dictionary<string, long>( StringComparer.OrdinalIgnoreCase );
        var resultsLock = new object();

        await Parallel.ForEachAsync(
            manifest.Fleet,
            new ParallelOptions
            {
                MaxDegreeOfParallelism = MaxParallelism,
                CancellationToken = cancellationToken
            },
            async ( env, ct ) =>
            {
                long lastApplied;
                try
                {
                    lastApplied = await cliProvider.ProbeLastAppliedVersionAsync(
                        env.Connection,
                        env.Topology,
                        ct ).ConfigureAwait( false );
                }
                catch ( Exception ex )
                {
                    throw new MigrationException(
                        $"fleet readiness probe failed for environment `{env.Name}`: {ex.Message}", ex );
                }
                lock ( resultsLock )
                {
                    results[env.Name] = lastApplied;
                }
            } ).ConfigureAwait( false );

        var states = results.Select( kv => new FleetMemberState( kv.Key, kv.Value ) );
        SquashFleetGate.EnsureGenerable( proposedFromVersion, proposedToVersion, states );

        return results;
    }
}
