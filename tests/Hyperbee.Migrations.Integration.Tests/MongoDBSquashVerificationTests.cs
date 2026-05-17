//#define INTEGRATIONS
using System.Text.Json;
using Hyperbee.Migrations;
using Hyperbee.Migrations.Integration.Tests.Container.MongoDb;
using Hyperbee.Migrations.Providers.MongoDB.Squash;
using Hyperbee.Migrations.Squash;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Hyperbee.Migrations.Integration.Tests;

#if INTEGRATIONS

// Phase 3 (R-P6): MongoDB squash verification round (A4).
//
// Contract: re-applying the historical migration range produces the same
// canonicalized state as applying the GENERATED squash content.
//
// Round-trip:
//   1. Set up collections + indexes in TestDatabase.
//   2. Capture A via MongoDBSnapshotCapture (the "historical" snapshot).
//   3. Run IntrospectionSnapshotStrategy.GenerateAsync -> Generated.Content.
//   4. Wipe the database.
//   5. Apply Generated.Content by walking the canonical JSON sections and
//      recreating each collection + its indexes via the driver.
//   6. Capture B.
//   7. MongoDBSquashVerifier.VerifyAsync -> expect Success.

[TestClass]
[DoNotParallelize]
// LocalOnly: heavy container-based integration test; excluded from the gating CI matrix (does not gate the NuGet publish). Runs locally / on demand.
[TestCategory( "LocalOnly" )]
public class MongoDBSquashVerificationTests
{
    private IMongoClient _client;
    private const string TestDatabase = "verifyround_db";

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
    public async Task EmptyDatabase_RoundTrip_ReturnsSuccess()
    {
        var ctx = MakeContext();
        var generated = await GenerateAsync( ctx );

        var verifier = new MongoDBSquashVerifier( new MongoDBSnapshotCanonicalizer() )
        {
            CaptureFromGeneratedAsync = async ( _, _, ct ) =>
            {
                var blob = await MongoDBSnapshotCapture.CaptureAsync( _client, TestDatabase, ct );
                return new SnapshotCaptureResult( blob );
            }
        };

        var result = await verifier.VerifyAsync( ctx, generated );

        if ( result is VerificationResult.Failed failed )
            Assert.Fail( $"Verification failed: {failed.Detail}\n{failed.DiffSummary}" );

        Assert.IsInstanceOfType<VerificationResult.Success>( result );
    }

    [TestMethod]
    public async Task PopulatedDatabase_RoundTrip_ReturnsSuccess()
    {
        // Set up structural state: two collections, one with a custom
        // unique index.
        var db = _client.GetDatabase( TestDatabase );
        await db.CreateCollectionAsync( "users" );
        await db.CreateCollectionAsync( "orders" );

        var users = db.GetCollection<BsonDocument>( "users" );
        await users.Indexes.CreateOneAsync(
            new CreateIndexModel<BsonDocument>(
                Builders<BsonDocument>.IndexKeys.Ascending( "email" ),
                new CreateIndexOptions { Name = "idx_email", Unique = true } ) );

        var ctx = MakeContext();
        var generated = await GenerateAsync( ctx );

        // Defense-in-depth.
        StringAssert.Contains( generated.Content, "users" );
        StringAssert.Contains( generated.Content, "orders" );
        StringAssert.Contains( generated.Content, "idx_email" );

        var verifier = new MongoDBSquashVerifier( new MongoDBSnapshotCanonicalizer() )
        {
            CaptureFromGeneratedAsync = async ( content, _, ct ) =>
            {
                await _client.DropDatabaseAsync( TestDatabase, ct );
                await ApplyGeneratedAsync( content, ct );
                var blob = await MongoDBSnapshotCapture.CaptureAsync( _client, TestDatabase, ct );
                return new SnapshotCaptureResult( blob );
            }
        };

        var result = await verifier.VerifyAsync( ctx, generated );

        if ( result is VerificationResult.Failed failed )
            Assert.Fail( $"Verification failed: {failed.Detail}\n{failed.DiffSummary}" );

        Assert.IsInstanceOfType<VerificationResult.Success>( result );
    }

    // ---- helpers -----------------------------------------------------------

    private MongoDBSquashGenerationContext MakeContext() => new(
        squashName: "Squash_2000",
        squashVersion: 2000,
        client: _client,
        databaseName: TestDatabase,
        captureSnapshotAsync: async ( _, ct ) =>
        {
            var blob = await MongoDBSnapshotCapture.CaptureAsync( _client, TestDatabase, ct );
            return new SnapshotCaptureResult( blob );
        } );

    private async Task<SquashGenerationResult.Generated> GenerateAsync( MongoDBSquashGenerationContext ctx )
    {
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

        return (SquashGenerationResult.Generated) result;
    }

    // Walk the canonical content's [collections] and [indexes] sections;
    // recreate each collection (with options) and its indexes. Minimum
    // viable apply for R-P6; the Phase 5 CLI ships the full version.
    private async Task ApplyGeneratedAsync( string content, CancellationToken ct )
    {
        var sections = ParseSections( content );
        var db = _client.GetDatabase( TestDatabase );

        if ( sections.TryGetValue( "collections", out var collectionsJson ) )
        {
            using var doc = JsonDocument.Parse( collectionsJson );
            if ( doc.RootElement.ValueKind == JsonValueKind.Object )
            {
                foreach ( var entry in doc.RootElement.EnumerateObject() )
                {
                    var collectionName = entry.Name;
                    // listCollections includes options + idIndex + info.
                    // For minimum viable apply we just create the collection;
                    // capped/validator/time-series options would need
                    // CreateCollectionOptions wiring (Phase 5 CLI).
                    await db.CreateCollectionAsync( collectionName, cancellationToken: ct ).ConfigureAwait( false );
                }
            }
        }

        if ( sections.TryGetValue( "indexes", out var indexesJson ) )
        {
            using var doc = JsonDocument.Parse( indexesJson );
            if ( doc.RootElement.ValueKind == JsonValueKind.Object )
            {
                foreach ( var entry in doc.RootElement.EnumerateObject() )
                {
                    var collectionName = entry.Name;
                    if ( entry.Value.ValueKind != JsonValueKind.Array )
                        continue;

                    var coll = db.GetCollection<BsonDocument>( collectionName );
                    foreach ( var indexEl in entry.Value.EnumerateArray() )
                    {
                        var indexBson = BsonDocument.Parse( indexEl.GetRawText() );
                        if ( !indexBson.TryGetValue( "key", out var keyVal ) || keyVal is not BsonDocument keyDoc )
                            continue;
                        if ( !indexBson.TryGetValue( "name", out var nameVal ) || !nameVal.IsString )
                            continue;

                        // Build IndexKeys from the captured key spec.
                        var keys = new BsonDocumentIndexKeysDefinition<BsonDocument>( keyDoc );
                        var options = new CreateIndexOptions { Name = nameVal.AsString };
                        if ( indexBson.TryGetValue( "unique", out var uniqVal ) && uniqVal.IsBoolean )
                            options.Unique = uniqVal.AsBoolean;

                        await coll.Indexes.CreateOneAsync(
                            new CreateIndexModel<BsonDocument>( keys, options ),
                            cancellationToken: ct ).ConfigureAwait( false );
                    }
                }
            }
        }
    }

    private static Dictionary<string, string> ParseSections( string content )
    {
        var bodies = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
        string current = null;
        var buffer = new System.Text.StringBuilder();

        foreach ( var rawLine in content.Split( '\n' ) )
        {
            var line = rawLine.TrimEnd( '\r' );
            var trimmed = line.TrimStart();

            if ( trimmed.StartsWith( '[' ) && trimmed.EndsWith( "]", StringComparison.Ordinal ) )
            {
                FlushSection( current, buffer, bodies );
                current = trimmed.Substring( 1, trimmed.Length - 2 ).Trim().ToLowerInvariant();
                buffer.Clear();
                continue;
            }

            if ( current == null )
                continue;

            buffer.Append( line ).Append( '\n' );
        }

        FlushSection( current, buffer, bodies );
        return bodies;
    }

    private static void FlushSection( string section, System.Text.StringBuilder buffer, Dictionary<string, string> bodies )
    {
        if ( section == null || buffer.Length == 0 )
            return;
        var body = buffer.ToString().Trim();
        if ( body.Length > 0 )
            bodies[section] = body;
    }
}

#endif
