using Hyperbee.Migrations.Integration.Tests.Container.MongoDb;
using Hyperbee.Migrations.Integration.Tests.Container.Postgres;

namespace Hyperbee.Migrations.Integration.Tests.Container;

[TestClass]
public class InitializeTestContainers
{
    [AssemblyInitialize]
    public static async Task Initialize( TestContext context )
    {
        await MongoDbTestContainer.Initialize( context );
        await PostgresTestContainer.Initialize( context );
    }
}
