using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Hyperbee.Migrations;

public class MigrationRunner
{
    private readonly IMigrationRecordStore _recordStore;

    private readonly MigrationOptions _options;
    private readonly ILogger<MigrationRunner> _logger;

    private record MigrationDescriptor( Type Type, MigrationAttribute Attribute );

    public MigrationRunner( IMigrationRecordStore recordStore, MigrationOptions options, ILogger<MigrationRunner> logger )
    {
        _recordStore = recordStore ?? throw new ArgumentNullException( nameof(recordStore) );
        _options = options ?? throw new ArgumentNullException( nameof(options) );
        _logger = logger ?? throw new ArgumentNullException( nameof(logger) );
    }

    public virtual async Task RunAsync( CancellationToken cancellationToken = default )
    {
        IDisposable syncLock = null;

        try
        {
            await _recordStore.InitializeAsync();

            if ( _options.LockingEnabled )
                syncLock = await _recordStore.CreateLockAsync();

            await RunMigrationsAsync( cancellationToken );
        }
        catch ( MigrationLockUnavailableException )
        {
            _logger.LogWarning( "The migration lock is unavailable. Skipping migrations." );
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

        var migrations = DiscoverMigrations( _options );

        var executionStopwatch = Stopwatch.StartNew();

        var runCount = 0;
        foreach ( var (type, attribute) in migrations )
        {
            cancellationToken.ThrowIfCancellationRequested();

            // instantiate the migration

            var migration = _options.MigrationActivator.CreateInstance( type );

            // determine if the migration should be run

            var recordId = _options.Conventions.GetRecordId( migration );

            var exists = await _recordStore.ExistsAsync( recordId );

            switch ( direction )
            {
                case Direction.Up when exists:
                case Direction.Down when !exists:
                    continue;
            }

            // run the migration

            var version = attribute!.Version;
            var name = migration.GetType().Name;

            _logger.LogInformation( "[{version}] {name}: {direction} migration started", version, name, direction );

            switch ( direction )
            {
                case Direction.Down:
                    await migration.DownAsync( cancellationToken );
                    await _recordStore.DeleteAsync( recordId );
                    break;

                case Direction.Up:
                    await migration.UpAsync( cancellationToken );
                    await _recordStore.StoreAsync( recordId );
                    break;
            }

            runCount++;

            _logger.LogInformation( "[{version}] {name}: {direction} migration completed", version, name, direction );

            if ( version == _options.ToVersion )
                break;
        }

        executionStopwatch.Stop();
        _logger.LogInformation( "Executed {migrationCount} migrations in {elapsed}", runCount, executionStopwatch.Elapsed );
    }

    private static IEnumerable<MigrationDescriptor> DiscoverMigrations( MigrationOptions options )
    {
        // discover descriptors
        var descriptors = options.Assemblies
            .SelectMany( assembly => assembly.GetTypes() )
            .Where( type => typeof(Migration).IsAssignableFrom( type ) && !type.IsAbstract )
            .Select( type =>
            {
                var attribute = type.GetCustomAttribute( typeof(MigrationAttribute) ) as MigrationAttribute;
                return new MigrationDescriptor( type, attribute );
            } )
            .Where( descriptor => DescriptorInScope( descriptor, options ) );

        // order by id
        var ordered = (options.Direction == Direction.Up
                ? descriptors.OrderBy( x => x.Attribute!.Version )
                : descriptors.OrderByDescending( x => x.Attribute!.Version ))
            .ToList();

        // throw if any duplicates
        var set = new HashSet<long>();

        var duplicate = ordered
            .Select( x => x.Attribute!.Version )
            .Where( x => !set.Add( x ) )
            .Select( x => new long?( x ) )
            .FirstOrDefault();

        if ( duplicate.HasValue )
            throw new DuplicateMigrationException( $"Migration number conflict detected for version number `{duplicate.Value}`.", duplicate.Value );

        // success
        return ordered;
    }

    private static bool DescriptorInScope( MigrationDescriptor descriptor, MigrationOptions options )
    {
        var (_, attribute) = descriptor;

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
}