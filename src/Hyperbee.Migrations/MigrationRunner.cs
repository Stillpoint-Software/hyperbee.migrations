using System.Diagnostics;
using System.Reflection;
using Hyperbee.Migrations.Helper;
using Microsoft.Extensions.Logging;

namespace Hyperbee.Migrations;

public class MigrationRunner
{
    private readonly IMigrationRecordStore _recordStore;
    private readonly MigrationOptions _options;
    private readonly ILogger<MigrationRunner> _logger;

    private record MigrationDescriptor( Type Type, MigrationAttribute Attribute, IReadOnlyList<long> ResolvedReplaces );

    public MigrationRunner( IMigrationRecordStore recordStore, MigrationOptions options, ILogger<MigrationRunner> logger )
    {
        _recordStore = recordStore ?? throw new ArgumentNullException( nameof( recordStore ) );
        _options = options ?? throw new ArgumentNullException( nameof( options ) );
        _logger = logger ?? throw new ArgumentNullException( nameof( logger ) );
    }

    public virtual async Task RunAsync( CancellationToken cancellationToken = default )
    {
        IDisposable syncLock = null;

        try
        {
            await _recordStore.InitializeAsync( cancellationToken );

            if ( _options.LockingEnabled )
                syncLock = await _recordStore.CreateLockAsync().ConfigureAwait( false );

            await RunMigrationsAsync( cancellationToken );
        }
        catch ( MigrationLockUnavailableException )
        {
            _logger.LogWarning( "The migration lock is unavailable. Skipping migrations." );
        }
        catch ( OperationCanceledException )
        {
            _logger.LogError( "The migration operation has been canceled. Application will stop." );
        }
        finally
        {
            syncLock?.Dispose();
        }
    }

    private async Task RunMigrationsAsync( CancellationToken cancellationToken )
    {
        var direction = _options.Direction;

        _logger.LogInformation( "Discovering {direction} migrations ...", direction );

        var migrations = DiscoverMigrations( _options ).ToList();

        // Pre-instantiate once: the recordId convention wants a Migration instance,
        // and we need (version → recordId) for squash reconciliation. Computing it
        // once avoids two instantiation passes (one for ledger probe, one for the
        // run loop) and keeps lifecycle visible to the activator.
        var instanceByVersion = new Dictionary<long, Migration>( migrations.Count );
        var recordIdByVersion = new Dictionary<long, string>( migrations.Count );
        foreach ( var d in migrations )
        {
            var instance = _options.MigrationActivator.CreateInstance( d.Type );
            instanceByVersion[d.Attribute!.Version] = instance;
            recordIdByVersion[d.Attribute.Version] = _options.Conventions.GetRecordId( instance );
        }

        // Classify ledger emptiness once at start (Phase 2 scaffolding); Phase 3
        // reconciliation refines per-squash classification below.
        var ledgerEmptyAtStart = await IsLedgerEmptyAsync( recordIdByVersion.Values, cancellationToken ).ConfigureAwait( false );
        var defaultApplyMode = ledgerEmptyAtStart ? MigrationApplyMode.Fresh : MigrationApplyMode.PartialCatchUp;

        var stopwatch = Stopwatch.StartNew();

        var runCount = 0;
        foreach ( var descriptor in migrations )
        {
            cancellationToken.ThrowIfCancellationRequested();

            var (type, attribute, resolvedReplaces) = descriptor;
            var version = attribute!.Version;
            var migration = instanceByVersion[version];
            var recordId = recordIdByVersion[version];
            var name = migration.GetType().Name;
            var hasCron = !string.IsNullOrEmpty( attribute.Cron );
            var isSquash = resolvedReplaces.Count > 0;

            // cron-based scheduling: check if due based on last run time

            if ( hasCron && direction == Direction.Up )
            {
                var record = await _recordStore.ReadAsync( recordId ).ConfigureAwait( false );

                if ( !MigrationCronHelper.IsDue( attribute.Cron, record?.RunOn ) )
                {
                    _logger.LogDebug( "[{version}] {name}: cron schedule not due, skipping", version, name );
                    continue;
                }
            }
            else
            {
                // standard one-time migration: skip if already recorded

                var exists = await _recordStore.ExistsAsync( recordId ).ConfigureAwait( false );

                switch ( direction )
                {
                    case Direction.Up when exists:
                    case Direction.Down when !exists:
                        continue;
                }
            }

            // Phase 3: squash reconciliation (Up-direction only; squashes are up-only
            // per ADR-0020). Inspect ledger state for replaced versions and branch:
            // Mature → auto-mark; Fresh → run UpAsync with ApplyMode.Fresh; partial
            // → MidRangeSquashException with recovery hints.
            if ( direction == Direction.Up && isSquash )
            {
                var (squashApplyMode, autoMark) = await ClassifySquashAsync(
                    descriptor, recordIdByVersion, cancellationToken ).ConfigureAwait( false );

                if ( autoMark )
                {
                    _logger.LogInformation(
                        "[{version}] {name}: squash auto-marked (mature env; {count} replaced versions all satisfied)",
                        version, name, resolvedReplaces.Count );
                    await WriteSquashJournalAsync( migration, attribute, recordId, resolvedReplaces, cancellationToken )
                        .ConfigureAwait( false );
                    runCount++;
                    if ( version == _options.ToVersion ) break;
                    continue;
                }

                _logger.LogInformation(
                    "[{version}] {name}: squash applying as {mode} (no prior ledger coverage of replaced versions)",
                    version, name, squashApplyMode );

                using ( MigrationContext.Push( new MigrationContext { ApplyMode = squashApplyMode } ) )
                    await migration.UpAsync( cancellationToken ).ConfigureAwait( false );

                await WriteSquashJournalAsync( migration, attribute, recordId, resolvedReplaces, cancellationToken )
                    .ConfigureAwait( false );

                runCount++;
                if ( version == _options.ToVersion ) break;
                continue;
            }

            // run the migration

            var migrationItem = new MigrationItem
            {
                Migration = migration,
                Direction = direction,
                Attribute = attribute,
                RecordId = recordId,
                CancellationToken = cancellationToken
            };

            if ( hasCron )
            {
                // cron migrations: execute once (non-blocking), update record

                _logger.LogInformation( "[{version}] {name}: {direction} migration started (cron)", version, name, direction );

                using ( MigrationContext.Push( new MigrationContext { ApplyMode = defaultApplyMode } ) )
                    await migrationItem.Migration.UpAsync( cancellationToken ).ConfigureAwait( false );

                // delete existing record before writing new one (upsert)
                if ( await _recordStore.ExistsAsync( recordId ).ConfigureAwait( false ) )
                    await _recordStore.DeleteAsync( recordId ).ConfigureAwait( false );

                await WriteJournalAsync( migration, attribute, recordId, cancellationToken ).ConfigureAwait( false );
            }
            else
            {
                // lifecycle migrations: loop until stop

                var stopProcess = false;

                using ( MigrationContext.Push( new MigrationContext { ApplyMode = defaultApplyMode } ) )
                {
                    while ( stopProcess == false )
                    {
                        if ( await StartMigration( migrationItem, cancellationToken ) )
                        {
                            _logger.LogInformation( "[{version}] {name}: {direction} migration started", version, name, direction );
                            stopProcess = await ProcessJobAsync( migrationItem, _recordStore, cancellationToken ).ConfigureAwait( false );
                        }

                        if ( stopProcess == false )
                        {
                            _logger.LogInformation( "[{version}] {name}: {direction} migration continuing", version, name, direction );
                        }
                    }
                }
            }

            runCount++;

            _logger.LogInformation( "[{version}] {name}: {direction} migration completed", version, name, direction );

            if ( version == _options.ToVersion )
                break;
        }

        stopwatch.Stop();
        _logger.LogInformation( "Executed {migrationCount} migrations in {elapsed}", runCount, stopwatch.Elapsed );
    }

    // Squash classification per ADR-0019. Returns (apply-mode, auto-mark) signal.
    // Raises MidRangeSquashException when the ledger covers a strict subset of
    // the squash's Replaces graph.
    //
    // Visible to the test assembly via InternalsVisibleTo so the three branches
    // (mature/fresh/midrange) can be exercised without weaving through full
    // RunAsync — Phase 2 discovery would reject some constructions otherwise.
    internal static async Task<(MigrationApplyMode mode, bool autoMark)> ClassifySquashAsync(
        IMigrationRecordStore store,
        long squashVersion,
        IReadOnlyList<long> resolvedReplaces,
        IReadOnlyDictionary<long, string> recordIdByVersion,
        CancellationToken cancellationToken )
    {
        // Direct satisfaction: Migration_<v> rows present in the ledger.
        var replacedRecordIds = resolvedReplaces.Select( v => recordIdByVersion[v] ).ToArray();
        var directlyApplied = await store
            .LoadAppliedVersionsAsync( replacedRecordIds, cancellationToken )
            .ConfigureAwait( false );

        // Transitive satisfaction: prior Squash row's Replaces array covers the version.
        var transitivelyCovered = await store
            .LoadSatisfyingRowsAsync( resolvedReplaces, cancellationToken )
            .ConfigureAwait( false );

        var satisfied = new HashSet<long>();
        foreach ( var v in resolvedReplaces )
        {
            if ( directlyApplied.Contains( recordIdByVersion[v] ) || transitivelyCovered.Contains( v ) )
                satisfied.Add( v );
        }

        if ( satisfied.Count == resolvedReplaces.Count )
            return (MigrationApplyMode.PartialCatchUp, autoMark: true);

        if ( satisfied.Count == 0 )
            return (MigrationApplyMode.Fresh, autoMark: false);

        var missing = resolvedReplaces.Where( v => !satisfied.Contains( v ) ).ToArray();
        throw new MidRangeSquashException(
            squashVersion,
            missing,
            satisfied.OrderBy( v => v ) );
    }

    private Task<(MigrationApplyMode mode, bool autoMark)> ClassifySquashAsync(
        MigrationDescriptor squash,
        IReadOnlyDictionary<long, string> recordIdByVersion,
        CancellationToken cancellationToken )
        => ClassifySquashAsync(
            _recordStore,
            squash.Attribute!.Version,
            squash.ResolvedReplaces,
            recordIdByVersion,
            cancellationToken );

    // Writes a squash row through the v3 record-bearing overload with
    // WritePrecondition.MustNotExist so concurrent runners reconciling the same
    // squash converge benignly (per WriteOutcome.AlreadyExistsBenign semantics).
    private async Task WriteSquashJournalAsync(
        Migration migration,
        MigrationAttribute attribute,
        string recordId,
        IReadOnlyList<long> replaces,
        CancellationToken cancellationToken )
    {
        var checksum = await _options.ChecksumStrategy
            .ComputeAsync( migration, attribute, cancellationToken )
            .ConfigureAwait( false );

        var record = new MigrationRecord
        {
            Id = recordId,
            RunOn = DateTimeOffset.UtcNow,
            Checksum = checksum,
            Kind = MigrationRecordKind.Squash,
            Replaces = replaces.ToArray()
        };

        var outcome = await _recordStore
            .WriteAsync( record, WritePrecondition.MustNotExist, cancellationToken )
            .ConfigureAwait( false );

        if ( outcome == WriteOutcome.PreconditionFailed )
            throw new MigrationException(
                $"Squash ledger write for `{recordId}` failed: an existing row's checksum does not match. " +
                "This indicates a definition drift — the squash type's checksum has changed since the row was first written. " +
                "Investigate the migration source vs. the ledger row before proceeding." );
    }

    private static IEnumerable<MigrationDescriptor> DiscoverMigrations( MigrationOptions options )
    {
        // raw discovery — collect all migration types with their attributes (no Replaces
        // resolution yet; we need the full version set first so ReplacesRange can resolve
        // and Replaces can be validated against it).
        var raw = options.Assemblies
            .SelectMany( assembly => assembly.GetTypes() )
            .Where( type => typeof( Migration ).IsAssignableFrom( type ) && !type.IsAbstract )
            .Select( type => (
                Type: type,
                Attribute: type.GetCustomAttribute( typeof( MigrationAttribute ) ) as MigrationAttribute
            ) )
            .Where( x => x.Attribute != null && DescriptorInScope( x.Attribute, options ) )
            .ToList();

        // throw if any duplicate versions (existing v2 contract)
        var seen = new HashSet<long>();
        foreach ( var (_, attr) in raw )
        {
            if ( !seen.Add( attr!.Version ) )
                throw new DuplicateMigrationException(
                    $"Migration number conflict detected for version number `{attr.Version}`.", attr.Version );
        }

        // resolve Replaces for each migration: union of explicit Replaces[] and
        // ReplacesRange-expanded set, intersected with the assembly's discovered
        // version set (per ADR-0019 Phase 2 semantics).
        var assemblyVersions = seen; // alias — `seen` now holds every version

        var descriptors = new List<MigrationDescriptor>( raw.Count );
        foreach ( var (type, attribute) in raw )
        {
            var resolved = ResolveReplaces( type, attribute!, assemblyVersions );
            descriptors.Add( new MigrationDescriptor( type, attribute, resolved ) );
        }

        return descriptors
            .OrderBy( x => x.Attribute!.Version, options.Direction )
            .ToList();
    }

    private static IReadOnlyList<long> ResolveReplaces(
        Type migrationType,
        MigrationAttribute attribute,
        HashSet<long> assemblyVersions )
    {
        var explicitSet = attribute.Replaces ?? Array.Empty<long>();
        var rangeSet = Helper.ReplacesRangeParser.Parse( attribute.ReplacesRange );

        if ( explicitSet.Length == 0 && rangeSet.Count == 0 )
            return Array.Empty<long>();

        // self-reference check
        var selfVersion = attribute.Version;
        if ( explicitSet.Contains( selfVersion ) || rangeSet.Contains( selfVersion ) )
            throw new MigrationLoadException(
                $"Migration `{migrationType.Name}` (version {selfVersion}) self-references its own version " +
                $"in Replaces / ReplacesRange. A squash cannot replace itself (ADR-0019)." );

        // union + dedupe
        var merged = new SortedSet<long>( explicitSet );
        foreach ( var v in rangeSet )
            merged.Add( v );

        // every named version must correspond to a discovered migration
        var missing = merged.Where( v => !assemblyVersions.Contains( v ) ).ToArray();
        if ( missing.Length > 0 )
            throw new MigrationLoadException(
                $"Migration `{migrationType.Name}` (version {selfVersion}) names version(s) " +
                $"[{string.Join( ", ", missing )}] in Replaces / ReplacesRange that do not correspond " +
                $"to any discovered [Migration] type. A squash's Replaces set must be a subset of " +
                $"the assembly's actual migration versions (ADR-0019)." );

        return merged.ToArray();
    }

    private async Task<bool> IsLedgerEmptyAsync(
        IEnumerable<string> candidateRecordIds,
        CancellationToken cancellationToken )
    {
        // A single bulk realtime query (LoadAppliedVersionsAsync) is the right
        // primitive here per ADR-0019; the per-id ExistsAsync loop in the DIM
        // default is a degraded fallback for v2 stores. Either way: if any
        // candidate id is in the ledger, we're not in a fresh-install state.
        var applied = await _recordStore
            .LoadAppliedVersionsAsync( candidateRecordIds, cancellationToken )
            .ConfigureAwait( false );
        return applied.Count == 0;
    }

    private static bool DescriptorInScope( MigrationAttribute attribute, MigrationOptions options )
    {
        if ( attribute == null ) // require the MigrationAttribute
            return false;

        // if no profile has been declared the migration is in-scope

        if ( !attribute.Profiles.Any() )
            return true;

        // the migration must belong to one of the specified profiles

        return options.Profiles
            .Intersect( attribute.Profiles, StringComparer.OrdinalIgnoreCase )
            .Any();
    }

    // Writes a journal row through the v3 record-bearing overload, computing the
    // checksum from the configured strategy. The legacy WriteAsync(string) DIM
    // default delegates back to the v2 store API for custom implementations,
    // preserving v2 semantics — see IMigrationRecordStore default impl notes.
    private async Task WriteJournalAsync(
        Migration migration,
        MigrationAttribute attribute,
        string recordId,
        CancellationToken cancellationToken )
    {
        var checksum = await _options.ChecksumStrategy
            .ComputeAsync( migration, attribute, cancellationToken )
            .ConfigureAwait( false );

        var record = new MigrationRecord
        {
            Id = recordId,
            RunOn = DateTimeOffset.UtcNow,
            Checksum = checksum,
            Kind = MigrationRecordKind.Migration
        };

        await _recordStore.WriteAsync( record, WritePrecondition.None, cancellationToken )
            .ConfigureAwait( false );
    }

    private async Task<bool> ProcessJobAsync( MigrationItem migrationItem, IMigrationRecordStore recordStore, CancellationToken cancellationToken )
    {
        switch ( migrationItem.Direction )
        {
            case Direction.Up:
                {
                    await migrationItem.Migration.UpAsync( cancellationToken ).ConfigureAwait( false );
                    var stopMigration = await StopMigration( migrationItem, cancellationToken );
                    if ( stopMigration )
                    {
                        if ( migrationItem.Attribute.Journal )
                            await WriteJournalAsync( migrationItem.Migration, migrationItem.Attribute, migrationItem.RecordId, cancellationToken ).ConfigureAwait( false );
                    }

                    return stopMigration;
                }
            case Direction.Down:
                {
                    await migrationItem.Migration.DownAsync( cancellationToken ).ConfigureAwait( false );
                    var stopMigration = await StopMigration( migrationItem, cancellationToken );
                    if ( stopMigration )
                    {
                        if ( migrationItem.Attribute.Journal )
                            await recordStore.DeleteAsync( migrationItem.RecordId ).ConfigureAwait( false );
                    }

                    return stopMigration;
                }
            default:
                throw new NullReferenceException();
        }
    }

    private async Task<bool> StartMigration( MigrationItem migrationItem, CancellationToken cancellationToken )
    {
        // prefer IContinuousMigration interface over reflection

        if ( migrationItem.Migration is IContinuousMigration continuous )
            return await continuous.StartAsync( cancellationToken );

        // fall back to string-based reflection

        if ( string.IsNullOrEmpty( migrationItem.Attribute.StartMethod ) )
            return true;

        var methodInfo = migrationItem.Migration.GetType().GetMethod( migrationItem.Attribute.StartMethod );

        if ( methodInfo != null && methodInfo.ReturnType == typeof( Task<bool> ) )
            return await (Task<bool>) methodInfo.Invoke( migrationItem.Migration, null )!;

        _logger.LogError( $"Method '{methodInfo?.Name}' not found or does not return a boolean or string." );
        return false;
    }

    private async Task<bool> StopMigration( MigrationItem migrationItem, CancellationToken cancellationToken )
    {
        // prefer IContinuousMigration interface over reflection

        if ( migrationItem.Migration is IContinuousMigration continuous )
            return await continuous.StopAsync( cancellationToken );

        // fall back to string-based reflection

        if ( string.IsNullOrEmpty( migrationItem.Attribute.StopMethod ) )
            return true;

        var methodInfo = migrationItem.Migration.GetType().GetMethod( migrationItem.Attribute.StopMethod );

        if ( methodInfo != null && methodInfo.ReturnType == typeof( Task<bool> ) )
            return await (Task<bool>) methodInfo.Invoke( migrationItem.Migration, null )!;

        _logger.LogError( $"Method '{methodInfo?.Name}' not found or does not return a boolean." );
        return false;
    }
}
