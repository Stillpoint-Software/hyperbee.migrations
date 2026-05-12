using FluentAssertions;
using Hyperbee.Migrations.Providers.Aerospike.Squash;
using Hyperbee.Migrations.Providers.Postgres.Squash;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 1 (R-P1, R-P8): unit coverage for AerospikeTopologySignature.
//
// Live CaptureAsync against a cluster is exercised in the Phase 1 integration
// suite (Testcontainers Aerospike). These tests cover the pure-logic paths:
// IsCompatibleWith comparison rules and the internal helpers that parse the
// info-protocol responses.

[TestClass]
public class AerospikeTopologySignatureTests
{
    private static AerospikeTopologySignature BaselineSignature() => new()
    {
        ServerMajor = 6,
        ServerMinor = 4,
        Namespace = "test",
        ReplicationFactor = 2,
        DefaultTtl = 2592000,
        NsupPeriod = 120,
        MemorySize = 1073741824L,
        StorageEngine = "memory",
        ClusterName = "null",
        Edition = "Community"
    };

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
        var b = a with { ServerMajor = 7 };

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
    public void IsCompatibleWith_DifferentNamespace_Incompatible()
    {
        var a = BaselineSignature();
        var b = a with { Namespace = "prod" };

        a.IsCompatibleWith( b, out var reason ).Should().BeFalse();
        reason.Should().Contain( "namespace" );
    }

    [TestMethod]
    public void IsCompatibleWith_DifferentReplicationFactor_Incompatible()
    {
        var a = BaselineSignature();
        var b = a with { ReplicationFactor = 3 };

        a.IsCompatibleWith( b, out var reason ).Should().BeFalse();
        reason.Should().Contain( "replication_factor" );
    }

    [TestMethod]
    public void IsCompatibleWith_DifferentStorageEngine_Incompatible()
    {
        var a = BaselineSignature();
        var b = a with { StorageEngine = "device" };

        a.IsCompatibleWith( b, out var reason ).Should().BeFalse();
        reason.Should().Contain( "storage_engine" );
    }

    [TestMethod]
    public void IsCompatibleWith_DifferentMemorySize_Incompatible()
    {
        var a = BaselineSignature();
        var b = a with { MemorySize = 2147483648L };

        a.IsCompatibleWith( b, out var reason ).Should().BeFalse();
        reason.Should().Contain( "memory_size" );
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
    public void IsCompatibleWith_DifferentEdition_Incompatible()
    {
        // Community vs Enterprise is a hard incompatibility: replaying an
        // Enterprise-source squash against a Community target silently
        // passes structural compare but fails at runtime on any
        // Enterprise-only feature path (SC namespaces, XDR, etc.).
        var a = BaselineSignature();
        var b = a with { Edition = "Enterprise" };

        a.IsCompatibleWith( b, out var reason ).Should().BeFalse();
        reason.Should().Contain( "edition" );
        reason.Should().Contain( "Community" );
        reason.Should().Contain( "Enterprise" );
    }

    [TestMethod]
    public void IsCompatibleWith_EditionCaseInsensitive()
    {
        // Normalization happens at capture time, but the compare uses
        // OrdinalIgnoreCase as defense-in-depth for hand-constructed signatures.
        var a = BaselineSignature() with { Edition = "community" };
        var b = BaselineSignature() with { Edition = "COMMUNITY" };

        a.IsCompatibleWith( b, out var reason ).Should().BeTrue();
        reason.Should().BeNullOrEmpty();
    }

    [TestMethod]
    public void IsCompatibleWith_CrossProvider_Incompatible()
    {
        var aerospike = BaselineSignature();
        var postgres = new PostgresTopologySignature
        {
            ServerMajor = 16,
            ServerMinor = 13,
            ServerEncoding = "UTF8",
            CollationProvider = "C",
            LocaleProvider = "libc"
        };

        aerospike.IsCompatibleWith( postgres, out var reason ).Should().BeFalse();
        reason.Should().Contain( "postgres" ).And.Contain( "aerospike" );
    }

    [TestMethod]
    public void Properties_ExposesAllAxesAsStrings()
    {
        var sig = BaselineSignature();

        sig.Properties.Should().ContainKey( "server_major" ).WhoseValue.Should().Be( "6" );
        sig.Properties.Should().ContainKey( "namespace" ).WhoseValue.Should().Be( "test" );
        sig.Properties.Should().ContainKey( "replication_factor" ).WhoseValue.Should().Be( "2" );
        sig.Properties.Should().ContainKey( "memory_size" ).WhoseValue.Should().Be( "1073741824" );
        sig.Properties.Should().ContainKey( "storage_engine" ).WhoseValue.Should().Be( "memory" );
        sig.Properties.Should().ContainKey( "cluster_name" ).WhoseValue.Should().Be( "null" );
        sig.Properties.Should().ContainKey( "edition" ).WhoseValue.Should().Be( "Community" );
    }

    [TestMethod]
    public void NormalizeEdition_RecognizesCommunityAndEnterprise()
    {
        AerospikeTopologySignature.NormalizeEdition( "Aerospike Community Edition" ).Should().Be( "Community" );
        AerospikeTopologySignature.NormalizeEdition( "Aerospike Enterprise Edition" ).Should().Be( "Enterprise" );
        // Case variants
        AerospikeTopologySignature.NormalizeEdition( "AEROSPIKE COMMUNITY EDITION" ).Should().Be( "Community" );
        AerospikeTopologySignature.NormalizeEdition( "aerospike enterprise edition" ).Should().Be( "Enterprise" );
    }

    [TestMethod]
    public void NormalizeEdition_EmptyOrWhitespace_ReturnsEmpty()
    {
        AerospikeTopologySignature.NormalizeEdition( "" ).Should().Be( "" );
        AerospikeTopologySignature.NormalizeEdition( "   " ).Should().Be( "" );
        AerospikeTopologySignature.NormalizeEdition( null ).Should().Be( "" );
    }

    [TestMethod]
    public void NormalizeEdition_UnknownPhrasing_PreservedTrimmed()
    {
        // Future-proof against server-version phrasing changes: unknown
        // edition strings round-trip verbatim (trimmed) so strict-equality
        // compare still works when source and target agree.
        AerospikeTopologySignature.NormalizeEdition( "  Aerospike Cloud Edition  " ).Should().Be( "Aerospike Cloud Edition" );
    }

    [TestMethod]
    public void SchemaVersion_AndProviderId_AreStable()
    {
        var sig = BaselineSignature();
        sig.SchemaVersion.Should().Be( 1 );
        sig.ProviderId.Should().Be( "aerospike" );
        AerospikeTopologySignature.ProviderIdValue.Should().Be( "aerospike" );
    }

    [TestMethod]
    public void ParseBuildVersion_DottedVersionExtractsMajorAndMinor()
    {
        AerospikeTopologySignature.ParseBuildVersion( "6.4.0.1" ).Should().Be( (6, 4) );
        AerospikeTopologySignature.ParseBuildVersion( "7.1.0.2" ).Should().Be( (7, 1) );
        AerospikeTopologySignature.ParseBuildVersion( "5.7" ).Should().Be( (5, 7) );
    }

    [TestMethod]
    public void ParseBuildVersion_MalformedThrows()
    {
        Action act = () => AerospikeTopologySignature.ParseBuildVersion( "garbage" );
        act.Should().Throw<MigrationException>().WithMessage( "*not a recognized version*" );

        Action act2 = () => AerospikeTopologySignature.ParseBuildVersion( "6" );
        act2.Should().Throw<MigrationException>();
    }

    [TestMethod]
    public void ParseInfoMap_SemicolonDelimitedKeyEqualsValue()
    {
        const string response =
            "replication-factor=2;default-ttl=2592000;nsup-period=120;" +
            "memory-size=1073741824;storage-engine=memory";

        var map = AerospikeTopologySignature.ParseInfoMap( response );

        map["replication-factor"].Should().Be( "2" );
        map["default-ttl"].Should().Be( "2592000" );
        map["nsup-period"].Should().Be( "120" );
        map["memory-size"].Should().Be( "1073741824" );
        map["storage-engine"].Should().Be( "memory" );
    }

    [TestMethod]
    public void ParseInfoMap_SkipsMalformedEntries()
    {
        // Trailing separator + a value-less segment must not break parsing.
        const string response = "a=1;;b=2;orphan;c=3;";

        var map = AerospikeTopologySignature.ParseInfoMap( response );

        map.Should().HaveCount( 3 );
        map["a"].Should().Be( "1" );
        map["b"].Should().Be( "2" );
        map["c"].Should().Be( "3" );
    }

    [TestMethod]
    public void ParseInfoMap_PreservesEqualsInValue()
    {
        // Some Aerospike info values contain '=' inside the value
        // (e.g., embedded config expressions). Only split on the first '='.
        const string response = "key=value=with=equals;other=plain";

        var map = AerospikeTopologySignature.ParseInfoMap( response );

        map["key"].Should().Be( "value=with=equals" );
        map["other"].Should().Be( "plain" );
    }
}
