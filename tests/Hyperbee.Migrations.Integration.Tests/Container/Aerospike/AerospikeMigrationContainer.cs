using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;

namespace Hyperbee.Migrations.Integration.Tests.Container.Aerospike;

public class AerospikeMigrationContainer
{
    // Sharing a single IFutureDockerImage across multiple ContainerBuilder()s and
    // then calling StartAsync on them in parallel races inside Testcontainers'
    // resource-materialization machinery (DockerContainer.StartAsync line 333,
    // NRE in the Task.WhenAll over the resource futures). Materialize the image
    // once up front and hand each container the image's full reference string so
    // every container has its own resource graph.
    public static async Task<string> BuildMigrationImageAsync()
    {
        var location = CommonDirectoryPath.GetSolutionDirectory( "../../" );
        var image = new ImageFromDockerfileBuilder()
            .WithDeleteIfExists( true )
            .WithCleanUp( true )
            .WithName( "aerospike-migrations" )
            .WithDockerfile( "runners/Hyperbee.MigrationRunner.Aerospike/Dockerfile" )
            .WithDockerfileDirectory( location.DirectoryPath )
            .Build();

        await image.CreateAsync( CancellationToken.None )
            .ConfigureAwait( false );

        return image.FullName;
    }

    public static async Task<IContainer> BuildMigrationsAsync( INetwork network, string imageName = null )
    {
        imageName ??= await BuildMigrationImageAsync();

        return new ContainerBuilder()
            .WithCleanUp( true )
            .WithNetwork( network )
            .WithImage( imageName )
            .WithEnvironment( "Aerospike__ConnectionString", "db:3000" )
            .WithEnvironment( "Migrations__FromPaths__0", "./Hyperbee.Migrations.Aerospike.Samples.dll" )
            .WithEnvironment( "Migrations__Lock__Enabled", "true" )
            .WithEnvironment( "Migrations__Lock__Name", "migration_lock" )
            .WithEnvironment( "Migrations__Namespace", "test" )
            .WithEnvironment( "Migrations__MigrationSet", "SchemaMigrations" )
            .WithCreateParameterModifier( p => p.HostConfig.LogConfig = new Docker.DotNet.Models.LogConfig
            {
                Type = "json-file"
            } )
            .WithWaitStrategy( DotNet.Testcontainers.Builders.Wait.ForUnixContainer().UntilMessageIsLogged( "Application is shutting down", o => o.WithMode( WaitStrategyMode.OneShot ) ) )
            .Build();
    }

    public static async Task RunMigrationsAsync( INetwork network )
    {
        var migrationContainer = await BuildMigrationsAsync( network );

        await migrationContainer.StartAsync( CancellationToken.None )
            .ConfigureAwait( false );
    }
}
