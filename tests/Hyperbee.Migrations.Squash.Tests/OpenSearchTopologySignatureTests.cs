using FluentAssertions;
using Hyperbee.Migrations.Providers.Aerospike.Squash;
using Hyperbee.Migrations.Providers.OpenSearch.Squash;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 2 Task 2.1: OpenSearchTopologySignature unit coverage.
//
// Live CaptureAsync against a cluster is exercised in the Phase 2 integration
// suite (Testcontainers OpenSearch). These tests cover:
//   - IsCompatibleWith comparison rules
//   - Cross-provider rejection
//   - Plugin-set strict-equality (the new pressure-test surface)
//   - Internal parser helpers (root response, version number, plugins response)

[TestClass]
public class OpenSearchTopologySignatureTests
{
    private static OpenSearchTopologySignature BaselineSignature() => new()
    {
        ServerMajor = 2,
        ServerMinor = 13,
        Distribution = "opensearch",
        ClusterName = "test-cluster",
        NodeCount = 1,
        Plugins = new[]
        {
            "opensearch-index-management",
            "opensearch-security"
        },
        IsmPathPrefix = "_plugins/_ism"
    };

    [TestMethod]
    public void SchemaVersion_AndProviderId_AreStable()
    {
        var sig = BaselineSignature();
        sig.SchemaVersion.Should().Be( 1 );
        sig.ProviderId.Should().Be( "opensearch" );
        OpenSearchTopologySignature.ProviderIdValue.Should().Be( "opensearch" );
    }

    [TestMethod]
    public void IsCompatibleWith_Self_ReturnsTrue()
    {
        var sig = BaselineSignature();
        sig.IsCompatibleWith( sig, out var reason ).Should().BeTrue();
        reason.Should().BeNullOrEmpty();
    }

    [TestMethod]
    public void IsCompatibleWith_DifferentMajor_Incompatible()
    {
        var a = BaselineSignature();
        var b = a with { ServerMajor = 3 };

        a.IsCompatibleWith( b, out var reason ).Should().BeFalse();
        reason.Should().Contain( "server_major" );
    }

    [TestMethod]
    public void IsCompatibleWith_DifferentMinor_StillCompatible()
    {
        var a = BaselineSignature();
        var b = a with { ServerMinor = 17 };

        a.IsCompatibleWith( b, out var reason ).Should().BeTrue();
        reason.Should().BeNullOrEmpty();
    }

    [TestMethod]
    public void IsCompatibleWith_DistributionMismatch_Incompatible()
    {
        // OpenSearch vs Elasticsearch is a hard incompatibility: the two
        // forks diverged in 1.0 and have different feature surfaces.
        var os = BaselineSignature();
        var es = os with { Distribution = "elasticsearch" };

        os.IsCompatibleWith( es, out var reason ).Should().BeFalse();
        reason.Should().Contain( "distribution" );
        reason.Should().Contain( "opensearch" );
        reason.Should().Contain( "elasticsearch" );
    }

    [TestMethod]
    public void IsCompatibleWith_DistributionCaseInsensitive()
    {
        // Case variants of the same distribution are equivalent.
        var a = BaselineSignature() with { Distribution = "OpenSearch" };
        var b = BaselineSignature() with { Distribution = "opensearch" };

        a.IsCompatibleWith( b, out var reason ).Should().BeTrue();
        reason.Should().BeNullOrEmpty();
    }

    [TestMethod]
    public void IsCompatibleWith_DifferentIsmPrefix_Incompatible()
    {
        // Modern (`_plugins/_ism`) vs legacy (`_opendistro/_ism`) imply
        // different server generations. The squash output may reference
        // policies via one prefix; replaying against a cluster exposing
        // the other will fail.
        var modern = BaselineSignature();
        var legacy = modern with { IsmPathPrefix = "_opendistro/_ism" };

        modern.IsCompatibleWith( legacy, out var reason ).Should().BeFalse();
        reason.Should().Contain( "ism_path_prefix" );
        reason.Should().Contain( "_plugins/_ism" );
        reason.Should().Contain( "_opendistro/_ism" );
    }

    [TestMethod]
    public void IsCompatibleWith_DifferentClusterName_Incompatible()
    {
        var a = BaselineSignature();
        var b = a with { ClusterName = "production-cluster" };

        a.IsCompatibleWith( b, out var reason ).Should().BeFalse();
        reason.Should().Contain( "cluster_name" );
    }

    [TestMethod]
    public void IsCompatibleWith_PluginAddedToSource_Incompatible()
    {
        // The squash-source cluster has k-NN; the target lacks it. The
        // squash output may reference k-NN mapping types; replaying silently
        // passes structural compare but fails at runtime when the unknown
        // mapping type lands.
        var sourceWithKnn = BaselineSignature() with
        {
            Plugins = new[]
            {
                "opensearch-index-management",
                "opensearch-knn",
                "opensearch-security"
            }
        };
        var targetWithoutKnn = BaselineSignature();

        sourceWithKnn.IsCompatibleWith( targetWithoutKnn, out var reason ).Should().BeFalse();
        reason.Should().Contain( "plugin set" );
        reason.Should().Contain( "opensearch-knn" );
    }

    [TestMethod]
    public void IsCompatibleWith_PluginRemovedFromSource_Incompatible()
    {
        var sourceMinimal = BaselineSignature() with
        {
            Plugins = new[] { "opensearch-index-management" }
        };
        var targetFull = BaselineSignature();

        sourceMinimal.IsCompatibleWith( targetFull, out var reason ).Should().BeFalse();
        reason.Should().Contain( "plugin set" );
        reason.Should().Contain( "opensearch-security" );
    }

    [TestMethod]
    public void IsCompatibleWith_PluginOrderingIndependent()
    {
        var a = BaselineSignature() with
        {
            Plugins = new[] { "opensearch-index-management", "opensearch-security" }
        };
        var b = BaselineSignature() with
        {
            Plugins = new[] { "opensearch-security", "opensearch-index-management" }
        };

        a.IsCompatibleWith( b, out var reason ).Should().BeTrue();
        reason.Should().BeNullOrEmpty();
    }

    [TestMethod]
    public void IsCompatibleWith_NodeCountMismatch_StillCompatible()
    {
        // Node count is captured for diagnostic context but does NOT gate
        // compatibility. A 1-node test cluster's squash output should be
        // valid against a 3-node production cluster as long as the plugin
        // matrix and distribution match.
        var a = BaselineSignature();
        var b = a with { NodeCount = 3 };

        a.IsCompatibleWith( b, out var reason ).Should().BeTrue();
        reason.Should().BeNullOrEmpty();
    }

    [TestMethod]
    public void IsCompatibleWith_CrossProvider_Incompatible()
    {
        var os = BaselineSignature();
        var aerospike = new AerospikeTopologySignature
        {
            ServerMajor = 6,
            ServerMinor = 4,
            Namespace = "test",
            Edition = "Community"
        };

        os.IsCompatibleWith( aerospike, out var reason ).Should().BeFalse();
        reason.Should().Contain( "aerospike" );
        reason.Should().Contain( "opensearch" );
    }

    [TestMethod]
    public void Properties_ExposesAllAxesAsStrings()
    {
        var sig = BaselineSignature();

        sig.Properties["server_major"].Should().Be( "2" );
        sig.Properties["server_minor"].Should().Be( "13" );
        sig.Properties["distribution"].Should().Be( "opensearch" );
        sig.Properties["cluster_name"].Should().Be( "test-cluster" );
        sig.Properties["node_count"].Should().Be( "1" );
        sig.Properties["ism_path_prefix"].Should().Be( "_plugins/_ism" );

        // Plugins emit as sorted comma-separated for byte-stable Properties
        // serialization regardless of input order.
        sig.Properties["plugins"].Should().Be( "opensearch-index-management,opensearch-security" );
    }

    // ---- internal helper tests ---------------------------------------------

    [TestMethod]
    public void ParseRootResponse_OpenSearchDistribution()
    {
        const string body = """
            {
              "name": "node-1",
              "cluster_name": "docker-cluster",
              "cluster_uuid": "abc",
              "version": {
                "distribution": "opensearch",
                "number": "2.13.0",
                "build_type": "tar",
                "build_hash": "...",
                "build_date": "..."
              },
              "tagline": "The OpenSearch Project: https://opensearch.org/"
            }
            """;

        var (major, minor, distribution) = OpenSearchTopologySignature.ParseRootResponse( body );

        major.Should().Be( 2 );
        minor.Should().Be( 13 );
        distribution.Should().Be( "opensearch" );
    }

    [TestMethod]
    public void ParseRootResponse_MissingDistribution_DefaultsToElasticsearch()
    {
        // Pre-fork Elasticsearch lacks the `distribution` field. Falling back
        // to "elasticsearch" ensures cross-fork attempts fail compat.
        const string body = """
            {
              "name": "node-1",
              "version": { "number": "7.17.0" }
            }
            """;

        var (major, minor, distribution) = OpenSearchTopologySignature.ParseRootResponse( body );

        major.Should().Be( 7 );
        minor.Should().Be( 17 );
        distribution.Should().Be( "elasticsearch" );
    }

    [TestMethod]
    public void ParseRootResponse_NormalizesDistributionToLowercase()
    {
        const string body = """
            { "version": { "distribution": "OpenSearch", "number": "2.13.0" } }
            """;

        var (_, _, distribution) = OpenSearchTopologySignature.ParseRootResponse( body );
        distribution.Should().Be( "opensearch" );
    }

    [TestMethod]
    public void ParseRootResponse_EmptyBody_Throws()
    {
        Action act = () => OpenSearchTopologySignature.ParseRootResponse( "" );
        act.Should().Throw<MigrationException>().WithMessage( "*empty*" );
    }

    [TestMethod]
    public void ParseRootResponse_MissingVersion_Throws()
    {
        Action act = () => OpenSearchTopologySignature.ParseRootResponse( """{"name":"x"}""" );
        act.Should().Throw<MigrationException>().WithMessage( "*version*" );
    }

    [TestMethod]
    public void ParseRootResponse_MissingVersionNumber_Throws()
    {
        Action act = () => OpenSearchTopologySignature.ParseRootResponse(
            """{"version":{"distribution":"opensearch"}}""" );
        act.Should().Throw<MigrationException>().WithMessage( "*number*" );
    }

    [TestMethod]
    public void ParseVersionNumber_DottedRelease()
    {
        OpenSearchTopologySignature.ParseVersionNumber( "2.13.0" ).Should().Be( (2, 13) );
        OpenSearchTopologySignature.ParseVersionNumber( "1.3.18" ).Should().Be( (1, 3) );
    }

    [TestMethod]
    public void ParseVersionNumber_SnapshotSuffix_StripsAndParses()
    {
        OpenSearchTopologySignature.ParseVersionNumber( "2.13.0-SNAPSHOT" ).Should().Be( (2, 13) );
        OpenSearchTopologySignature.ParseVersionNumber( "3.0.0-alpha1" ).Should().Be( (3, 0) );
    }

    [TestMethod]
    public void ParseVersionNumber_Malformed_Throws()
    {
        Action act = () => OpenSearchTopologySignature.ParseVersionNumber( "garbage" );
        act.Should().Throw<MigrationException>().WithMessage( "*recognized format*" );

        Action act2 = () => OpenSearchTopologySignature.ParseVersionNumber( "2" );
        act2.Should().Throw<MigrationException>();
    }

    [TestMethod]
    public void ParsePluginsResponse_DedupAndSort()
    {
        // Per-node response: same plugin listed once per node. Output must
        // dedupe to a single ordinally-sorted union.
        const string body = """
            [
              {"name": "node-2", "component": "opensearch-security", "version": "2.13.0.0"},
              {"name": "node-1", "component": "opensearch-security", "version": "2.13.0.0"},
              {"name": "node-1", "component": "opensearch-index-management", "version": "2.13.0.0"},
              {"name": "node-2", "component": "opensearch-knn", "version": "2.13.0.0"}
            ]
            """;

        var plugins = OpenSearchTopologySignature.ParsePluginsResponse( body );

        plugins.Should().Equal(
            "opensearch-index-management",
            "opensearch-knn",
            "opensearch-security" );
    }

    [TestMethod]
    public void ParsePluginsResponse_EmptyArray_ReturnsEmpty()
    {
        OpenSearchTopologySignature.ParsePluginsResponse( "[]" ).Should().BeEmpty();
    }

    [TestMethod]
    public void ParsePluginsResponse_EmptyBody_ReturnsEmpty()
    {
        OpenSearchTopologySignature.ParsePluginsResponse( "" ).Should().BeEmpty();
        OpenSearchTopologySignature.ParsePluginsResponse( null ).Should().BeEmpty();
    }

    [TestMethod]
    public void ParsePluginsResponse_NonArrayBody_ReturnsEmpty()
    {
        // Defensive: a malformed cluster response that returns an object
        // instead of an array should yield empty plugin list rather than
        // throw -- the topology compare layer still emits a useful diagnostic.
        OpenSearchTopologySignature.ParsePluginsResponse( """{"error":"forbidden"}""" ).Should().BeEmpty();
    }

    [TestMethod]
    public void ParsePluginsResponse_EntriesMissingComponent_AreSkipped()
    {
        const string body = """
            [
              {"name": "node-1", "version": "2.13.0.0"},
              {"name": "node-1", "component": "opensearch-security"},
              {"name": "node-1", "component": ""}
            ]
            """;

        var plugins = OpenSearchTopologySignature.ParsePluginsResponse( body );
        plugins.Should().Equal( "opensearch-security" );
    }

    [TestMethod]
    public async Task CaptureAsync_NullClient_Throws()
    {
        Func<Task> act = () => OpenSearchTopologySignature.CaptureAsync( null! );
        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName( "client" );
    }

    [TestMethod]
    public async Task CaptureAsync_CancelledToken_Throws()
    {
        var client = global::NSubstitute.Substitute.For<global::OpenSearch.Client.IOpenSearchClient>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => OpenSearchTopologySignature.CaptureAsync( client, cts.Token );
        await act.Should().ThrowAsync<OperationCanceledException>();
    }
}
