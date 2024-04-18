using System.Data;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Networks;

namespace Hyperbee.Migrations.Integration.Tests;

public class MigrationContainer
{
    public static async Task RunMigrationsAsync( IDbConnection connection, INetwork network )
    {
        var location = CommonDirectoryPath.GetSolutionDirectory( "../../" );
        var image = new ImageFromDockerfileBuilder()
            .WithDeleteIfExists( true )
            .WithCleanUp( true )
            .WithName( "db-migrations" )
            .WithDockerfile( "samples/Hyperbee.Migrations.Postgres.Samples/Dockerfile" )
            .WithDockerfileDirectory( location.DirectoryPath )
            .Build();

        await image.CreateAsync( CancellationToken.None )
            .ConfigureAwait( false );

        var migrationContainer = new ContainerBuilder()
            .WithCleanUp( true )
            .WithNetwork( network )
            .WithImage( image )
            // TODO: fix connection string issue with server using IP verses network aliases
            //.WithEnvironment( "Postgresql__ConnectionString", connection.ConnectionString )
            .WithEnvironment( "Postgresql__ConnectionString", "Server=db;Port=5432;Database=postgres;User Id=test;Password=test;" )
            .WithEnvironment( "Migrations__FromPaths__0", "./Hyperbee.Migrations.Postgres.Samples.dll" )
            .WithEnvironment( "Migrations__Lock__Enabled", "true" )
            .WithEnvironment( "Migrations__Lock__Name", "ledger_lock" )
            .WithWaitStrategy( Wait.ForUnixContainer().AddCustomWaitStrategy( new WaitUntilExited() ) )
            .Build();

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