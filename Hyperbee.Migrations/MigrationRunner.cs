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
            // instantiate the migration

            var migration = pair.Migration();
            migration.Setup( _options, _logger );

            // make sure we want to run the migration

            var recordId = _options.Conventions.GetRecordId( migration, _options.IdSeparatorChar );

            var exists = await _recordStore.ExistsAsync( recordId );
            var direction = _options.Direction;

            switch ( direction )
            {
                case Directions.Up when exists:
                case Directions.Down when !exists:
                    continue;
            }

            // run the migration

            var version = pair.Attribute!.Version;
            var name = migration.GetType().Name;

            _logger.LogInformation( "[{version}] {name}: {direction} migration started", version, name, direction );

            switch ( direction )
            {
                case Directions.Down:
                    migration.Down();
                    await _recordStore.DeleteAsync( recordId );
                    break;

                case Directions.Up:
                    migration.Up();
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

    private static IEnumerable<MigrationDescriptor> FindMigrations( MigrationOptions options )
    {
        var migrations = options.Assemblies
            .SelectMany( assembly => assembly.GetTypes() )
            .Where( type => typeof(Migration).IsAssignableFrom( type ) && !type.IsAbstract )
            .Select( type => new MigrationDescriptor
            {
                Migration = () => options.MigrationActivator.CreateInstance( type ),
                Attribute = type.GetCustomAttribute( typeof(MigrationAttribute) ) as MigrationAttribute
            } )
            .Where( descriptor => IsInScope( descriptor, options ) );
        
        return options.Direction == Directions.Up
            ? migrations.OrderBy( x => x.Attribute!.Version )
            : migrations.OrderByDescending( x => x.Attribute!.Version );
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