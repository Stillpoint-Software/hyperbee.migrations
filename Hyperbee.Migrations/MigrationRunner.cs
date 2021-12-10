using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Linq;
using System.Reflection;
using System.Threading.Tasks;
using Microsoft.Extensions.Logging;

namespace Hyperbee.Migrations;

public class MigrationRunner
{
    private readonly IMigrationRecordStore _recordStore;

    private readonly MigrationOptions _options;
    private readonly ILogger<MigrationRunner> _logger;

    public MigrationRunner( IMigrationRecordStore recordStore, MigrationOptions options, ILogger<MigrationRunner> logger )
    {
        _recordStore = recordStore ?? throw new ArgumentNullException( nameof(recordStore) );
        _options = options ?? throw new ArgumentNullException( nameof(options) );
        _logger = logger ?? throw new ArgumentNullException( nameof(logger) );
    }

    public virtual async Task RunAsync()
    {
        IDisposable mutex = null;

        try
        {
            await _recordStore.InitializeAsync();

            if ( _options.MutexEnabled )
                mutex = await _recordStore.CreateMutexAsync();

            await RunMigrationsAsync(); 
        }
        catch ( MigrationMutexUnavailableException )
        {
            _logger.LogWarning( "The migration mutex is unavailable. Skipping migrations." );
        }
        finally
        {
            mutex?.Dispose();
        }
    }

    private async Task RunMigrationsAsync()
    {
        var migrations = FindMigrations( _options );

        var executionStopwatch = Stopwatch.StartNew();

        var runCount = 0;
        foreach ( var pair in migrations )
        {
            var migration = pair.Migration();
            migration.Setup( _options, _logger );
            var migrationId = _options.Conventions.MigrationDocumentId( migration, _options.IdSeparatorChar );

            var migrationRecord = await _recordStore.LoadAsync( migrationId );

            switch ( _options.Direction )
            {
                case Directions.Down:
                    if ( migrationRecord == null )
                        continue;

                    await ExecuteMigrationAsync( _options.Direction, pair.Attribute!.Version, migration, async () =>
                    {
                        migration.Down();
                        await _recordStore.DeleteAsync( migrationRecord );
                    } );
                    runCount++;
                    break;

                case Directions.Up:
                    if ( migrationRecord != null )
                        continue;

                    await ExecuteMigrationAsync( _options.Direction, pair.Attribute!.Version, migration, async () =>
                    {
                        migration.Up();
                        await _recordStore.StoreAsync( migrationId );
                    } );
                    runCount++;
                    break;

                default:
                    throw new ArgumentOutOfRangeException( nameof(_options.Direction) );
            }

            if ( pair.Attribute.Version == _options.ToVersion )
                break;
        }

        executionStopwatch.Stop();
        _logger.LogInformation( "{migrationCount} migrations executed in {elapsed}", runCount, executionStopwatch.Elapsed );
    }

    private async Task ExecuteMigrationAsync( Directions direction, long version, Migration migration, Func<Task> migrationAsync )
    {
        var migrationDirection = direction == Directions.Down ? "Down" : "Up";
        _logger.LogInformation( "[{version}] {name}: {direction} migration started", version, migration.GetType().Name, migrationDirection );

        var migrationStopwatch = Stopwatch.StartNew();

        await migrationAsync();

        migrationStopwatch.Stop();

        _logger.LogInformation( "[{version}] {name}: {direction} migration completed in {elapsed}", version, migration.GetType().Name, migrationDirection, migrationStopwatch.Elapsed );
    }

    private static IEnumerable<MigrationDescriptor> FindMigrations( MigrationOptions options )
    {
        var migrations = options.Assemblies
            .SelectMany( AssemblyTypes, ( assembly, type ) => new { assembly, type } )
            .Where( x => options.Conventions.TypeIsMigration( x.type ) )
            .Select( x => new MigrationDescriptor
            {
                Migration = () => options.MigrationActivator.CreateInstance( x.type ),
                Attribute = x.type.GetMigrationAttribute()
            } )
            .Where( descriptor => IsInScope( descriptor, options ) );
        
        return options.Direction == Directions.Up
            ? migrations.OrderBy( x => x.Attribute!.Version )
            : migrations.OrderByDescending( x => x.Attribute!.Version );
    }

    private static IEnumerable<Type> AssemblyTypes( Assembly assembly )
    {
        if ( assembly == null )
            throw new ArgumentNullException( nameof(assembly) );

        try
        {
            return assembly.GetTypes();
        }
        catch ( ReflectionTypeLoadException ex )
        {
            return ex.Types.Where( type => type != null );
        }
    }

    private static bool IsInScope( MigrationDescriptor descriptor, MigrationOptions options )
    {
        if ( descriptor.Attribute == null )
            // Subclasses of Migration that can be instantiated must have the MigrationAttribute.
            // If this class was intended as a base class for other migrations, make it an abstract class.
            return false;

        // if no profile has been declared the migration is in-scope

        if ( !descriptor.Attribute.Profiles.Any() )
            return true;

        // the migration must belong to at least one of the currently specified profiles

        return options.Profiles
            .Intersect( descriptor.Attribute.Profiles, StringComparer.OrdinalIgnoreCase )
            .Any();
    }
}