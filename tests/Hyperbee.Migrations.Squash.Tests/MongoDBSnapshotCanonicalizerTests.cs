using FluentAssertions;
using Hyperbee.Migrations.Providers.MongoDB.Squash;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 3 Task 3.4: MongoDBSnapshotCanonicalizer unit coverage.
//
// Same shape as OpenSearch Task 2.4: section-headered JSON form, recursive
// key sort, ephemeral strip at every nesting level, opaque content
// preservation, cross-platform LF normalization, idempotence.

[TestClass]
public class MongoDBSnapshotCanonicalizerTests
{
    private static readonly MongoDBSnapshotCanonicalizer Canonicalizer = new();

    [TestMethod]
    public void ProviderId_IsMongoDB()
    {
        Canonicalizer.ProviderId.Should().Be( "mongodb" );
    }

    // ---- section parsing ---------------------------------------------------

    [TestMethod]
    public void Canonicalize_BasicSnapshot_EmitsSectionHeaderedCanonicalForm()
    {
        const string snapshot = """
            # mongodb-snapshot v1
            # database: appdb

            [collections]
            {"users": {"type": "collection", "options": {}}, "orders": {"type": "collection"}}

            [indexes]
            {"users": [{"name": "_id_", "key": {"_id": 1}}], "orders": [{"name": "_id_", "key": {"_id": 1}}]}
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        canon.Should().Contain( "# mongodb-squash v1" );
        canon.Should().Contain( "[collections]" );
        canon.Should().Contain( "[indexes]" );
        canon.Should().Contain( "users" );
        canon.Should().Contain( "orders" );
    }

    [TestMethod]
    public void Canonicalize_SectionsEmittedInAlphabeticalOrder()
    {
        const string snapshot = """
            [indexes]
            {"users": []}

            [collections]
            {"users": {}}
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        var collIdx = canon.IndexOf( "[collections]", StringComparison.Ordinal );
        var idxIdx = canon.IndexOf( "[indexes]", StringComparison.Ordinal );

        collIdx.Should().BeGreaterThan( -1 );
        idxIdx.Should().BeGreaterThan( -1 );
        collIdx.Should().BeLessThan( idxIdx );
    }

    [TestMethod]
    public void Canonicalize_EmptySnapshot_EmitsHeaderOnly()
    {
        var canon = Canonicalizer.Canonicalize( "" );

        canon.Should().StartWith( "# mongodb-squash v1" );
        canon.Should().NotContain( "[" );
    }

    // ---- JSON key sorting --------------------------------------------------

    [TestMethod]
    public void Canonicalize_SortsTopLevelKeysOrdinal()
    {
        const string snapshot = """
            [collections]
            {"zebras": {}, "apples": {}, "mangos": {}}
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        var apples = canon.IndexOf( "apples", StringComparison.Ordinal );
        var mangos = canon.IndexOf( "mangos", StringComparison.Ordinal );
        var zebras = canon.IndexOf( "zebras", StringComparison.Ordinal );

        apples.Should().BeLessThan( mangos );
        mangos.Should().BeLessThan( zebras );
    }

    [TestMethod]
    public void Canonicalize_SortsNestedKeysOrdinal()
    {
        const string snapshot = """
            [collections]
            {"users": {"zebra_field": 1, "apple_field": 2, "mango_field": {"nested_z": "z", "nested_a": "a"}}}
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        var apple = canon.IndexOf( "apple_field", StringComparison.Ordinal );
        var mango = canon.IndexOf( "mango_field", StringComparison.Ordinal );
        var zebra = canon.IndexOf( "zebra_field", StringComparison.Ordinal );
        var nestedA = canon.IndexOf( "nested_a", StringComparison.Ordinal );
        var nestedZ = canon.IndexOf( "nested_z", StringComparison.Ordinal );

        apple.Should().BeLessThan( mango );
        mango.Should().BeLessThan( zebra );
        nestedA.Should().BeLessThan( nestedZ );
    }

    [TestMethod]
    public void Canonicalize_PreservesArrayOrder()
    {
        // Aggregation pipeline stages depend on declared order; sorting
        // would break view definitions.
        const string snapshot = """
            [views]
            {"active_users": {"pipeline": [{"$match": {"active": true}}, {"$project": {"name": 1}}, {"$sort": {"name": 1}}]}}
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        var match = canon.IndexOf( "$match", StringComparison.Ordinal );
        var project = canon.IndexOf( "$project", StringComparison.Ordinal );
        var sort = canon.IndexOf( "$sort", StringComparison.Ordinal );

        match.Should().BeLessThan( project );
        project.Should().BeLessThan( sort );
    }

    // ---- ephemeral stripping -----------------------------------------------

    [TestMethod]
    public void Canonicalize_StripsUuid()
    {
        // info.uuid is server-generated; varies per recreation.
        const string snapshot = """
            [collections]
            {"users": {"type": "collection", "info": {"uuid": "abc-123", "readOnly": false}, "options": {}}}
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        canon.Should().NotContain( "uuid" );
        canon.Should().NotContain( "abc-123" );
    }

    [TestMethod]
    public void Canonicalize_StripsReadOnly()
    {
        const string snapshot = """
            [collections]
            {"users": {"info": {"readOnly": false}}}
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        canon.Should().NotContain( "readOnly" );
    }

    [TestMethod]
    public void Canonicalize_StripsIndexVersionField()
    {
        // `v` field on each index is server-managed (1 or 2) and varies
        // by MongoDB version. The plan's load-bearing strip case: index
        // v field at every nesting level (per-index `v` AND idIndex.v).
        const string snapshot = """
            [indexes]
            {
              "users": [
                { "v": 2, "key": {"_id": 1}, "name": "_id_" },
                { "v": 2, "key": {"email": 1}, "name": "idx_email", "unique": true }
              ]
            }
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        canon.Should().NotContain( "\"v\":" );
        canon.Should().Contain( "idx_email" );
        canon.Should().Contain( "unique" );
    }

    [TestMethod]
    public void Canonicalize_StripsIdIndexV()
    {
        // listCollections returns each collection's idIndex with a v field.
        // The same `v` strip at every level catches this without a path-
        // specific rule.
        const string snapshot = """
            [collections]
            {
              "users": {
                "type": "collection",
                "idIndex": { "v": 2, "key": {"_id": 1}, "name": "_id_", "ns": "appdb.users" }
              }
            }
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        canon.Should().NotContain( "\"v\":" );
        canon.Should().NotContain( "\"ns\":" );
        canon.Should().Contain( "_id_" );
    }

    [TestMethod]
    public void Canonicalize_StripsLegacyNsField()
    {
        // Legacy MongoDB injected `ns` (collection namespace as a string)
        // into responses. Deprecated in 4.4+ but still emitted by some
        // hosted offerings. Strip it.
        const string snapshot = """
            [collections]
            {"users": {"ns": "appdb.users", "type": "collection"}}
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        canon.Should().NotContain( "appdb.users" );
        canon.Should().Contain( "type" );
    }

    [TestMethod]
    public void Canonicalize_StripsEphemeralsAtEveryNestingLevel()
    {
        // Deep nesting: validator -> properties -> field -> uuid. All `uuid`
        // fields at any depth strip uniformly.
        const string snapshot = """
            [collections]
            {
              "users": {
                "info": {"uuid": "outer-uuid"},
                "options": {
                  "validator": {
                    "_meta": {"uuid": "inner-uuid"},
                    "$jsonSchema": {"required": ["email"]}
                  }
                }
              }
            }
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        canon.Should().NotContain( "outer-uuid" );
        canon.Should().NotContain( "inner-uuid" );
        canon.Should().Contain( "$jsonSchema" );
        canon.Should().Contain( "email" );
    }

    // ---- opaque BSON-content preservation ----------------------------------

    [TestMethod]
    public void Canonicalize_AggregationPipeline_PreservedVerbatim()
    {
        // BSON content inside a view's pipeline is opaque JSON content.
        // The canonicalizer preserves stage operators ($match, $project,
        // $sort), embedded operators ($eq, $gt), and complex nesting
        // byte-for-byte (modulo key sorting at each object level).
        const string snapshot = """
            [views]
            {"recent_users": {"pipeline": [{"$match": {"createdAt": {"$gt": "ISODate('2024-01-01')"}}}, {"$project": {"_id": 1, "email": 1}}]}}
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        canon.Should().Contain( "$match" );
        canon.Should().Contain( "$gt" );
        canon.Should().Contain( "ISODate('2024-01-01')" );
        canon.Should().Contain( "$project" );
    }

    [TestMethod]
    public void Canonicalize_PartialFilterExpression_PreservedVerbatim()
    {
        // partialFilterExpression on an index is opaque BSON query content.
        const string snapshot = """
            [indexes]
            {"users": [{"key": {"email": 1}, "name": "idx_email_active", "partialFilterExpression": {"active": true, "tenant_id": {"$exists": true}}}]}
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        canon.Should().Contain( "partialFilterExpression" );
        canon.Should().Contain( "\"active\": true" );
        canon.Should().Contain( "$exists" );
    }

    // ---- idempotence (C12) -------------------------------------------------

    [TestMethod]
    public void Canonicalize_IsIdempotent()
    {
        const string snapshot = """
            [collections]
            {"users": {"info": {"uuid": "abc-123"}, "type": "collection"}}

            [indexes]
            {"users": [{"v": 2, "key": {"_id": 1}, "name": "_id_"}]}
            """;

        var first = Canonicalizer.Canonicalize( snapshot );
        var second = Canonicalizer.Canonicalize( first );

        second.Should().Be( first );
    }

    [TestMethod]
    public void Canonicalize_DifferentKeyOrders_ProduceSameOutput()
    {
        const string snapshotA = """
            [collections]
            {"users": {"options": {}, "type": "collection"}}
            """;

        const string snapshotB = """
            [collections]
            {"users": {"type": "collection", "options": {}}}
            """;

        Canonicalizer.Canonicalize( snapshotA ).Should().Be( Canonicalizer.Canonicalize( snapshotB ) );
    }

    [TestMethod]
    public void Canonicalize_DifferentEphemeralValues_ProduceSameOutput()
    {
        // Two captures of the same logical state differing only in
        // server-generated UUIDs must canonicalize identically.
        const string snapshotA = """
            [collections]
            {"users": {"info": {"uuid": "uuid-aaa"}, "type": "collection"}}
            """;

        const string snapshotB = """
            [collections]
            {"users": {"info": {"uuid": "uuid-bbb"}, "type": "collection"}}
            """;

        Canonicalizer.Canonicalize( snapshotA ).Should().Be( Canonicalizer.Canonicalize( snapshotB ) );
    }

    [TestMethod]
    public void Canonicalize_DifferentIndexV_ProduceSameOutput()
    {
        // Cross-MongoDB-version stability: same logical index with
        // different v values (1 vs 2) canonicalizes identically.
        const string snapshotV1 = """
            [indexes]
            {"users": [{"v": 1, "key": {"email": 1}, "name": "idx_email"}]}
            """;

        const string snapshotV2 = """
            [indexes]
            {"users": [{"v": 2, "key": {"email": 1}, "name": "idx_email"}]}
            """;

        Canonicalizer.Canonicalize( snapshotV1 ).Should().Be( Canonicalizer.Canonicalize( snapshotV2 ) );
    }

    // ---- normalization edge cases ------------------------------------------

    [TestMethod]
    public void Canonicalize_NormalizesLineEndings()
    {
        var crlfSnapshot = "[collections]\r\n{\"users\":{}}\r\n";

        var canon = Canonicalizer.Canonicalize( crlfSnapshot );

        canon.Should().NotContain( "\r" );
        canon.Should().Contain( "users" );
    }

    [TestMethod]
    public void Canonicalize_PreservesNumberRepresentation()
    {
        // BSON numeric encoding: integers can appear as plain numbers,
        // strings (for $numberLong), or floats. Preserve operator
        // representation.
        const string snapshot = """
            [collections]
            {"users": {"a": "1", "b": 1, "c": 1.0, "d": 1e3}}
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        canon.Should().Contain( "\"a\": \"1\"" );
        canon.Should().Contain( "\"b\": 1" );
        canon.Should().Contain( "\"c\": 1.0" );
        canon.Should().Contain( "\"d\": 1e3" );
    }

    [TestMethod]
    public void Canonicalize_CaseInsensitiveSectionHeaders()
    {
        const string snapshot = """
            [COLLECTIONS]
            {"users": {}}

            [Indexes]
            {"users": []}
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        canon.Should().Contain( "[collections]" );
        canon.Should().Contain( "[indexes]" );
        canon.Should().NotContain( "[COLLECTIONS]" );
        canon.Should().NotContain( "[Indexes]" );
    }

    // ---- error handling ----------------------------------------------------

    [TestMethod]
    public void Canonicalize_InvalidJsonSection_ThrowsMigrationException()
    {
        const string snapshot = """
            [collections]
            { this is not valid json
            """;

        Action act = () => Canonicalizer.Canonicalize( snapshot );
        act.Should().Throw<MigrationException>()
            .WithMessage( "*not valid JSON*" )
            .Which.Message.Should().Contain( "[collections]" );
    }

    [TestMethod]
    public void Canonicalize_NullInput_Throws()
    {
        Action act = () => Canonicalizer.Canonicalize( null );
        act.Should().Throw<ArgumentNullException>();
    }

    [TestMethod]
    public void EmitScript_IsCanonicalize()
    {
        const string snapshot = """
            [collections]
            {"users": {"type": "collection"}}
            """;

        Canonicalizer.EmitScript( snapshot ).Should().Be( Canonicalizer.Canonicalize( snapshot ) );
    }

    [TestMethod]
    public void EphemeralCatalog_IncludesPlannedFields()
    {
        // The ephemeral catalog must include the load-bearing MongoDB
        // fields documented in the plan (Task 3.4).
        MongoDBSnapshotCanonicalizer.Ephemerals.Should().Contain( "uuid" );
        MongoDBSnapshotCanonicalizer.Ephemerals.Should().Contain( "readOnly" );
        MongoDBSnapshotCanonicalizer.Ephemerals.Should().Contain( "v" );
        MongoDBSnapshotCanonicalizer.Ephemerals.Should().Contain( "ns" );
    }
}
