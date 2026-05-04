using Hyperbee.Migrations.Integration.Tests.Container.Aerospike;
using Hyperbee.Migrations.Integration.Tests.Container.Couchbase;
using Hyperbee.Migrations.Integration.Tests.Container.MongoDb;
using Hyperbee.Migrations.Integration.Tests.Container.OpenSearch;
using Hyperbee.Migrations.Integration.Tests.Container.Postgres;

namespace Hyperbee.Migrations.Integration.Tests.Container;

[TestClass]
public class InitializeTestContainers
{
    // Provider names recognized by HYPERBEE_TESTS_PROVIDERS_ONLY (case-insensitive).
    private const string MongoDb = "MongoDb";
    private const string Postgres = "Postgres";
    private const string Couchbase = "Couchbase";
    private const string Aerospike = "Aerospike";
    private const string OpenSearch = "OpenSearch";

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

        // HYPERBEE_TESTS_PROVIDERS_ONLY scopes the assembly initializer to a
        // subset of providers. Without it, all five providers' containers
        // start up — the historical default. With it, only the named
        // providers (comma-separated, case-insensitive) initialize. Examples:
        //
        //   HYPERBEE_TESTS_PROVIDERS_ONLY=OpenSearch
        //   HYPERBEE_TESTS_PROVIDERS_ONLY=Couchbase,MongoDb
        //
        // The motivation is that the assembly initializer is otherwise a
        // single point of failure for the whole integration suite — any one
        // provider's container failing to start (an environmental issue on
        // a contributor's machine, a test-fixture timing problem, a flaky
        // image pull) prevents EVERY test from running, including ones for
        // unrelated providers. Provider scoping lets contributors verify
        // changes affecting only their provider without fighting unrelated
        // test-fixture problems.
        var providers = ParseProviderFilter( Environment.GetEnvironmentVariable( "HYPERBEE_TESTS_PROVIDERS_ONLY" ) );

        if ( providers is null || providers.Contains( MongoDb ) )
            await MongoDbTestContainer.Initialize( context );
        if ( providers is null || providers.Contains( Postgres ) )
            await PostgresTestContainer.Initialize( context );
        if ( providers is null || providers.Contains( Couchbase ) )
            await CouchbaseTestContainer.Initialize( context );
        if ( providers is null || providers.Contains( Aerospike ) )
            await AerospikeTestContainer.Initialize( context );
        if ( providers is null || providers.Contains( OpenSearch ) )
            await OpenSearchTestContainer.Initialize( context );
    }

    private static HashSet<string>? ParseProviderFilter( string? raw )
    {
        if ( string.IsNullOrWhiteSpace( raw ) )
            return null;

        return raw
            .Split( ',', StringSplitOptions.RemoveEmptyEntries | StringSplitOptions.TrimEntries )
            .ToHashSet( StringComparer.OrdinalIgnoreCase );
    }
}
