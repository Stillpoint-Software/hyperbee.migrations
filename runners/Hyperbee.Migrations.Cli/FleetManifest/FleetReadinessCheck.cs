using Hyperbee.Migrations.Squash;
using Npgsql;

namespace Hyperbee.Migrations.Cli.FleetManifest;

/// <summary>
/// Phase 7 Task 7.3 — pre-generation fleet readiness check. Probes each
/// registered environment in parallel for its last-applied version, then
/// invokes <see cref="SquashFleetGate.EnsureGenerable"/> to refuse if any
/// member is mid-range.
/// </summary>
/// <remarks>
/// <para>
/// Per A6 (PA-6 redesign): probing is parallel with bounded concurrency.
/// Per ADR-0019 the squash CLI must refuse generation while any registered
/// environment hasn't reached the squash's upper bound, lest the generated
/// squash carry an <c>ExpectedFleetVersions</c> entry that the deploy-time
/// gate then rejects with <see cref="StaleFleetMemberException"/>.
/// </para>
/// <para>
/// V1 supports Postgres only — the probe does a <c>SELECT</c> against the
/// migration ledger for the highest <c>record_id</c> matching the
/// <c>record.&lt;version&gt;.&lt;name&gt;</c> convention. Other providers
/// throw <see cref="NotSupportedException"/>; future versions plug in
/// per-provider probe implementations.
/// </para>
/// </remarks>
public static class FleetReadinessCheck
{
    private const int MaxParallelism = 8;

    /// <summary>
    /// Probes every fleet environment in parallel, returns each member's
    /// last-applied version, and raises <see cref="MidRangeFleetException"/>
    /// when any member falls strictly inside the proposed squash range.
    /// </summary>
    /// <returns>
    /// Per-environment last-applied version for every entry in the manifest.
    /// </returns>
    public static async Task<IReadOnlyDictionary<string, long>> EnsureGenerableAsync(
        FleetManifestModel manifest,
        long proposedFromVersion,
        long proposedToVersion,
        CancellationToken cancellationToken )
    {
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
                var lastApplied = await ProbeLastAppliedVersionAsync( env, ct ).ConfigureAwait( false );
                lock ( resultsLock )
                {
                    results[env.Name] = lastApplied;
                }
            } ).ConfigureAwait( false );

        var states = results.Select( kv => new FleetMemberState( kv.Key, kv.Value ) );
        SquashFleetGate.EnsureGenerable( proposedFromVersion, proposedToVersion, states );

        return results;
    }

    private static async Task<long> ProbeLastAppliedVersionAsync(
        FleetEnvironment env,
        CancellationToken cancellationToken )
    {
        // V1: Postgres-only probe. The migration table convention follows
        // DefaultMigrationConventions: record_id = "record.<version>.<name>".
        // We extract the leading numeric token after "record." to find the
        // highest applied version.
        await using var dataSource = NpgsqlDataSource.Create( env.Connection );
        await using var connection = await dataSource.OpenConnectionAsync( cancellationToken ).ConfigureAwait( false );

        // The schema/table name is operator-configurable but defaults to
        // public.migrations. For v1 we hard-code the default; an
        // env.topology.schema/table override is a v1.0.x extension.
        const string sql =
            "SELECT MAX(CAST(SPLIT_PART(record_id, '.', 2) AS bigint)) " +
            "FROM public.migrations " +
            "WHERE record_id ~ '^record\\.\\d+\\.'";

        try
        {
            await using var cmd = new NpgsqlCommand( sql, connection );
            var result = await cmd.ExecuteScalarAsync( cancellationToken ).ConfigureAwait( false );
            if ( result is null || result is DBNull )
                return 0; // empty ledger → fresh install
            return Convert.ToInt64( result, System.Globalization.CultureInfo.InvariantCulture );
        }
        catch ( Npgsql.PostgresException ex ) when ( ex.SqlState == "42P01" )
        {
            // ledger table doesn't exist yet — treat as empty
            return 0;
        }
        catch ( Exception ex )
        {
            throw new MigrationException(
                $"fleet readiness probe failed for environment `{env.Name}`: {ex.Message}", ex );
        }
    }
}
