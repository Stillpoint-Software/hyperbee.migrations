using Aerospike.Client;
using Hyperbee.Migrations.Providers.Aerospike.Extensions;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;

[TestClass]
public class AerospikeClientExtensionsTests
{
    // ContainsIndex: the correctness-critical parsing of sindex info responses.
    // The Aerospike `sindex/<ns>` command returns a single string with entries like:
    //   ns=test:indexname=idx_email:set=users:bin=email:type=STRING;ns=test:indexname=idx_age:...

    [TestMethod]
    public void ContainsIndex_returns_true_when_index_present()
    {
        const string response = "ns=test:indexname=idx_email:set=users:bin=email:type=STRING";

        Assert.IsTrue( AerospikeClientExtensions.ContainsIndex( response, "idx_email" ) );
    }

    [TestMethod]
    public void ContainsIndex_returns_false_when_index_missing()
    {
        const string response = "ns=test:indexname=idx_email:set=users:bin=email:type=STRING";

        Assert.IsFalse( AerospikeClientExtensions.ContainsIndex( response, "idx_age" ) );
    }

    [TestMethod]
    public void ContainsIndex_returns_false_when_response_empty()
    {
        Assert.IsFalse( AerospikeClientExtensions.ContainsIndex( "", "idx_email" ) );
        Assert.IsFalse( AerospikeClientExtensions.ContainsIndex( null, "idx_email" ) );
    }

    [TestMethod]
    public void ContainsIndex_returns_false_for_prefix_match()
    {
        // This is the correctness fix: old code did a naive Contains(indexName) and would
        // incorrectly return true for `idx_foo` when only `idx_foo_bar` existed.

        const string response = "ns=test:indexname=idx_foo_bar:set=users:bin=value:type=STRING";

        Assert.IsFalse( AerospikeClientExtensions.ContainsIndex( response, "idx_foo" ) );
    }

    [TestMethod]
    public void ContainsIndex_finds_index_among_multiple()
    {
        const string response =
            "ns=test:indexname=idx_email:set=users:bin=email:type=STRING;" +
            "ns=test:indexname=idx_age:set=users:bin=age:type=NUMERIC;" +
            "ns=test:indexname=idx_category:set=products:bin=category:type=STRING";

        Assert.IsTrue( AerospikeClientExtensions.ContainsIndex( response, "idx_email" ) );
        Assert.IsTrue( AerospikeClientExtensions.ContainsIndex( response, "idx_age" ) );
        Assert.IsTrue( AerospikeClientExtensions.ContainsIndex( response, "idx_category" ) );
        Assert.IsFalse( AerospikeClientExtensions.ContainsIndex( response, "idx_missing" ) );
    }

    [TestMethod]
    public void ContainsIndex_is_case_insensitive()
    {
        const string response = "ns=test:indexname=idx_Email:set=users:bin=email:type=STRING";

        Assert.IsTrue( AerospikeClientExtensions.ContainsIndex( response, "idx_email" ) );
        Assert.IsTrue( AerospikeClientExtensions.ContainsIndex( response, "IDX_EMAIL" ) );
    }

    // IndexExistsAsync: when the cluster has no nodes, the extension must not throw — it returns false
    // so callers can proceed to create-index calls which then surface real cluster errors.

    [TestMethod]
    public async Task IndexExistsAsync_returns_false_when_no_nodes()
    {
        var client = Substitute.For<IAerospikeClient>();
        client.Nodes.Returns( Array.Empty<Node>() );

        var result = await client.IndexExistsAsync( "test", "idx_email" );

        Assert.IsFalse( result );
    }

    // UpsertAsync: verifies the extension constructs the Key and Bin[] correctly and
    // forwards to IAsyncClient.Put with the given cancellation token.

    [TestMethod]
    public async Task UpsertAsync_dict_builds_key_and_bins_and_forwards_token()
    {
        var client = Substitute.For<IAsyncClient>();
        var cts = new CancellationTokenSource();

        var bins = new Dictionary<string, object>
        {
            ["name"] = "Alice",
            ["active"] = 1L,
            ["score"] = 42.5
        };

        await client.UpsertAsync( "test", "users", "user-1", bins, cts.Token );

        await client.Received( 1 ).Put(
            null,
            cts.Token,
            Arg.Is<Key>( k => k.ns == "test" && k.setName == "users" && k.userKey.ToString() == "user-1" ),
            Arg.Is<Bin[]>( b =>
                b.Length == 3 &&
                b.Any( x => x.name == "name" ) &&
                b.Any( x => x.name == "active" ) &&
                b.Any( x => x.name == "score" ) ) );
    }

    [TestMethod]
    public async Task UpsertAsync_bin_array_forwards_bins_unchanged()
    {
        var client = Substitute.For<IAsyncClient>();
        var bins = new[] { new Bin( "name", "Bob" ), new Bin( "score", 10L ) };

        await client.UpsertAsync( "prod", "items", "item-1", bins, CancellationToken.None );

        await client.Received( 1 ).Put(
            null,
            CancellationToken.None,
            Arg.Is<Key>( k => k.ns == "prod" && k.setName == "items" ),
            Arg.Is<Bin[]>( b => ReferenceEquals( b, bins ) ) );
    }
}
