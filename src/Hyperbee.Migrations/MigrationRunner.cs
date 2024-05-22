using System.Diagnostics;
using System.Reflection;
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

        var migrations = DiscoverMigrations( _options );

        var stopwatch = Stopwatch.StartNew();

        var runCount = 0;
        foreach ( var (type, attribute) in migrations )
        {
            cancellationToken.ThrowIfCancellationRequested();

            // instantiate the migration

            var migration = _options.MigrationActivator.CreateInstance( type );

            // determine if the migration should be run

            var recordId = _options.Conventions.GetRecordId( migration );

            var exists = await _recordStore.ExistsAsync( recordId ).ConfigureAwait( false );

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

            var migrationItem = new MigrationItem
            {
                Migration = migration,
                Direction = direction,
                Attribute = attribute,
                RecordId = recordId,
                CancellationToken = cancellationToken
            };

            if ( await StartMigration( migrationItem ) )
            {
                await ProcessJobAsync( migrationItem, _recordStore, cancellationToken ).ConfigureAwait( false );
            }

            runCount++;

            _logger.LogInformation( "[{version}] {name}: {direction} migration completed", version, name, direction );

            if ( version == _options.ToVersion )
                break;
        }

        stopwatch.Stop();
        _logger.LogInformation( "Executed {migrationCount} migrations in {elapsed}", runCount, stopwatch.Elapsed );
    }

    private static IEnumerable<MigrationDescriptor> DiscoverMigrations( MigrationOptions options )
    {
        // discover descriptors
        var descriptors = options.Assemblies
            .SelectMany( assembly => assembly.GetTypes() )
            .Where( type => typeof( Migration ).IsAssignableFrom( type ) && !type.IsAbstract )
            .Select( type =>
            {
                var attribute = type.GetCustomAttribute( typeof( MigrationAttribute ) ) as MigrationAttribute;
                return new MigrationDescriptor( type, attribute );
            } )
            .Where( descriptor => DescriptorInScope( descriptor, options ) )
            .OrderBy( x => x.Attribute!.Version, options.Direction )
            .ToList();

        // throw if any duplicates
        var set = new HashSet<long>();

        var duplicate = descriptors
            .Select( x => x.Attribute!.Version )
            .Where( x => !set.Add( x ) )
            .Select( x => new long?( x ) )
            .FirstOrDefault();

        if ( duplicate.HasValue )
            throw new DuplicateMigrationException( $"Migration number conflict detected for version number `{duplicate.Value}`.", duplicate.Value );

        // success
        return descriptors;
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

    private async Task ProcessJobAsync( MigrationItem migrationItem, IMigrationRecordStore recordStore, CancellationToken cancellationToken )
    {
        switch ( migrationItem.Direction )
        {
            case Direction.Up:
                await migrationItem.Migration.UpAsync( cancellationToken ).ConfigureAwait( false );

                if ( await StopMigration( migrationItem ) )
                {
                    if ( migrationItem.Attribute.Journal )
                        await recordStore.WriteAsync( migrationItem.RecordId ).ConfigureAwait( false );
                }
                break;
            case Direction.Down:
                await migrationItem.Migration.DownAsync( cancellationToken ).ConfigureAwait( false );

                if ( await StopMigration( migrationItem ) )
                {
                    if ( migrationItem.Attribute.Journal )
                        await recordStore.DeleteAsync( migrationItem.RecordId ).ConfigureAwait( false );
                }
                break;
        }
    }

    private async Task<bool> StartMigration( MigrationItem migrationItem )
    {
        if ( string.IsNullOrEmpty( migrationItem.Attribute.StartMethod ) )
        {
            return true;
        }

        var methodInfo = migrationItem.Migration.GetType().GetMethod( migrationItem.Attribute.StartMethod );

        if ( methodInfo != null && methodInfo.ReturnType == typeof( Task<bool> ) )
            return await (Task<bool>) methodInfo.Invoke( migrationItem.Migration, null )!;

        _logger.LogError( $"Method '{methodInfo?.Name}' not found or does not return a boolean or string." );
        return false;
    }

    private async Task<bool> StopMigration( MigrationItem migrationItem )
    {
        if ( string.IsNullOrEmpty( migrationItem.Attribute.StopMethod ) )
        {
            return true;
        }
        var methodInfo = migrationItem.Migration.GetType().GetMethod( migrationItem.Attribute.StopMethod );

        if ( methodInfo != null && methodInfo.ReturnType == typeof( Task<bool> ) )
            return await (Task<bool>) methodInfo.Invoke( migrationItem.Migration, null )!;

        _logger.LogError( $"Method '{methodInfo?.Name}' not found or does not return a boolean." );
        return false;
    }

}
