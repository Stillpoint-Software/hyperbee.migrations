using Hyperbee.Migrations.Integration.Tests.Container.Aerospike;
using Hyperbee.Migrations.Integration.Tests.Container.Couchbase;
using Hyperbee.Migrations.Integration.Tests.Container.MongoDb;
using Hyperbee.Migrations.Integration.Tests.Container.OpenSearch;
using Hyperbee.Migrations.Integration.Tests.Container.Postgres;

namespace Hyperbee.Migrations.Integration.Tests.Container;

[TestClass]
public class InitializeTestContainers
{
    [AssemblyInitialize]
    public static async Task Initialize( TestContext context )
    {
        // CI workflows that only run multi-node tests (R-28b multi_node_tests.yml)
        // set HYPERBEE_TESTS_SKIP_SINGLE_NODE=true to bypass single-node
        // container startup cost. The MultiNode-tagged test class handles
        // its own 3-node cluster via [ClassInitialize], so the assembly
        // initializer becomes a no-op in that mode.
        if ( Environment.GetEnvironmentVariable( "HYPERBEE_TESTS_SKIP_SINGLE_NODE" ) == "true" )
            return;

        await MongoDbTestContainer.Initialize( context );
        await PostgresTestContainer.Initialize( context );
        await CouchbaseTestContainer.Initialize( context );
        await AerospikeTestContainer.Initialize( context );
        await OpenSearchTestContainer.Initialize( context );
    }
}
