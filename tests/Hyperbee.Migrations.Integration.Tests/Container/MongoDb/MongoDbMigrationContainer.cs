using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;
using MongoDB.Driver;

namespace Hyperbee.Migrations.Integration.Tests.Container.MongoDb;

public class MongoDbMigrationContainer
{
    public static async Task<IFutureDockerImage> BuildMigrationImageAsync()
    {
        var location = CommonDirectoryPath.GetSolutionDirectory( "../../" );
        var image = new ImageFromDockerfileBuilder()
            .WithDeleteIfExists( true )
            .WithCleanUp( true )
            .WithName( "db-migrations" )
            .WithDockerfile( "samples/Hyperbee.Migrations.MongoDB.Samples/Dockerfile" )
            .WithDockerfileDirectory( location.DirectoryPath )
            .Build();

        await image.CreateAsync( CancellationToken.None )
            .ConfigureAwait( false );

        return image;
    }

    public static async Task<IContainer> BuildMigrationsAsync( IMongoClient client, INetwork network, IFutureDockerImage image = null )
    {
        image ??= await BuildMigrationImageAsync();

        return new ContainerBuilder()
            .WithCleanUp( true )
            .WithNetwork( network )
            .WithImage( image )
            // TODO: fix connection string issue with server using IP verses network aliases
            .WithEnvironment( "Provider", "MongoDb" )
            .WithEnvironment( "MongoDb__ConnectionString", "mongodb://test:test@db:27017/")
            .WithEnvironment( "Migrations__FromPaths__0", "./Hyperbee.Migrations.MongoDB.Samples.dll" )
            .WithEnvironment( "Migrations__Lock__Enabled", "true" )
            .WithEnvironment( "Migrations__Lock__Name", "ledger_lock" )
            .WithWaitStrategy( Wait.ForUnixContainer().AddCustomWaitStrategy( new WaitUntilExited() ) )
            .Build();
    }

    public static async Task RunMigrationsAsync( IMongoClient client, INetwork network )
    {
        var migrationContainer = await BuildMigrationsAsync( client, network );

        await migrationContainer.StartAsync( CancellationToken.None )
            .ConfigureAwait( false );
    }

    public class WaitUntilExited : IWaitUntil
    {
        public async Task<bool> UntilAsync( IContainer container )
        {
            await Task.CompletedTask;
            return container.State == TestcontainersStates.Exited;
        }
    }
}
