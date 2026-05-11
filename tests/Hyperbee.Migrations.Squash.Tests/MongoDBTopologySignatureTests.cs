using FluentAssertions;
using Hyperbee.Migrations.Providers.MongoDB.Squash;
using Hyperbee.Migrations.Providers.OpenSearch.Squash;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MongoDB.Bson;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 3 Task 3.1: MongoDBTopologySignature unit coverage.
//
// Live CaptureAsync against a cluster is exercised in the Phase 3 integration
// suite (Testcontainers MongoDB). These tests cover:
//   - IsCompatibleWith comparison rules across all axes
//   - Cross-provider rejection
//   - Internal parser helpers (versionArray + version-string fallback,
//     hello deployment-topology classification, FCV extraction,
//     storageEngine extraction)

[TestClass]
public class MongoDBTopologySignatureTests
{
    private static MongoDBTopologySignature BaselineSignature() => new()
    {
        ServerMajor = 7,
        ServerMinor = 0,
        FeatureCompatibilityVersion = "7.0",
        DeploymentTopology = "Standalone",
        ReplicaSetName = "",
        DatabaseName = "appdb",
        DefaultReadConcern = "local",
        DefaultWriteConcern = "1",
        StorageEngine = "wiredTiger"
    };

    [TestMethod]
    public void SchemaVersion_AndProviderId_AreStable()
    {
        var sig = BaselineSignature();
        sig.SchemaVersion.Should().Be( 1 );
        sig.ProviderId.Should().Be( "mongodb" );
        MongoDBTopologySignature.ProviderIdValue.Should().Be( "mongodb" );
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
        var b = a with { ServerMajor = 8 };

        a.IsCompatibleWith( b, out var reason ).Should().BeFalse();
        reason.Should().Contain( "server_major" );
    }

    [TestMethod]
    public void IsCompatibleWith_DifferentMinor_StillCompatible()
    {
        var a = BaselineSignature();
        var b = a with { ServerMinor = 4 };

        a.IsCompatibleWith( b, out var reason ).Should().BeTrue();
        reason.Should().BeNullOrEmpty();
    }

    [TestMethod]
    public void IsCompatibleWith_DifferentFcv_Incompatible()
    {
        // FCV gates index features, aggregation operators, BSON shape.
        // Different FCV is a hard incompatibility.
        var a = BaselineSignature();
        var b = a with { FeatureCompatibilityVersion = "6.0" };

        a.IsCompatibleWith( b, out var reason ).Should().BeFalse();
        reason.Should().Contain( "fcv" );
        reason.Should().Contain( "7.0" );
        reason.Should().Contain( "6.0" );
    }

    [TestMethod]
    public void IsCompatibleWith_DifferentDeploymentTopology_Incompatible()
    {
        // Standalone vs ReplicaSet vs Sharded differ in write concerns,
        // index build mechanics, and available operators.
        var a = BaselineSignature();
        var b = a with { DeploymentTopology = "ReplicaSet" };

        a.IsCompatibleWith( b, out var reason ).Should().BeFalse();
        reason.Should().Contain( "deployment_topology" );
    }

    [TestMethod]
    public void IsCompatibleWith_DeploymentTopologyCaseInsensitive()
    {
        var a = BaselineSignature() with { DeploymentTopology = "standalone" };
        var b = BaselineSignature() with { DeploymentTopology = "STANDALONE" };

        a.IsCompatibleWith( b, out var reason ).Should().BeTrue();
        reason.Should().BeNullOrEmpty();
    }

    [TestMethod]
    public void IsCompatibleWith_DifferentDatabaseName_Incompatible()
    {
        var a = BaselineSignature();
        var b = a with { DatabaseName = "otherdb" };

        a.IsCompatibleWith( b, out var reason ).Should().BeFalse();
        reason.Should().Contain( "database_name" );
    }

    [TestMethod]
    public void IsCompatibleWith_DifferentStorageEngine_Incompatible()
    {
        var a = BaselineSignature();
        var b = a with { StorageEngine = "inMemory" };

        a.IsCompatibleWith( b, out var reason ).Should().BeFalse();
        reason.Should().Contain( "storage_engine" );
    }

    [TestMethod]
    public void IsCompatibleWith_DifferentReadConcern_Incompatible()
    {
        var a = BaselineSignature();
        var b = a with { DefaultReadConcern = "majority" };

        a.IsCompatibleWith( b, out var reason ).Should().BeFalse();
        reason.Should().Contain( "default_read_concern" );
    }

    [TestMethod]
    public void IsCompatibleWith_DifferentWriteConcern_Incompatible()
    {
        var a = BaselineSignature();
        var b = a with { DefaultWriteConcern = "majority" };

        a.IsCompatibleWith( b, out var reason ).Should().BeFalse();
        reason.Should().Contain( "default_write_concern" );
    }

    [TestMethod]
    public void IsCompatibleWith_DifferentReplicaSetName_StillCompatible()
    {
        // ReplicaSetName is captured for diagnostics but NOT used by
        // IsCompatibleWith -- the same logical replica set can have
        // different names across environments.
        var a = BaselineSignature() with { DeploymentTopology = "ReplicaSet", ReplicaSetName = "rs0" };
        var b = a with { ReplicaSetName = "rs-prod" };

        a.IsCompatibleWith( b, out var reason ).Should().BeTrue();
        reason.Should().BeNullOrEmpty();
    }

    [TestMethod]
    public void IsCompatibleWith_CrossProvider_Incompatible()
    {
        var mongo = BaselineSignature();
        var opensearch = new OpenSearchTopologySignature
        {
            ServerMajor = 2,
            ServerMinor = 13,
            Distribution = "opensearch",
            ClusterName = "test",
            NodeCount = 1,
            Plugins = Array.Empty<string>(),
            IsmPathPrefix = "_plugins/_ism"
        };

        mongo.IsCompatibleWith( opensearch, out var reason ).Should().BeFalse();
        reason.Should().Contain( "opensearch" );
        reason.Should().Contain( "mongodb" );
    }

    [TestMethod]
    public void Properties_ExposesAllAxesAsStrings()
    {
        var sig = BaselineSignature();

        sig.Properties["server_major"].Should().Be( "7" );
        sig.Properties["server_minor"].Should().Be( "0" );
        sig.Properties["fcv"].Should().Be( "7.0" );
        sig.Properties["deployment_topology"].Should().Be( "Standalone" );
        sig.Properties["database_name"].Should().Be( "appdb" );
        sig.Properties["default_read_concern"].Should().Be( "local" );
        sig.Properties["default_write_concern"].Should().Be( "1" );
        sig.Properties["storage_engine"].Should().Be( "wiredTiger" );
    }

    // ---- internal helper tests ---------------------------------------------

    [TestMethod]
    public void ParseVersionFromBuildInfo_PrefersVersionArray()
    {
        // versionArray is the authoritative numeric form when both fields
        // are present.
        var buildInfo = new BsonDocument
        {
            { "versionArray", new BsonArray { 7, 0, 5, 0 } },
            { "version", "7.0.5" }
        };

        MongoDBTopologySignature.ParseVersionFromBuildInfo( buildInfo ).Should().Be( (7, 0) );
    }

    [TestMethod]
    public void ParseVersionFromBuildInfo_FallsBackToVersionString()
    {
        // When versionArray is absent or malformed, fall back to version string.
        var buildInfo = new BsonDocument { { "version", "6.0.12" } };

        MongoDBTopologySignature.ParseVersionFromBuildInfo( buildInfo ).Should().Be( (6, 0) );
    }

    [TestMethod]
    public void ParseVersionFromBuildInfo_NeitherField_Throws()
    {
        var buildInfo = new BsonDocument { { "build_environment", new BsonDocument() } };

        Action act = () => MongoDBTopologySignature.ParseVersionFromBuildInfo( buildInfo );
        act.Should().Throw<MigrationException>().WithMessage( "*versionArray*" );
    }

    [TestMethod]
    public void ParseVersionString_DottedRelease()
    {
        MongoDBTopologySignature.ParseVersionString( "7.0.5" ).Should().Be( (7, 0) );
        MongoDBTopologySignature.ParseVersionString( "6.0.12" ).Should().Be( (6, 0) );
    }

    [TestMethod]
    public void ParseVersionString_PrereleaseSuffix_StripsAndParses()
    {
        MongoDBTopologySignature.ParseVersionString( "7.0.0-rc0" ).Should().Be( (7, 0) );
        MongoDBTopologySignature.ParseVersionString( "8.0.0-alpha1" ).Should().Be( (8, 0) );
    }

    [TestMethod]
    public void ParseVersionString_Malformed_Throws()
    {
        Action act = () => MongoDBTopologySignature.ParseVersionString( "garbage" );
        act.Should().Throw<MigrationException>().WithMessage( "*recognized format*" );

        Action act2 = () => MongoDBTopologySignature.ParseVersionString( "7" );
        act2.Should().Throw<MigrationException>();
    }

    [TestMethod]
    public void ParseDeploymentFromHello_SetName_ReturnsReplicaSet()
    {
        var hello = new BsonDocument
        {
            { "isWritablePrimary", true },
            { "setName", "rs0" }
        };

        var (topology, replicaSetName) = MongoDBTopologySignature.ParseDeploymentFromHello( hello );

        topology.Should().Be( "ReplicaSet" );
        replicaSetName.Should().Be( "rs0" );
    }

    [TestMethod]
    public void ParseDeploymentFromHello_IsdbgridMsg_ReturnsSharded()
    {
        var hello = new BsonDocument
        {
            { "isWritablePrimary", true },
            { "msg", "isdbgrid" }
        };

        var (topology, replicaSetName) = MongoDBTopologySignature.ParseDeploymentFromHello( hello );

        topology.Should().Be( "Sharded" );
        replicaSetName.Should().Be( "" );
    }

    [TestMethod]
    public void ParseDeploymentFromHello_NoSetNameNoMsg_ReturnsStandalone()
    {
        var hello = new BsonDocument { { "isWritablePrimary", true } };

        var (topology, replicaSetName) = MongoDBTopologySignature.ParseDeploymentFromHello( hello );

        topology.Should().Be( "Standalone" );
        replicaSetName.Should().Be( "" );
    }

    [TestMethod]
    public void ParseDeploymentFromHello_ShardedTakesPrecedenceOverReplicaSet()
    {
        // A mongos response includes both msg=isdbgrid AND setName (the
        // config server's replica set). Classify as Sharded -- it's the
        // operational mode that matters for squash compatibility.
        var hello = new BsonDocument
        {
            { "isWritablePrimary", true },
            { "msg", "isdbgrid" },
            { "setName", "configReplSet" }
        };

        var (topology, replicaSetName) = MongoDBTopologySignature.ParseDeploymentFromHello( hello );

        topology.Should().Be( "Sharded" );
        replicaSetName.Should().Be( "" );
    }

    [TestMethod]
    public void ParseFcv_StandardShape_ExtractsVersion()
    {
        var doc = new BsonDocument
        {
            { "featureCompatibilityVersion", new BsonDocument { { "version", "7.0" } } },
            { "ok", 1.0 }
        };

        MongoDBTopologySignature.ParseFcv( doc ).Should().Be( "7.0" );
    }

    [TestMethod]
    public void ParseFcv_MissingField_ReturnsEmpty()
    {
        var doc = new BsonDocument { { "ok", 1.0 } };

        MongoDBTopologySignature.ParseFcv( doc ).Should().Be( "" );
    }

    [TestMethod]
    public void ParseFcv_NullDoc_ReturnsEmpty()
    {
        MongoDBTopologySignature.ParseFcv( null ).Should().Be( "" );
    }

    [TestMethod]
    public void ParseStorageEngine_StandardShape_ExtractsName()
    {
        var statusDoc = new BsonDocument
        {
            { "storageEngine", new BsonDocument { { "name", "wiredTiger" }, { "supportsCommittedReads", true } } }
        };

        MongoDBTopologySignature.ParseStorageEngine( statusDoc ).Should().Be( "wiredTiger" );
    }

    [TestMethod]
    public void ParseStorageEngine_MissingField_ReturnsEmpty()
    {
        var statusDoc = new BsonDocument { { "ok", 1.0 } };

        MongoDBTopologySignature.ParseStorageEngine( statusDoc ).Should().Be( "" );
    }

    [TestMethod]
    public void ParseStorageEngine_NullDoc_ReturnsEmpty()
    {
        MongoDBTopologySignature.ParseStorageEngine( null ).Should().Be( "" );
    }

    [TestMethod]
    public async Task CaptureAsync_NullClient_Throws()
    {
        Func<Task> act = () => MongoDBTopologySignature.CaptureAsync( null!, "appdb" );
        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName( "client" );
    }

    [TestMethod]
    public async Task CaptureAsync_EmptyDatabaseName_Throws()
    {
        var client = global::NSubstitute.Substitute.For<global::MongoDB.Driver.IMongoClient>();
        Func<Task> act = () => MongoDBTopologySignature.CaptureAsync( client, "" );
        await act.Should().ThrowAsync<ArgumentException>().WithParameterName( "databaseName" );
    }
}
