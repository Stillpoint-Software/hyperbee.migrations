//#define INTEGRATIONS
using Hyperbee.Migrations;
using Hyperbee.Migrations.Integration.Tests.Container.MongoDb;
using Hyperbee.Migrations.Providers.MongoDB.Squash;
using Hyperbee.Migrations.Squash;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Hyperbee.Migrations.Integration.Tests;

#if INTEGRATIONS

// Phase 3 (R-P5): MongoDB squash codegen determinism gate (C12).
//
// Contract: given identical cluster state, two GenerateAsync calls must
// produce byte-equal SquashGenerationResult.Generated.Content. Guarantees
// the canonicalizer fully removes ephemeral fields (uuid, readOnly, v at
// every nesting level, legacy ns) and JSON key orderings are sorted away.

[TestClass]
[DoNotParallelize]
// LocalOnly: heavy container-based integration test; excluded from the gating CI matrix (does not gate the NuGet publish). Runs locally / on demand.
[TestCategory( "LocalOnly" )]
public class MongoDBSquashDeterminismTests
{
    private IMongoClient _client;
    private const string TestDatabase = "detgate_db";
    private const string TestCollectionPrefix = "detgate_";

    [TestInitialize]
    public void Setup()
    {
        _client = MongoDbTestContainer.Client;
        Assert.IsNotNull( _client, "MongoDbTestContainer must initialize before this test class runs." );
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        try { await _client.DropDatabaseAsync( TestDatabase ); } catch { }
    }

    [TestMethod]
    public async Task EmptyDatabase_TwoRuns_ProduceByteEqualContent()
    {
        var content1 = await GenerateOnceAsync();
        var content2 = await GenerateOnceAsync();

        Assert.AreEqual( content1, content2,
            "Two GenerateAsync runs against an unchanged database must produce byte-equal output." );
    }

    [TestMethod]
    public async Task PopulatedDatabase_TwoRuns_ProduceByteEqualContent()
    {
        var db = _client.GetDatabase( TestDatabase );

        // Create collections with indexes.
        await db.CreateCollectionAsync( $"{TestCollectionPrefix}users" );
        await db.CreateCollectionAsync( $"{TestCollectionPrefix}orders" );

        var users = db.GetCollection<BsonDocument>( $"{TestCollectionPrefix}users" );
        await users.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending( "email" ),
                new CreateIndexOptions { Name = "idx_email", Unique = true } ) );

        var content1 = await GenerateOnceAsync();
        var content2 = await GenerateOnceAsync();

        Assert.AreEqual( content1, content2,
            "Two GenerateAsync runs against the same populated state must produce byte-equal output." );

        // Defense-in-depth: ensure the populated content contains the
        // structural objects we created.
        StringAssert.Contains( content1, $"{TestCollectionPrefix}users" );
        StringAssert.Contains( content1, $"{TestCollectionPrefix}orders" );
        StringAssert.Contains( content1, "idx_email" );
    }

    [TestMethod]
    public async Task CollectionCreationOrder_DoesNotAffectCanonicalOutput()
    {
        var db = _client.GetDatabase( TestDatabase );

        await db.CreateCollectionAsync( $"{TestCollectionPrefix}zcoll" );
        await db.CreateCollectionAsync( $"{TestCollectionPrefix}acoll" );

        var firstOrder = await GenerateOnceAsync();

        await db.DropCollectionAsync( $"{TestCollectionPrefix}zcoll" );
        await db.DropCollectionAsync( $"{TestCollectionPrefix}acoll" );

        await db.CreateCollectionAsync( $"{TestCollectionPrefix}acoll" );
        await db.CreateCollectionAsync( $"{TestCollectionPrefix}zcoll" );

        var secondOrder = await GenerateOnceAsync();

        Assert.AreEqual( firstOrder, secondOrder,
            "Collection creation order must not affect canonical output bytes." );
    }

    [TestMethod]
    public async Task RecreateCollectionWithDifferentUuid_ProducesByteEqualContent()
    {
        // Server-generated UUIDs differ per recreation; canonicalizer
        // strips uuid at every nesting level. Two captures of "same
        // logical state with different uuids" must canonicalize identically.
        var db = _client.GetDatabase( TestDatabase );

        await db.CreateCollectionAsync( $"{TestCollectionPrefix}users" );
        var first = await GenerateOnceAsync();

        await db.DropCollectionAsync( $"{TestCollectionPrefix}users" );
        await db.CreateCollectionAsync( $"{TestCollectionPrefix}users" );
        var second = await GenerateOnceAsync();

        Assert.AreEqual( first, second,
            "Recreating a collection produces a new UUID; canonicalizer must strip it for byte-stable output." );
    }

    // ---- helpers -----------------------------------------------------------

    private async Task<string> GenerateOnceAsync()
    {
        var ctx = new MongoDBSquashGenerationContext(
            squashName: "Squash_2000",
            squashVersion: 2000,
            client: _client,
            databaseName: TestDatabase,
            captureSnapshotAsync: async ( _, ct ) =>
            {
                var blob = await MongoDBSnapshotCapture.CaptureAsync( _client, TestDatabase, ct );
                return new SnapshotCaptureResult( blob );
            } );

        var descriptors = new[]
        {
            new MigrationDescriptor( typeof( object ), new MigrationAttribute( 1000 ), Array.Empty<long>() ),
            new MigrationDescriptor( typeof( object ), new MigrationAttribute( 2000 ), Array.Empty<long>() ),
        };

        var strategy = new IntrospectionSnapshotStrategy(
            new MongoDBSnapshotCanonicalizer(),
            new MongoDBDataOpClassifier() );

        var result = await strategy.GenerateAsync( ctx, descriptors, new SquashGenerationOptions() );

        if ( result is SquashGenerationResult.Failed failed )
            Assert.Fail( $"GenerateAsync returned Failed: {failed.Detail}" );

        var generated = (SquashGenerationResult.Generated) result;
        return generated.Content;
    }
}

#endif
