using System.Text.Json.Nodes;
using FluentAssertions;
using Hyperbee.Migrations.Providers.Couchbase.Squash;
using Hyperbee.Migrations.Providers.MongoDB.Squash;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 4 Task 4.1: CouchbaseTopologySignature unit coverage.
//
// Live CaptureAsync against a cluster is exercised in the Phase 4 integration
// suite. These tests cover IsCompatibleWith rules + internal parser helpers
// (cluster details JSON, version string, bucket details, edition
// classification via isEnterprise / version suffix fallback).

[TestClass]
public class CouchbaseTopologySignatureTests
{
    private static CouchbaseTopologySignature BaselineSignature() => new()
    {
        ServerMajor = 7,
        ServerMinor = 2,
        Edition = "Enterprise",
        Services = new[] { "fts", "index", "kv", "n1ql" },
        BucketName = "appbucket",
        BucketType = "membase",
        StorageBackend = "couchstore",
        ReplicaCount = 1,
        MemoryQuotaMB = 256
    };

    [TestMethod]
    public void SchemaVersion_AndProviderId_AreStable()
    {
        var sig = BaselineSignature();
        sig.SchemaVersion.Should().Be( 1 );
        sig.ProviderId.Should().Be( "couchbase" );
        CouchbaseTopologySignature.ProviderIdValue.Should().Be( "couchbase" );
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
        var b = a with { ServerMinor = 5 };
        a.IsCompatibleWith( b, out var reason ).Should().BeTrue();
        reason.Should().BeNullOrEmpty();
    }

    [TestMethod]
    public void IsCompatibleWith_DifferentEdition_Incompatible()
    {
        // EE-source applied to CE target: silent corruption risk if Magma /
        // XDCR / eventing / analytics features are in the squash. Hard fail.
        var a = BaselineSignature();
        var b = a with { Edition = "Community" };
        a.IsCompatibleWith( b, out var reason ).Should().BeFalse();
        reason.Should().Contain( "edition" );
        reason.Should().Contain( "Enterprise" );
        reason.Should().Contain( "Community" );
    }

    [TestMethod]
    public void IsCompatibleWith_EditionCaseInsensitive()
    {
        var a = BaselineSignature() with { Edition = "enterprise" };
        var b = BaselineSignature() with { Edition = "ENTERPRISE" };
        a.IsCompatibleWith( b, out var reason ).Should().BeTrue();
        reason.Should().BeNullOrEmpty();
    }

    [TestMethod]
    public void IsCompatibleWith_DifferentBucketName_Incompatible()
    {
        var a = BaselineSignature();
        var b = a with { BucketName = "other" };
        a.IsCompatibleWith( b, out var reason ).Should().BeFalse();
        reason.Should().Contain( "bucket_name" );
    }

    [TestMethod]
    public void IsCompatibleWith_DifferentBucketType_Incompatible()
    {
        var a = BaselineSignature();
        var b = a with { BucketType = "ephemeral" };
        a.IsCompatibleWith( b, out var reason ).Should().BeFalse();
        reason.Should().Contain( "bucket_type" );
    }

    [TestMethod]
    public void IsCompatibleWith_DifferentStorageBackend_Incompatible()
    {
        var a = BaselineSignature();
        var b = a with { StorageBackend = "magma" };
        a.IsCompatibleWith( b, out var reason ).Should().BeFalse();
        reason.Should().Contain( "storage_backend" );
    }

    [TestMethod]
    public void IsCompatibleWith_DifferentReplicaCount_Incompatible()
    {
        var a = BaselineSignature();
        var b = a with { ReplicaCount = 2 };
        a.IsCompatibleWith( b, out var reason ).Should().BeFalse();
        reason.Should().Contain( "replica_count" );
    }

    [TestMethod]
    public void IsCompatibleWith_DifferentMemoryQuota_Incompatible()
    {
        var a = BaselineSignature();
        var b = a with { MemoryQuotaMB = 512 };
        a.IsCompatibleWith( b, out var reason ).Should().BeFalse();
        reason.Should().Contain( "memory_quota_mb" );
    }

    [TestMethod]
    public void IsCompatibleWith_DifferentServices_Incompatible()
    {
        // Source has eventing + analytics; target lacks them. Squash may
        // reference eventing functions or analytics datasets; will fail
        // on apply.
        var sourceFull = BaselineSignature() with
        {
            Services = new[] { "analytics", "eventing", "fts", "index", "kv", "n1ql" }
        };
        var targetMinimal = BaselineSignature();

        sourceFull.IsCompatibleWith( targetMinimal, out var reason ).Should().BeFalse();
        reason.Should().Contain( "services set" );
        reason.Should().Contain( "eventing" );
    }

    [TestMethod]
    public void IsCompatibleWith_ServicesOrderingIndependent()
    {
        var a = BaselineSignature() with
        {
            Services = new[] { "kv", "n1ql", "index", "fts" }
        };
        var b = BaselineSignature() with
        {
            Services = new[] { "fts", "kv", "index", "n1ql" }
        };

        a.IsCompatibleWith( b, out var reason ).Should().BeTrue();
        reason.Should().BeNullOrEmpty();
    }

    [TestMethod]
    public void IsCompatibleWith_CrossProvider_Incompatible()
    {
        var cb = BaselineSignature();
        var mongo = new MongoDBTopologySignature
        {
            ServerMajor = 7,
            ServerMinor = 0,
            FeatureCompatibilityVersion = "7.0",
            DeploymentTopology = "Standalone",
            DatabaseName = "appdb",
            DefaultReadConcern = "local",
            DefaultWriteConcern = "1",
            StorageEngine = "wiredTiger"
        };

        cb.IsCompatibleWith( mongo, out var reason ).Should().BeFalse();
        reason.Should().Contain( "mongodb" );
        reason.Should().Contain( "couchbase" );
    }

    [TestMethod]
    public void Properties_ExposesAllAxesAsStrings()
    {
        var sig = BaselineSignature();

        sig.Properties["server_major"].Should().Be( "7" );
        sig.Properties["server_minor"].Should().Be( "2" );
        sig.Properties["edition"].Should().Be( "Enterprise" );
        // Services emit sorted comma-joined for byte-stable Properties serialization.
        sig.Properties["services"].Should().Be( "fts,index,kv,n1ql" );
        sig.Properties["bucket_name"].Should().Be( "appbucket" );
        sig.Properties["bucket_type"].Should().Be( "membase" );
        sig.Properties["storage_backend"].Should().Be( "couchstore" );
        sig.Properties["replica_count"].Should().Be( "1" );
        sig.Properties["memory_quota_mb"].Should().Be( "256" );
    }

    // ---- internal parser tests ---------------------------------------------

    [TestMethod]
    public void ParseClusterDetails_StandardEnterpriseShape()
    {
        var json = JsonNode.Parse( """
            {
              "isEnterprise": true,
              "nodes": [
                {
                  "version": "7.2.0-5325-enterprise",
                  "services": ["kv", "n1ql", "index", "fts"]
                }
              ]
            }
            """ );

        var (major, minor, edition) = CouchbaseTopologySignature.ParseClusterDetails( json );

        major.Should().Be( 7 );
        minor.Should().Be( 2 );
        edition.Should().Be( "Enterprise" );
    }

    [TestMethod]
    public void ParseClusterDetails_StandardCommunityShape()
    {
        var json = JsonNode.Parse( """
            {
              "isEnterprise": false,
              "nodes": [{ "version": "7.2.0-5325-community", "services": ["kv","n1ql"] }]
            }
            """ );

        var (_, _, edition) = CouchbaseTopologySignature.ParseClusterDetails( json );
        edition.Should().Be( "Community" );
    }

    [TestMethod]
    public void ParseClusterDetails_MissingIsEnterprise_FallsBackToVersionSuffix()
    {
        // Some older Couchbase versions omit `isEnterprise`. Fall back to
        // parsing the version-string suffix.
        var json = JsonNode.Parse( """
            {
              "nodes": [{ "version": "6.6.0-7909-enterprise", "services": ["kv"] }]
            }
            """ );

        var (major, minor, edition) = CouchbaseTopologySignature.ParseClusterDetails( json );
        major.Should().Be( 6 );
        minor.Should().Be( 6 );
        edition.Should().Be( "Enterprise" );
    }

    [TestMethod]
    public void ParseClusterDetails_VersionWithoutSuffix_EditionEmpty()
    {
        var json = JsonNode.Parse( """
            { "nodes": [{ "version": "7.2.0", "services": ["kv"] }] }
            """ );

        var (_, _, edition) = CouchbaseTopologySignature.ParseClusterDetails( json );
        edition.Should().Be( "" );
    }

    [TestMethod]
    public void ParseClusterDetails_NonObject_Throws()
    {
        var json = JsonNode.Parse( "[]" );

        Action act = () => CouchbaseTopologySignature.ParseClusterDetails( json );
        act.Should().Throw<MigrationException>().WithMessage( "*JSON object*" );
    }

    [TestMethod]
    public void ParseVersionString_Dotted()
    {
        CouchbaseTopologySignature.ParseVersionString( "7.2.0-5325-enterprise" ).Should().Be( (7, 2) );
        CouchbaseTopologySignature.ParseVersionString( "6.6.0" ).Should().Be( (6, 6) );
    }

    [TestMethod]
    public void ParseVersionString_Malformed_Throws()
    {
        Action act = () => CouchbaseTopologySignature.ParseVersionString( "garbage" );
        act.Should().Throw<MigrationException>().WithMessage( "*recognized format*" );
    }

    [TestMethod]
    public void NormalizeEditionFromVersion_RecognizesSuffixes()
    {
        CouchbaseTopologySignature.NormalizeEditionFromVersion( "7.2.0-5325-enterprise" ).Should().Be( "Enterprise" );
        CouchbaseTopologySignature.NormalizeEditionFromVersion( "7.2.0-5325-community" ).Should().Be( "Community" );
        CouchbaseTopologySignature.NormalizeEditionFromVersion( "7.2.0" ).Should().Be( "" );
        CouchbaseTopologySignature.NormalizeEditionFromVersion( "" ).Should().Be( "" );
        CouchbaseTopologySignature.NormalizeEditionFromVersion( null ).Should().Be( "" );
    }

    [TestMethod]
    public void ParseServices_UnionAcrossNodesSortedAndDeduped()
    {
        // Multi-node cluster with services split across nodes (typical
        // MDS deployment). The union should be sorted and deduped.
        var json = (JsonObject) JsonNode.Parse( """
            {
              "nodes": [
                { "services": ["kv", "n1ql"] },
                { "services": ["index", "kv"] },
                { "services": ["fts"] }
              ]
            }
            """ );

        var services = CouchbaseTopologySignature.ParseServices( json );

        services.Should().Equal( "fts", "index", "kv", "n1ql" );
    }

    [TestMethod]
    public void ParseServices_NoNodes_ReturnsEmpty()
    {
        var json = (JsonObject) JsonNode.Parse( "{}" );
        CouchbaseTopologySignature.ParseServices( json ).Should().BeEmpty();
    }

    [TestMethod]
    public void ParseServices_NodeWithoutServices_Skipped()
    {
        var json = (JsonObject) JsonNode.Parse( """
            { "nodes": [{ "services": ["kv"] }, { "version": "7.2.0" }] }
            """ );

        CouchbaseTopologySignature.ParseServices( json ).Should().Equal( "kv" );
    }

    [TestMethod]
    public void ParseBucketDetails_StandardShape()
    {
        var json = JsonNode.Parse( """
            {
              "bucketType": "membase",
              "storageBackend": "couchstore",
              "replicaNumber": 1,
              "quota": { "ram": 268435456, "rawRAM": 268435456 }
            }
            """ );

        var (bucketType, storageBackend, replicaCount, memoryQuotaMB) =
            CouchbaseTopologySignature.ParseBucketDetails( json );

        bucketType.Should().Be( "membase" );
        storageBackend.Should().Be( "couchstore" );
        replicaCount.Should().Be( 1 );
        memoryQuotaMB.Should().Be( 256 ); // 268435456 / (1024 * 1024) = 256
    }

    [TestMethod]
    public void ParseBucketDetails_MissingStorageBackend_ReturnsEmpty()
    {
        // CE buckets don't report `storageBackend` (always Couchstore).
        var json = JsonNode.Parse( """
            {
              "bucketType": "membase",
              "replicaNumber": 1,
              "quota": { "ram": 134217728 }
            }
            """ );

        var (_, storageBackend, _, memoryQuotaMB) =
            CouchbaseTopologySignature.ParseBucketDetails( json );

        storageBackend.Should().Be( "" );
        memoryQuotaMB.Should().Be( 128 );
    }

    [TestMethod]
    public void ParseBucketDetails_EphemeralBucket()
    {
        var json = JsonNode.Parse( """
            { "bucketType": "ephemeral", "replicaNumber": 0, "quota": { "ram": 134217728 } }
            """ );

        var (bucketType, _, replicaCount, _) =
            CouchbaseTopologySignature.ParseBucketDetails( json );

        bucketType.Should().Be( "ephemeral" );
        replicaCount.Should().Be( 0 );
    }

    [TestMethod]
    public void ParseBucketDetails_NonObject_Throws()
    {
        var json = JsonNode.Parse( "[]" );
        Action act = () => CouchbaseTopologySignature.ParseBucketDetails( json );
        act.Should().Throw<MigrationException>().WithMessage( "*JSON object*" );
    }

    [TestMethod]
    public async Task CaptureAsync_NullRestApi_Throws()
    {
        Func<Task> act = () => CouchbaseTopologySignature.CaptureAsync( null, "bucket" );
        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName( "restApi" );
    }

    [TestMethod]
    public async Task CaptureAsync_EmptyBucketName_Throws()
    {
        var restApi = global::NSubstitute.Substitute.For<global::Hyperbee.Migrations.Providers.Couchbase.Services.ICouchbaseRestApiService>();
        Func<Task> act = () => CouchbaseTopologySignature.CaptureAsync( restApi, "" );
        await act.Should().ThrowAsync<ArgumentException>().WithParameterName( "bucketName" );
    }
}
