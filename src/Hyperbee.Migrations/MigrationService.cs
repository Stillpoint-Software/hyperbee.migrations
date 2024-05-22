using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hyperbee.Migrations
{
    public class MigrationService( MigrationQueue migrationQueue, IMigrationRecordStore recordStore, ILogger<MigrationService> logger )
        : BackgroundService
    {
        public async Task DoWork( CancellationToken cancellationToken )
        {
            MigrationItem migrationItem;
            do
            {
                migrationItem = migrationQueue.TryPeek();
                if ( migrationItem != null )
                {
                    var version = migrationItem.Attribute!.Version;
                    var name = migrationItem.Migration.GetType();
                    var direction = migrationItem.Direction;

                    logger.LogInformation( $"[{version}] {name}: {direction} migration started", migrationItem.Attribute.Version, migrationItem.GetType().Name, migrationItem.Direction );


                    if ( await CanStartMigration( migrationItem ) )
                    {
                        await ProcessJobAsync( migrationItem, recordStore, cancellationToken ).ConfigureAwait( false );
                    }

                    logger.LogInformation( $"[{version}] {name}: {direction} migration item {name} completed", migrationItem.Attribute.Version, migrationItem.GetType().Name, migrationItem.Direction );

                    //finished processing the current migration item
                    migrationQueue.FinishedItem( migrationItem );
                    //get the next item.
                    migrationItem = migrationQueue.GetNextItem();
                    if ( migrationItem == null )
                    {

                    }
                }

            } while ( migrationItem != null && !cancellationToken.IsCancellationRequested );

            //migrationQueue.CompleteAdding();

            //if ( migrationQueue.Finished() )
            //{
            //    await StopAsync( cancellationToken );
            //}
        }

        protected override async Task ExecuteAsync( CancellationToken cancellationToken )
        {
            await DoWork( cancellationToken );
        }

        private async Task ProcessJobAsync( MigrationItem migrationItem, IMigrationRecordStore recordStore, CancellationToken cancellationToken )
        {
            switch ( migrationItem.Direction )
            {
                case Direction.Up:
                    await migrationItem.Migration.UpAsync( cancellationToken ).ConfigureAwait( false );

                    if ( await CanStopMigration( migrationItem ) )
                    {

                        if ( migrationItem.Attribute.Journal )
                            await recordStore.WriteAsync( migrationItem.RecordId ).ConfigureAwait( false );
                    }

                    break;
                case Direction.Down:
                    await migrationItem.Migration.DownAsync( cancellationToken ).ConfigureAwait( false );

                    if ( await CanStopMigration( migrationItem ) )
                    {
                        if ( migrationItem.Attribute.Journal )
                            await recordStore.DeleteAsync( migrationItem.RecordId ).ConfigureAwait( false );
                    }

                    break;
            }
        }

        private async Task<bool> CanStartMigration( MigrationItem migrationItem )
        {
            if ( string.IsNullOrEmpty( migrationItem.Attribute.StartMethod ) )
            {
                return true;
            }

            var methodInfo = migrationItem.Migration.GetType().GetMethod( migrationItem.Attribute.StartMethod );

            if ( methodInfo != null && methodInfo.ReturnType == typeof( Task<bool> ) )
                return await (Task<bool>) methodInfo.Invoke( migrationItem.Migration, null )!;

            logger.LogError( $"Method '{methodInfo?.Name}' not found or does not return a boolean or string." );
            return false;
        }

        private async Task<bool> CanStopMigration( MigrationItem migrationItem )
        {
            if ( string.IsNullOrEmpty( migrationItem.Attribute.StopMethod ) )
            {
                return true;
            }
            var methodInfo = migrationItem.Migration.GetType().GetMethod( migrationItem.Attribute.StopMethod );

            if ( methodInfo != null && methodInfo.ReturnType == typeof( Task<bool> ) )
                return await (Task<bool>) methodInfo.Invoke( migrationItem.Migration, null )!;

            logger.LogError( $"Method '{methodInfo?.Name}' not found or does not return a boolean." );
            return false;
        }

    }
}
