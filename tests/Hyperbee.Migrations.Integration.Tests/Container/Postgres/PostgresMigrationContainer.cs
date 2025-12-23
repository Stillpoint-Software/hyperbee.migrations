using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;

namespace Hyperbee.Migrations.Integration.Tests.Container.Postgres;

public class PostgresMigrationContainer
{
    public static async Task<IFutureDockerImage> BuildMigrationImageAsync()
    {
        var location = CommonDirectoryPath.GetSolutionDirectory( "../../" );
        var image = new ImageFromDockerfileBuilder()
            .WithDeleteIfExists( true )
            .WithCleanUp( true )
            .WithName( "postgres-db-migrations" )
            .WithDockerfile( "samples/Hyperbee.MigrationRunner.Postgres/Dockerfile" )
            .WithDockerfileDirectory( location.DirectoryPath )
            .Build();

        await image.CreateAsync( CancellationToken.None )
            .ConfigureAwait( false );

        return image;
    }

    public static async Task<IContainer> BuildMigrationsAsync( IDbConnection connection, INetwork network, IFutureDockerImage image = null )
    {
        image ??= await BuildMigrationImageAsync();

        return new ContainerBuilder()
            .WithCleanUp( true )
            .WithNetwork( network )
            .WithImage( image )
            // TODO: fix connection string issue with server using IP verses network aliases
            //.WithEnvironment( "Postgresql__ConnectionString", connection.ConnectionString )
            .WithEnvironment( "Postgresql__ConnectionString", "Server=db;Port=5432;Database=postgres;User Id=test;Password=test;" )
            .WithEnvironment( "Migrations__FromPaths__0", "./Hyperbee.Migrations.Postgres.Samples.dll" )
            .WithEnvironment( "Migrations__Lock__Enabled", "true" )
            .WithEnvironment( "Migrations__Lock__Name", "ledger_lock" )
            .WithWaitStrategy( DotNet.Testcontainers.Builders.Wait.ForUnixContainer().UntilMessageIsLogged( "Executed", o => o.WithMode( WaitStrategyMode.OneShot ) ) )
            .Build();
    }

    public static async Task RunMigrationsAsync( IDbConnection connection, INetwork network )
    {
        var migrationContainer = await BuildMigrationsAsync( connection, network );

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
