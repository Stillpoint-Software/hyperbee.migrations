using DotNet.Testcontainers.Networks;
using Hyperbee.Migrations.Integration.Tests.Container.MongoDb;
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
}
