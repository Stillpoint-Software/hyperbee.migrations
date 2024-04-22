using DotNet.Testcontainers.Networks;
using Hyperbee.Migrations.Integration.Tests.Container.MongoDb;
using Hyperbee.Migrations.Integration.Tests.Container.Postgres;
using MongoDB.Driver;

namespace Hyperbee.Migrations.Integration.Tests;

[TestClass]
public class MongoDBRunnerTest
{
    public IMongoClient Client;
    public INetwork Network;

    [TestInitialize]
    public void Setup()
    {
        Client = MongoDbTestContainer.Client;
        Network = MongoDbTestContainer.Network;
    }

    [TestMethod]
    public async Task Should_Run_WithMongoDb()
    {
        var migrationContainer = await MongoDbMigrationContainer.BuildMigrationsAsync( Client, Network );
        await migrationContainer.StartAsync();
        await migrationContainer.StartAsync();

        // TODO: Assert
    }


    [TestMethod]
    public async Task Should_Fail_WhenMigrationHasLock()
    {
        var migrationImage = await MongoDbMigrationContainer.BuildMigrationImageAsync();

        var migrationContainer1 = await MongoDbMigrationContainer.BuildMigrationsAsync( Client, Network, migrationImage );
        var migrationContainer2 = await MongoDbMigrationContainer.BuildMigrationsAsync( Client, Network, migrationImage );
        var migrationContainer3 = await MongoDbMigrationContainer.BuildMigrationsAsync( Client, Network, migrationImage );
        var migrationContainer4 = await MongoDbMigrationContainer.BuildMigrationsAsync( Client, Network, migrationImage );

        var migration1 = migrationContainer1.StartAsync();
        var migration2 = migrationContainer2.StartAsync();
        var migration3 = migrationContainer3.StartAsync();
        var migration4 = migrationContainer4.StartAsync();

        await Task.WhenAll( migration1, migration2, migration3, migration4 );

        // TODO: Assert
    }
}
