
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Configurations;
using DotNet.Testcontainers.Containers;
using DotNet.Testcontainers.Images;
using DotNet.Testcontainers.Networks;
using Testcontainers.Couchbase;

namespace Hyperbee.Migrations.Integration.Tests.Container.Couchbase;

public class CouchbaseMigrationContainer
{
    public static async Task<IFutureDockerImage> BuildMigrationImageAsync()
    {
        var location = CommonDirectoryPath.GetSolutionDirectory( "../../" );
        var image = new ImageFromDockerfileBuilder()
            .WithDeleteIfExists( true )
            .WithCleanUp( true )
            .WithName( "couchbase-db-migrations" )
            .WithDockerfile( "samples/Hyperbee.MigrationRunner.Couchbase/Dockerfile" )
            .WithDockerfileDirectory( location.DirectoryPath )
            .Build();

        await image.CreateAsync( CancellationToken.None )
            .ConfigureAwait( false );

        return image;
    }

    public static async Task<IContainer> BuildMigrationsAsync( string connectionString, INetwork network, IFutureDockerImage image = null )
    {
        image ??= await BuildMigrationImageAsync();

        return new ContainerBuilder()
            .WithCleanUp( true )
            .WithNetwork( network )
            .WithImage( image )
            .WithEnvironment( "Couchbase__ConnectionString", connectionString )
            .WithEnvironment( "Couchbase__UserName", CouchbaseBuilder.DefaultUsername )
            .WithEnvironment( "Couchbase__Password", CouchbaseBuilder.DefaultPassword )
            .WithEnvironment( "Migrations__FromPaths__0", "./Hyperbee.Migrations.Couchbase.Samples.dll" )
            .WithEnvironment( "Migrations__Lock__Enabled", "true" )
            .WithWaitStrategy( DotNet.Testcontainers.Builders.Wait.ForUnixContainer().UntilHttpRequestIsSucceeded( r => r.ForPort( 8091 ) ) )
            .Build();
    }

    public static async Task RunMigrationsAsync( string connectionString, INetwork network )
    {
        var migrationContainer = await BuildMigrationsAsync( connectionString, network );

        await migrationContainer.StartAsync( CancellationToken.None )
            .ConfigureAwait( false );
    }
}
