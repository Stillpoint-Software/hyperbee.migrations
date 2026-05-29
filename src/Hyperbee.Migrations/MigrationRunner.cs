using System.Diagnostics;
using System.Reflection;
using Hyperbee.Migrations.Helper;
using Microsoft.Extensions.Logging;

namespace Hyperbee.Migrations;

public class MigrationRunner
{
    private readonly IMigrationRecordStore _recordStore;
    private readonly MigrationOptions _options;
    private readonly ILogger _logger;

    // Primary constructor: accepts an ILoggerFactory so subclass instances log
    // under their concrete runtime type (per ADR-0023). The factory creates a
    // category-named logger for the most-derived type at construction time.
    public MigrationRunner( IMigrationRecordStore recordStore, MigrationOptions options, ILoggerFactory loggerFactory )
    {
        _recordStore = recordStore ?? throw new ArgumentNullException( nameof( recordStore ) );
        _options = options ?? throw new ArgumentNullException( nameof( options ) );
        if ( loggerFactory == null )
            throw new ArgumentNullException( nameof( loggerFactory ) );
        _logger = loggerFactory.CreateLogger( GetType() );
    }

    // Back-compat constructor: existing call sites that pass ILogger<MigrationRunner>
    // continue to compile. Subclass instances should prefer the ILoggerFactory
    // overload so logs are categorized under the runtime type.
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

        // R-2: snapshot the applied set once at start with a single bulk realtime
        // read. The loop below consults this snapshot via `appliedAtStart.Contains`
        // instead of issuing a per-migration `ExistsAsync` round-trip. Avoids the
        // N-RTT-while-holding-the-fleet-lock perf+correctness hazard the audit
        // flagged (500-migration projects formerly serialized the fleet for
        // 500 round trips of nothing-but-existence-probes).
        //
        // Up direction: the snapshot is correct for every step because Up only
        // ADDS records; nothing in the loop removes one. Down direction: Down
        // only REMOVES records, so the snapshot may report a record as present
        // after the loop already deleted it -- harmless because in Down we want
        // to run only the migrations that exist (Direction.Down when !exists ->
        // continue), and the snapshot's "exists" answer is the value at start.
        // Cron migrations bypass the snapshot via the ReadAsync path that
        // inspects RunOn directly.
        var appliedAtStart = await _recordStore
            .IntersectWithAppliedAsync( recordIdByVersion.Values, cancellationToken )
            .ConfigureAwait( false );
        var defaultApplyMode = appliedAtStart.Count == 0 ? MigrationApplyMode.Fresh : MigrationApplyMode.PartialCatchUp;

        // ADR-0027 Tier 1 — interruption pre-scan. A leftover in-flight sentinel
        // means a migration body started on a prior invocation but never wrote its
        // journal row (SIGTERM / SIGKILL / node death). Detect those before the run
        // loop and apply the fail-closed policy. Up-direction only (sentinels are
        // an up-path mechanism). Runs inside the migration lock, so it is atomic
        // against another runner (the guarantee depends on LockingEnabled).
        if ( direction == Direction.Up )
            await ScanForInterruptedMigrationsAsync( migrations, recordIdByVersion, appliedAtStart, cancellationToken )
                .ConfigureAwait( false );

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
                // standard one-time migration: skip if already recorded.
                // R-2: consult the start-of-run applied snapshot rather than
                // probing the store per migration.
                var exists = appliedAtStart.Contains( recordId );

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
                var (squashApplyMode, autoMark, recoveryRowToDelete) = await ClassifySquashAsync(
                    descriptor, recordIdByVersion, cancellationToken ).ConfigureAwait( false );

                if ( autoMark )
                {
                    if ( recoveryRowToDelete != null )
                    {
                        _logger.LogWarning(
                            "[{version}] {name}: squash AUTO-MARKED via persisted recovery acknowledgement (RB-2). " +
                            "Replaced versions ({count}) treated as satisfied without running the squash body. " +
                            "This path is only safe when the live data state already matches the squashed schema; " +
                            "the operator who ran `hyperbee-migrations recover from-mid-range` owns that verification.",
                            version, name, resolvedReplaces.Count );
                    }
                    else
                    {
                        _logger.LogInformation(
                            "[{version}] {name}: squash auto-marked (mature env; {count} replaced versions all satisfied)",
                            version, name, resolvedReplaces.Count );
                    }

                    await WriteSquashJournalAsync( migration, attribute, recordId, resolvedReplaces, cancellationToken )
                        .ConfigureAwait( false );

                    // RB-2: delete the persisted recovery acknowledgement AFTER the
                    // squash journal write commits, so a crash between the two leaves
                    // the recovery row consumable by the next runner pass rather than
                    // losing the operator's acknowledgement. Best-effort: a stale
                    // recovery row left behind is harmless because the next pass
                    // finds a fully-applied squash and skips classification.
                    if ( recoveryRowToDelete != null )
                    {
                        try
                        {
                            await _recordStore.DeleteAsync( recoveryRowToDelete ).ConfigureAwait( false );
                            _logger.LogInformation(
                                "[{version}] {name}: consumed recovery acknowledgement row `{recoveryId}`",
                                version, name, recoveryRowToDelete );
                        }
                        catch ( Exception ex )
                        {
                            _logger.LogWarning( ex,
                                "[{version}] {name}: squash journal written but recovery row `{recoveryId}` " +
                                "could not be deleted; harmless (next runner pass will skip classification)",
                                version, name, recoveryRowToDelete );
                        }
                    }

                    runCount++;
                    if ( version == _options.ToVersion ) break;
                    continue;
                }

                _logger.LogInformation(
                    "[{version}] {name}: squash applying as {mode} (no prior ledger coverage of replaced versions)",
                    version, name, squashApplyMode );

                // ADR-0027: the squash body mutates state; bracket it with a sentinel.
                await WriteSentinelAsync( recordId, cancellationToken ).ConfigureAwait( false );

                using ( MigrationContext.Push( new MigrationContext { ApplyMode = squashApplyMode } ) )
                    await migration.UpAsync( cancellationToken ).ConfigureAwait( false );

                await WriteSquashJournalAsync( migration, attribute, recordId, resolvedReplaces, cancellationToken )
                    .ConfigureAwait( false );

                await DeleteSentinelAsync( recordId ).ConfigureAwait( false );

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

                // ADR-0027: bracket the body with an in-flight sentinel for
                // Up-direction journaled migrations (those subject to the
                // fail-closed pre-scan). Down is not covered (v1 scope); cron and
                // non-journaled migrations re-run by design and are exempt.
                var useSentinel = direction == Direction.Up && attribute.Journal;
                if ( useSentinel )
                    await WriteSentinelAsync( recordId, cancellationToken ).ConfigureAwait( false );

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

                // Reached only on successful completion (the journal row is now
                // written); an interruption inside the loop throws past this and
                // leaves the sentinel behind for the next run's pre-scan.
                if ( useSentinel )
                    await DeleteSentinelAsync( recordId ).ConfigureAwait( false );
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
    // The third tuple element carries a recovery acknowledgement row id when
    // RB-2 force-mark fired; non-null tells the caller to delete that row
    // after the squash journal write succeeds. Defaults to null in every
    // path except RB-2 auto-mark.
    internal static async Task<(MigrationApplyMode mode, bool autoMark, string recoveryRowToDelete)> ClassifySquashAsync(
        IMigrationRecordStore store,
        long squashVersion,
        IReadOnlyList<long> resolvedReplaces,
        IReadOnlyDictionary<long, string> recordIdByVersion,
        CancellationToken cancellationToken,
        string environmentName = null )
    {
        // Direct satisfaction: Migration_<v> rows present in the ledger.
        var replacedRecordIds = resolvedReplaces.Select( v => recordIdByVersion[v] ).ToArray();
        var directlyApplied = await store
            .IntersectWithAppliedAsync( replacedRecordIds, cancellationToken )
            .ConfigureAwait( false );

        // Transitive satisfaction: prior Squash row's Replaces array covers the version.
        var transitivelyCovered = await store
            .IntersectWithSquashedAsync( resolvedReplaces, cancellationToken )
            .ConfigureAwait( false );

        var satisfied = new HashSet<long>();
        foreach ( var v in resolvedReplaces )
        {
            if ( directlyApplied.Contains( recordIdByVersion[v] ) || transitivelyCovered.Contains( v ) )
                satisfied.Add( v );
        }

        if ( satisfied.Count == resolvedReplaces.Count )
            return (MigrationApplyMode.PartialCatchUp, autoMark: true, recoveryRowToDelete: null);

        if ( satisfied.Count == 0 )
            return (MigrationApplyMode.Fresh, autoMark: false, recoveryRowToDelete: null);

        var missing = resolvedReplaces.Where( v => !satisfied.Contains( v ) ).ToArray();

        // RB-2: before failing the runner, look for a persisted recovery
        // acknowledgement at the deterministic row id. The CLI verb
        // `recover from-mid-range` writes this row after the operator
        // externally verifies that the live data state already matches the
        // squashed schema. If we find a valid row (token matches a fresh
        // recompute AND the persisted missing-set matches what we just
        // classified), the squash auto-marks WITHOUT running its body and
        // the recovery row is deleted -- so a stale acknowledgement cannot
        // force-mark a second time.
        if ( !string.IsNullOrWhiteSpace( environmentName ) )
        {
            var recoveryId = Squash.RecoveryRecord.IdFor( squashVersion, environmentName );
            MigrationRecord recoveryRow = null;
            try
            {
                recoveryRow = await store.ReadAsync( recoveryId ).ConfigureAwait( false );
            }
            catch
            {
                // ReadAsync surface varies by provider; tolerate any failure
                // (treat as no recovery row). The MidRangeSquashException
                // below still surfaces with the actionable token.
            }

            if ( recoveryRow != null
                 && Squash.RecoveryRecord.IsValidFor( recoveryRow, squashVersion, environmentName, missing ) )
            {
                // Auto-mark + clean up recovery row. The recovery row id is
                // returned so RunAsync can delete it AFTER the squash journal
                // write commits: that ordering means a crash between the two
                // leaves the recovery row consumable by a retry rather than
                // losing the operator's acknowledgement. The delete is
                // best-effort; a stale recovery row left behind is harmless
                // because the next runner pass finds a fully-applied squash
                // and skips classification entirely.
                return (MigrationApplyMode.PartialCatchUp, autoMark: true, recoveryRowToDelete: recoveryId);
            }
        }

        // R-10: surface the recovery acknowledgement token in the exception
        // message so the operator has it during incident response. Token is
        // deterministic per (env, squash, missing-versions); see
        // RecoveryAcknowledgement.
        throw new MidRangeSquashException(
            squashVersion,
            missing,
            satisfied.OrderBy( v => v ),
            environmentName );
    }

    private Task<(MigrationApplyMode mode, bool autoMark, string recoveryRowToDelete)> ClassifySquashAsync(
        MigrationDescriptor squash,
        IReadOnlyDictionary<long, string> recordIdByVersion,
        CancellationToken cancellationToken )
        => ClassifySquashAsync(
            _recordStore,
            squash.Attribute!.Version,
            squash.ResolvedReplaces,
            recordIdByVersion,
            cancellationToken,
            _options.EnvironmentName );

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
    // ADR-0027 Tier 1 — in-flight sentinel lifecycle. The sentinel is written
    // immediately before a migration body runs and deleted only after the real
    // journal row commits (write-real-row-then-delete-sentinel ordering, symmetric
    // with the recovery-row delete-after-journal). If the body throws (e.g.
    // interruption), the delete is skipped on purpose so the sentinel survives as
    // the durable interruption signal for the next run's pre-scan.
    private Task WriteSentinelAsync( string recordId, CancellationToken cancellationToken )
        => _recordStore.WriteAsync( InProgressRecord.Build( recordId ), WritePrecondition.None, cancellationToken );

    private Task DeleteSentinelAsync( string recordId )
        => _recordStore.DeleteAsync( InProgressRecord.IdFor( recordId ) );

    // ADR-0027 — restart interruption pre-scan. Reuses the kind-agnostic
    // existence-by-id contract of IntersectWithAppliedAsync (see that method's
    // remarks) to find leftover sentinels by their derived ids in a single
    // round-trip, then applies policy:
    //   * journal row present  -> stale sentinel from a failed delete-after-journal;
    //                             reap unconditionally (red-blue finding 2).
    //   * journal row absent    -> interrupted:
    //       [StructuralOnly]              -> reap + INFO (idempotent replay, re-runs)
    //       [DataMigration] / unannotated -> ForceResume ? reap + WARN : throw.
    // Cron and non-journaled migrations never write sentinels, so they are not
    // scanned.
    private async Task ScanForInterruptedMigrationsAsync(
        IEnumerable<MigrationDescriptor> migrations,
        IReadOnlyDictionary<long, string> recordIdByVersion,
        IReadOnlySet<string> appliedAtStart,
        CancellationToken cancellationToken )
    {
        // Map sentinel id -> descriptor for every migration that could have
        // written a sentinel (journaled, non-cron). Squashes qualify: their body
        // path writes a sentinel; the auto-mark path writes none, so a leftover
        // can only come from an interrupted body.
        var sentinelToDescriptor = new Dictionary<string, MigrationDescriptor>( StringComparer.Ordinal );
        foreach ( var descriptor in migrations )
        {
            var attribute = descriptor.Attribute!;
            if ( !attribute.Journal || !string.IsNullOrEmpty( attribute.Cron ) )
                continue;

            var recordId = recordIdByVersion[attribute.Version];
            sentinelToDescriptor[InProgressRecord.IdFor( recordId )] = descriptor;
        }

        if ( sentinelToDescriptor.Count == 0 )
            return;

        var leftover = await _recordStore
            .IntersectWithAppliedAsync( sentinelToDescriptor.Keys, cancellationToken )
            .ConfigureAwait( false );

        foreach ( var sentinelId in leftover )
        {
            var descriptor = sentinelToDescriptor[sentinelId];
            var version = descriptor.Attribute!.Version;
            var recordId = recordIdByVersion[version];
            var name = descriptor.Type.Name;

            // Stale sentinel: the journal row exists, so the migration completed
            // and only the delete-after-journal was lost. Reap and move on.
            if ( appliedAtStart.Contains( recordId ) )
            {
                _logger.LogDebug(
                    "[{version}] {name}: reaping stale in-flight sentinel (migration already applied).",
                    version, name );
                await DeleteSentinelAsync( recordId ).ConfigureAwait( false );
                continue;
            }

            // Interrupted: no journal row. Structural-only migrations replay
            // idempotently, so reap and let the run loop re-run.
            var isStructural =
                Attribute.IsDefined( descriptor.Type, typeof( StructuralOnlyAttribute ), inherit: true )
                && !Attribute.IsDefined( descriptor.Type, typeof( DataMigrationAttribute ), inherit: true );

            if ( isStructural )
            {
                _logger.LogInformation(
                    "[{version}] {name}: previously interrupted, but [StructuralOnly] — reaping sentinel and re-running.",
                    version, name );
                await DeleteSentinelAsync( recordId ).ConfigureAwait( false );
                continue;
            }

            // Data / unannotated: fail closed unless the operator opted in.
            if ( _options.ForceResume )
            {
                _logger.LogWarning(
                    "[{version}] {name}: previously interrupted; ForceResume is set — reaping sentinel and re-running. " +
                    "This re-applies the migration body; ensure the live data state is consistent with replay.",
                    version, name );
                await DeleteSentinelAsync( recordId ).ConfigureAwait( false );
                continue;
            }

            throw new MigrationInterruptedException( recordId, version, name );
        }
    }

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
