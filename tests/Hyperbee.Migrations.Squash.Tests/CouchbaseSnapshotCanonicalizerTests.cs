using FluentAssertions;
using Hyperbee.Migrations.Providers.Couchbase.Squash;
using Hyperbee.Migrations.Squash;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 4 Task 4.4: CouchbaseSnapshotCanonicalizer unit coverage.
//
// Mirrors OpenSearch + MongoDB canonicalizer tests: section-headered JSON
// form, recursive key sort, ephemeral strip at every nesting level, opaque
// content preservation, cross-platform LF normalization, idempotence. Adds
// Couchbase-specific coverage for the R-P3 OQ resolution -- deferred-build
// index state preservation.

[TestClass]
public class CouchbaseSnapshotCanonicalizerTests
{
    private static readonly CouchbaseSnapshotCanonicalizer Canonicalizer = new();

    [TestMethod]
    public void ProviderId_IsCouchbase()
    {
        Canonicalizer.ProviderId.Should().Be( "couchbase" );
    }

    // ---- section parsing + emission ---------------------------------------

    [TestMethod]
    public void Canonicalize_BasicSnapshot_EmitsSectionHeaderedCanonicalForm()
    {
        const string snapshot = """
            # couchbase-snapshot v1
            # bucket: myapp

            [buckets]
            {"myapp": {"bucketType": "membase", "ramQuotaMB": 256}}

            [indexes]
            {"items": [{"name": "idx_email", "keyspace_id": "myapp"}]}
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        canon.Should().Contain( "# couchbase-squash v1" );
        canon.Should().Contain( "[buckets]" );
        canon.Should().Contain( "[indexes]" );
        canon.Should().Contain( "myapp" );
        canon.Should().Contain( "idx_email" );
    }

    [TestMethod]
    public void Canonicalize_SectionsEmittedInAlphabeticalOrder()
    {
        const string snapshot = """
            [indexes]
            {"items": []}

            [buckets]
            {"myapp": {}}
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        var bktIdx = canon.IndexOf( "[buckets]", StringComparison.Ordinal );
        var idxIdx = canon.IndexOf( "[indexes]", StringComparison.Ordinal );

        bktIdx.Should().BeGreaterThan( -1 );
        idxIdx.Should().BeGreaterThan( -1 );
        bktIdx.Should().BeLessThan( idxIdx );
    }

    [TestMethod]
    public void Canonicalize_EmptySnapshot_EmitsHeaderOnly()
    {
        var canon = Canonicalizer.Canonicalize( "" );

        canon.Should().StartWith( "# couchbase-squash v1" );
        canon.Should().NotContain( "[" );
    }

    [TestMethod]
    public void Canonicalize_LeadingCommentsBeforeSections_Ignored()
    {
        // Comments are only stripped BEFORE the first section header (same
        // behavior as the MongoDB canonicalizer); once a section body is
        // open, all lines belong to that body. This is intentional -- it
        // lets capture strategies prepend a snapshot header without
        // requiring a JSON-friendly comment representation inside section
        // bodies.
        const string snapshot = """
            # snapshot header
            # captured: 2026-05-11
            [buckets]
            {"myapp": {"bucketType": "membase"}}
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        canon.Should().Contain( "[buckets]" );
        canon.Should().NotContain( "snapshot header" );
        canon.Should().NotContain( "captured:" );
    }

    [TestMethod]
    public void Canonicalize_CaseInsensitiveSectionHeaders()
    {
        const string snapshot = """
            [BUCKETS]
            {"myapp": {}}
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        canon.Should().Contain( "[buckets]" );
    }

    // ---- JSON key sorting --------------------------------------------------

    [TestMethod]
    public void Canonicalize_SortsTopLevelKeysOrdinally()
    {
        const string snapshot = """
            [buckets]
            {"zebra": {}, "alpha": {}}
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        canon.IndexOf( "alpha", StringComparison.Ordinal )
            .Should().BeLessThan( canon.IndexOf( "zebra", StringComparison.Ordinal ) );
    }

    [TestMethod]
    public void Canonicalize_SortsNestedKeysRecursively()
    {
        const string snapshot = """
            [buckets]
            {"myapp": {"replicaNumber": 2, "bucketType": "membase", "ramQuotaMB": 256}}
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        var bt = canon.IndexOf( "bucketType", StringComparison.Ordinal );
        var ram = canon.IndexOf( "ramQuotaMB", StringComparison.Ordinal );
        var rep = canon.IndexOf( "replicaNumber", StringComparison.Ordinal );
        bt.Should().BeLessThan( ram );
        ram.Should().BeLessThan( rep );
    }

    [TestMethod]
    public void Canonicalize_ArraysPreserveOrder()
    {
        // GSI index key arrays are order-sensitive (compound index).
        const string snapshot = """
            [indexes]
            {"items": [{"name": "idx_compound", "index_key": ["email", "tenantId", "createdAt"]}]}
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        var eIdx = canon.IndexOf( "email", StringComparison.Ordinal );
        var tIdx = canon.IndexOf( "tenantId", StringComparison.Ordinal );
        var cIdx = canon.IndexOf( "createdAt", StringComparison.Ordinal );
        eIdx.Should().BeLessThan( tIdx );
        tIdx.Should().BeLessThan( cIdx );
    }

    // ---- ephemeral stripping -----------------------------------------------

    [TestMethod]
    public void Canonicalize_StripsBucketRuntimeStats()
    {
        const string snapshot = """
            [buckets]
            {"myapp": {"bucketType": "membase", "docCount": 12345, "dataSize": 9999999, "memUsed": 555, "diskUsed": 1024, "opsPerSec": 0.5, "quotaUsed": 0.1, "quotaPercentUsed": 10, "basicStats": {"itemCount": 12345}}}
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        canon.Should().NotContain( "docCount" );
        canon.Should().NotContain( "dataSize" );
        canon.Should().NotContain( "memUsed" );
        canon.Should().NotContain( "diskUsed" );
        canon.Should().NotContain( "opsPerSec" );
        canon.Should().NotContain( "quotaUsed" );
        canon.Should().NotContain( "quotaPercentUsed" );
        canon.Should().NotContain( "basicStats" );
        canon.Should().Contain( "bucketType" );
    }

    [TestMethod]
    public void Canonicalize_StripsIndexId()
    {
        const string snapshot = """
            [indexes]
            {"items": [{"id": "abc-123-def", "name": "idx_email", "keyspace_id": "myapp"}]}
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        canon.Should().NotContain( "abc-123-def" );
        canon.Should().NotContain( "\"id\"" );
        canon.Should().Contain( "idx_email" );
    }

    [TestMethod]
    public void Canonicalize_StripsLastRebalanceTimestamp_BothCasings()
    {
        const string snapshot = """
            [buckets]
            {"myapp": {"bucketType": "membase", "lastRebalanceTimestamp": "2026-01-01T00:00:00Z", "last_rebalance_timestamp": 1234567890}}
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        canon.Should().NotContain( "lastRebalanceTimestamp" );
        canon.Should().NotContain( "last_rebalance_timestamp" );
    }

    [TestMethod]
    public void Canonicalize_StripsNodesPlacement()
    {
        const string snapshot = """
            [buckets]
            {"myapp": {"bucketType": "membase", "nodes": [{"hostname": "node1"}, {"hostname": "node2"}], "replicaNumber": 1}}
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        canon.Should().NotContain( "node1" );
        canon.Should().NotContain( "node2" );
        canon.Should().NotContain( "nodes" );
        canon.Should().Contain( "replicaNumber" );
    }

    [TestMethod]
    public void Canonicalize_StripsVBucketServerMap()
    {
        const string snapshot = """
            [buckets]
            {"myapp": {"bucketType": "membase", "vBucketServerMap": {"serverList": ["a"], "vBucketMap": [[0]]}}}
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        canon.Should().NotContain( "vBucketServerMap" );
        canon.Should().NotContain( "vBucketMap" );
        canon.Should().NotContain( "serverList" );
    }

    [TestMethod]
    public void Canonicalize_StripsEphemeralsAtNestedLevels()
    {
        const string snapshot = """
            [buckets]
            {"myapp": {"bucketType": "membase", "controllers": {"compactAll": "/x", "docCount": 0}, "settings": {"docCount": 9}}}
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        canon.Should().NotContain( "docCount" );
        canon.Should().Contain( "compactAll" );
    }

    // ---- deferred-build R-P3 OQ resolution --------------------------------

    [TestMethod]
    public void Canonicalize_IndexStateOnline_Dropped()
    {
        const string snapshot = """
            [indexes]
            {"items": [{"name": "idx_email", "keyspace_id": "myapp", "state": "online"}]}
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        canon.Should().NotContain( "\"state\"" );
        canon.Should().NotContain( "online" );
        canon.Should().Contain( "idx_email" );
    }

    [TestMethod]
    public void Canonicalize_IndexStateDeferred_Preserved()
    {
        // R-P3 OQ resolution: deferred state IS structural intent. Apply
        // path needs to know to issue BUILD INDEX. Preserved.
        const string snapshot = """
            [indexes]
            {"items": [{"name": "idx_email", "keyspace_id": "myapp", "state": "deferred"}]}
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        canon.Should().Contain( "\"state\"" );
        canon.Should().Contain( "deferred" );
    }

    [TestMethod]
    public void Canonicalize_IndexStateBuilding_Throws()
    {
        // Transient state: capture strategy must wait. The canonicalizer
        // rejects loudly so the bug surfaces at squash-time rather than
        // producing a non-deterministic snapshot.
        const string snapshot = """
            [indexes]
            {"items": [{"name": "idx_email", "keyspace_id": "myapp", "state": "building"}]}
            """;

        Action act = () => Canonicalizer.Canonicalize( snapshot );
        act.Should().Throw<MigrationException>().WithMessage( "*transient*building*" );
    }

    [TestMethod]
    public void Canonicalize_IndexStatePending_Throws()
    {
        const string snapshot = """
            [indexes]
            {"items": [{"name": "idx_email", "keyspace_id": "myapp", "state": "pending"}]}
            """;

        Action act = () => Canonicalizer.Canonicalize( snapshot );
        act.Should().Throw<MigrationException>().WithMessage( "*transient*pending*" );
    }

    [TestMethod]
    public void Canonicalize_BucketsSectionStateNotProcessed()
    {
        // The deferred-state rule applies only to the `[indexes]` section.
        // A field named `state` in `[buckets]` is just a regular field and
        // passes through unmodified.
        const string snapshot = """
            [buckets]
            {"myapp": {"bucketType": "membase", "state": "healthy"}}
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        canon.Should().Contain( "healthy" );
        canon.Should().Contain( "\"state\"" );
    }

    // ---- opaque content preservation --------------------------------------

    [TestMethod]
    public void Canonicalize_EventingFunctionSource_PreservedOpaque()
    {
        // Eventing function .js source is opaque content. The canonicalizer
        // never parses JS; passes the string content through unchanged.
        const string snapshot = """
            [eventing_functions]
            {"funcs": [{"appname": "auditLog", "appcode": "function OnUpdate(doc, meta) { /* preserve */ log(meta.id); }"}]}
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        canon.Should().Contain( "OnUpdate" );
        canon.Should().Contain( "function" );
        canon.Should().Contain( "/* preserve */" );
    }

    [TestMethod]
    public void Canonicalize_N1qlWhereClause_PreservedOpaque()
    {
        const string snapshot = """
            [indexes]
            {"items": [{"name": "idx_active_users", "keyspace_id": "myapp", "condition": "((`type` = \"user\") AND (`status` = \"active\"))"}]}
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        canon.Should().Contain( "active" );
        canon.Should().Contain( "user" );
        canon.Should().Contain( "AND" );
    }

    // ---- numeric preservation, idempotence, line endings, errors ----------

    [TestMethod]
    public void Canonicalize_NumberRepresentationPreserved()
    {
        // GetRawText preservation: 1.0 stays 1.0, 1 stays 1.
        const string snapshot = """
            [buckets]
            {"myapp": {"ramQuotaMB": 256, "compressionRatio": 1.0}}
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        canon.Should().Contain( "256" );
        canon.Should().Contain( "1.0" );
    }

    [TestMethod]
    public void Canonicalize_Idempotent_TwiceProducesSameOutput()
    {
        const string snapshot = """
            [buckets]
            {"myapp": {"bucketType": "membase", "ramQuotaMB": 256, "docCount": 0}}

            [indexes]
            {"items": [{"name": "idx", "keyspace_id": "myapp", "id": "ephemeral", "state": "online"}]}
            """;

        var first = Canonicalizer.Canonicalize( snapshot );
        var second = Canonicalizer.Canonicalize( first );

        second.Should().Be( first );
    }

    [TestMethod]
    public void Canonicalize_DivergentKeyOrders_ProduceSameOutput()
    {
        var a = """
            [buckets]
            {"alpha": {}, "zebra": {}}
            """;
        var b = """
            [buckets]
            {"zebra": {}, "alpha": {}}
            """;

        Canonicalizer.Canonicalize( a ).Should().Be( Canonicalizer.Canonicalize( b ) );
    }

    [TestMethod]
    public void Canonicalize_DivergentEphemerals_ProduceSameOutput()
    {
        var a = """
            [buckets]
            {"myapp": {"bucketType": "membase", "docCount": 1}}
            """;
        var b = """
            [buckets]
            {"myapp": {"bucketType": "membase", "docCount": 999999}}
            """;

        Canonicalizer.Canonicalize( a ).Should().Be( Canonicalizer.Canonicalize( b ) );
    }

    [TestMethod]
    public void Canonicalize_LineEndingsNormalizedToLF()
    {
        var snapshot = "[buckets]\r\n{\"myapp\": {}}\r\n";

        var canon = Canonicalizer.Canonicalize( snapshot );

        canon.Should().NotContain( "\r" );
    }

    [TestMethod]
    public void Canonicalize_InvalidJsonSection_Throws()
    {
        const string snapshot = """
            [buckets]
            {invalid json
            """;

        Action act = () => Canonicalizer.Canonicalize( snapshot );
        act.Should().Throw<MigrationException>().WithMessage( "*buckets*" );
    }

    [TestMethod]
    public void Canonicalize_UnknownSection_PassesThroughCanonical()
    {
        const string snapshot = """
            [analytics]
            {"datasets": {"sales": {"linkName": "main"}}}
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        canon.Should().Contain( "[analytics]" );
        canon.Should().Contain( "datasets" );
        canon.Should().Contain( "sales" );
    }

    [TestMethod]
    public void Canonicalize_Null_Throws()
    {
        Action act = () => Canonicalizer.Canonicalize( null );
        act.Should().Throw<ArgumentNullException>();
    }

    [TestMethod]
    public void EmitScript_Identity_SameAsCanonicalize()
    {
        const string snapshot = """
            [buckets]
            {"myapp": {"bucketType": "membase"}}
            """;

        Canonicalizer.EmitScript( Canonicalizer.Canonicalize( snapshot ) )
            .Should().Be( Canonicalizer.Canonicalize( snapshot ) );
    }
}
