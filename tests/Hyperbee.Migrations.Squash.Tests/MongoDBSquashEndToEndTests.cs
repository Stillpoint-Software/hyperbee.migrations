using FluentAssertions;
using Hyperbee.Migrations;
using Hyperbee.Migrations.Providers.MongoDB.Squash;
using Hyperbee.Migrations.Squash;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using MongoDB.Bson;
using MongoDB.Driver;
using NSubstitute;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 3 Task 3.5: IntrospectionSnapshotStrategy + context + capture-helper
// unit coverage. Live cluster GenerateAsync happy path lives in the Phase 3
// integration suite (Testcontainers MongoDB); these tests exercise the
// strategy's guard rails, the context's argument validation, and the capture
// helper's pure-logic ComposeBlob path.

[TestClass]
public class MongoDBSquashEndToEndTests
{
    // ---- ComposeBlob (capture helper, pure-logic) --------------------------

    [TestMethod]
    public void ComposeBlob_ProducesSectionHeaderedFormat()
    {
        var collections = new Dictionary<string, BsonDocument>( StringComparer.Ordinal )
        {
            ["users"] = new() { { "type", "collection" } },
            ["orders"] = new() { { "type", "collection" } }
        };
        var indexes = new Dictionary<string, BsonArray>( StringComparer.Ordinal )
        {
            ["users"] = new()
            {
                new BsonDocument { { "name", "idx_email" }, { "key", new BsonDocument { { "email", 1 } } } }
            }
        };

        var blob = MongoDBSnapshotCapture.ComposeBlob( "appdb", collections, indexes );

        blob.Should().Contain( "# mongodb-snapshot v1" );
        blob.Should().Contain( "# database: appdb" );
        blob.Should().Contain( "[collections]" );
        blob.Should().Contain( "[indexes]" );
        blob.Should().Contain( "users" );
        blob.Should().Contain( "orders" );
        blob.Should().Contain( "idx_email" );
    }

    [TestMethod]
    public void ComposeBlob_EmitsCollectionsBeforeIndexes()
    {
        var collections = new Dictionary<string, BsonDocument> { ["users"] = new() };
        var indexes = new Dictionary<string, BsonArray> { ["users"] = new() { new BsonDocument { { "name", "idx" } } } };

        var blob = MongoDBSnapshotCapture.ComposeBlob( "appdb", collections, indexes );

        var collIdx = blob.IndexOf( "[collections]", StringComparison.Ordinal );
        var idxIdx = blob.IndexOf( "[indexes]", StringComparison.Ordinal );

        collIdx.Should().BeGreaterThan( -1 );
        idxIdx.Should().BeGreaterThan( -1 );
        collIdx.Should().BeLessThan( idxIdx );
    }

    [TestMethod]
    public void ComposeBlob_EmptyCollectionsAndIndexes_EmitsHeaderOnly()
    {
        var blob = MongoDBSnapshotCapture.ComposeBlob(
            "appdb",
            new Dictionary<string, BsonDocument>(),
            new Dictionary<string, BsonArray>() );

        blob.Should().Contain( "# mongodb-snapshot v1" );
        blob.Should().Contain( "# database: appdb" );
        blob.Should().NotContain( "[collections]" );
        blob.Should().NotContain( "[indexes]" );
    }

    [TestMethod]
    public void ComposeBlob_RoundTripsThroughCanonicalizer()
    {
        // The composed blob must canonicalize cleanly.
        var collections = new Dictionary<string, BsonDocument>
        {
            ["users"] = new() { { "type", "collection" }, { "info", new BsonDocument { { "uuid", "abc-123" }, { "readOnly", false } } } }
        };
        var indexes = new Dictionary<string, BsonArray>
        {
            ["users"] = new()
            {
                new BsonDocument { { "v", 2 }, { "key", new BsonDocument { { "email", 1 } } }, { "name", "idx_email" } }
            }
        };

        var blob = MongoDBSnapshotCapture.ComposeBlob( "appdb", collections, indexes );
        var canon = new MongoDBSnapshotCanonicalizer().Canonicalize( blob );

        canon.Should().Contain( "users" );
        canon.Should().Contain( "idx_email" );
        canon.Should().NotContain( "uuid" );
        canon.Should().NotContain( "abc-123" );
        canon.Should().NotContain( "\"v\":" );
        canon.Should().NotContain( "readOnly" );
    }

    [TestMethod]
    public void ComposeBlob_NullCollections_Throws()
    {
        Action act = () => MongoDBSnapshotCapture.ComposeBlob( "appdb", null, new Dictionary<string, BsonArray>() );
        act.Should().Throw<ArgumentNullException>();
    }

    [TestMethod]
    public void ComposeBlob_NullIndexes_Throws()
    {
        Action act = () => MongoDBSnapshotCapture.ComposeBlob( "appdb", new Dictionary<string, BsonDocument>(), null );
        act.Should().Throw<ArgumentNullException>();
    }

    // ---- CaptureAsync guard rails ------------------------------------------

    [TestMethod]
    public async Task CaptureAsync_NullClient_Throws()
    {
        Func<Task> act = () => MongoDBSnapshotCapture.CaptureAsync( null, "appdb" );
        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName( "client" );
    }

    [TestMethod]
    public async Task CaptureAsync_EmptyDatabaseName_Throws()
    {
        var client = Substitute.For<IMongoClient>();
        Func<Task> act = () => MongoDBSnapshotCapture.CaptureAsync( client, "" );
        await act.Should().ThrowAsync<ArgumentException>().WithParameterName( "databaseName" );
    }

    // ---- Strategy guard rails ----------------------------------------------

    private static MongoDBSquashGenerationContext MakeContext(
        string snapshotBlob = "[collections]\n{}",
        Action<SnapshotCaptureRequest> captureCallback = null )
    {
        var client = Substitute.For<IMongoClient>();

        return new MongoDBSquashGenerationContext(
            squashName: "Squash_2000",
            squashVersion: 2000,
            client: client,
            databaseName: "appdb",
            captureSnapshotAsync: ( req, _ ) =>
            {
                captureCallback?.Invoke( req );
                return Task.FromResult( new SnapshotCaptureResult( snapshotBlob ) );
            } );
    }

    private static IReadOnlyList<MigrationDescriptor> MakeDescriptors( params long[] versions )
        => versions
            .Select( v => new MigrationDescriptor(
                Type: typeof( object ),
                Attribute: new MigrationAttribute( v ),
                ResolvedReplaces: Array.Empty<long>() ) )
            .ToList();

    [TestMethod]
    public async Task GenerateAsync_NullContext_ReturnsFailed()
    {
        var strategy = new IntrospectionSnapshotStrategy(
            new MongoDBSnapshotCanonicalizer(),
            new MongoDBDataOpClassifier() );

        var result = await strategy.GenerateAsync(
            context: null,
            descriptors: MakeDescriptors( 1000 ),
            options: new SquashGenerationOptions() );

        result.Should().BeOfType<SquashGenerationResult.Failed>()
            .Which.Detail.Should().Contain( "MongoDBSquashGenerationContext" );
    }

    [TestMethod]
    public async Task GenerateAsync_EmptyDescriptors_ReturnsFailed()
    {
        var strategy = new IntrospectionSnapshotStrategy(
            new MongoDBSnapshotCanonicalizer(),
            new MongoDBDataOpClassifier() );

        var result = await strategy.GenerateAsync(
            context: MakeContext(),
            descriptors: Array.Empty<MigrationDescriptor>(),
            options: new SquashGenerationOptions() );

        result.Should().BeOfType<SquashGenerationResult.Failed>()
            .Which.Detail.Should().Contain( "No migrations supplied" );
    }

    [TestMethod]
    public async Task GenerateAsync_WrongContextType_ReturnsFailed()
    {
        var strategy = new IntrospectionSnapshotStrategy(
            new MongoDBSnapshotCanonicalizer(),
            new MongoDBDataOpClassifier() );

        var wrongContext = Substitute.For<ISquashGenerationContext>();
        wrongContext.ProviderId.Returns( "aerospike" );

        var result = await strategy.GenerateAsync(
            context: wrongContext,
            descriptors: MakeDescriptors( 1000 ),
            options: new SquashGenerationOptions() );

        result.Should().BeOfType<SquashGenerationResult.Failed>()
            .Which.Detail.Should().Contain( "MongoDBSquashGenerationContext" );
    }

    [TestMethod]
    public void Strategy_ProviderId_IsMongoDB()
    {
        var strategy = new IntrospectionSnapshotStrategy(
            new MongoDBSnapshotCanonicalizer(),
            new MongoDBDataOpClassifier() );

        strategy.ProviderId.Should().Be( "mongodb" );
    }

    [TestMethod]
    public void Strategy_NullDependencies_Throw()
    {
        Action nullCanon = () => new IntrospectionSnapshotStrategy( null, new MongoDBDataOpClassifier() );
        nullCanon.Should().Throw<ArgumentNullException>().WithParameterName( "canonicalizer" );

        Action nullClassifier = () => new IntrospectionSnapshotStrategy( new MongoDBSnapshotCanonicalizer(), null );
        nullClassifier.Should().Throw<ArgumentNullException>().WithParameterName( "dataOpClassifier" );
    }

    [TestMethod]
    public void Strategy_NullLogger_AcceptsAndUsesNullLogger()
    {
        var strategy = new IntrospectionSnapshotStrategy(
            new MongoDBSnapshotCanonicalizer(),
            new MongoDBDataOpClassifier(),
            logger: null );

        strategy.ProviderId.Should().Be( "mongodb" );
    }

    // ---- Context validation ------------------------------------------------

    [TestMethod]
    public void Context_RequiresAllFields()
    {
        var client = Substitute.For<IMongoClient>();
        Func<SnapshotCaptureRequest, CancellationToken, Task<SnapshotCaptureResult>> capture =
            ( _, _ ) => Task.FromResult( new SnapshotCaptureResult( "[collections]\n{}" ) );

        Action emptyName = () => new MongoDBSquashGenerationContext( "", 1, client, "appdb", capture );
        emptyName.Should().Throw<ArgumentException>().WithParameterName( "squashName" );

        Action zeroVersion = () => new MongoDBSquashGenerationContext( "n", 0, client, "appdb", capture );
        zeroVersion.Should().Throw<ArgumentException>().WithParameterName( "squashVersion" );

        Action emptyDb = () => new MongoDBSquashGenerationContext( "n", 1, client, "", capture );
        emptyDb.Should().Throw<ArgumentException>().WithParameterName( "databaseName" );

        Action nullClient = () => new MongoDBSquashGenerationContext( "n", 1, null, "appdb", capture );
        nullClient.Should().Throw<ArgumentNullException>().WithParameterName( "client" );

        Action nullCapture = () => new MongoDBSquashGenerationContext( "n", 1, client, "appdb", null );
        nullCapture.Should().Throw<ArgumentNullException>().WithParameterName( "captureSnapshotAsync" );
    }

    [TestMethod]
    public void Context_ProviderId_IsMongoDB()
    {
        var ctx = MakeContext();
        ctx.ProviderId.Should().Be( "mongodb" );
        ctx.SquashName.Should().Be( "Squash_2000" );
        ctx.SquashVersion.Should().Be( 2000 );
        ctx.DatabaseName.Should().Be( "appdb" );
    }

    // ---- Source-scan refusal gate (Task 3.7) -------------------------------

    [TestMethod]
    public async Task GenerateAsync_SourceScanFindsUnannotated_ReturnsFailedWithDiagnostic()
    {
        var tempRoot = Path.Combine( Path.GetTempPath(), "mongodb-scanner-" + Guid.NewGuid().ToString( "N" ) );
        Directory.CreateDirectory( tempRoot );
        try
        {
            File.WriteAllText( Path.Combine( tempRoot, "SeedUsers.cs" ), """
                using Hyperbee.Migrations;
                namespace App;
                [Migration(2000)]
                public class SeedUsers : Migration
                {
                    public override async Task UpAsync(CancellationToken ct)
                    {
                        await collection.InsertOneAsync(new User { Id = "u1" });
                    }
                }
                """ );

            var strategy = new IntrospectionSnapshotStrategy(
                new MongoDBSnapshotCanonicalizer(),
                new MongoDBDataOpClassifier() )
            {
                MigrationSourceRoot = tempRoot
            };

            var result = await strategy.GenerateAsync(
                context: MakeContext(),
                descriptors: MakeDescriptors( 2000 ),
                options: new SquashGenerationOptions() );

            result.Should().BeOfType<SquashGenerationResult.Failed>();
            var failed = (SquashGenerationResult.Failed) result;
            failed.Detail.Should().Contain( "ADR-0019 A5" );
            failed.Detail.Should().Contain( "SeedUsers" );
            failed.Detail.Should().Contain( "[DataMigration]" );
        }
        finally
        {
            try { Directory.Delete( tempRoot, recursive: true ); } catch { }
        }
    }

    [TestMethod]
    public async Task GenerateAsync_SourceScanAllAnnotated_ProceedsPastScanGate()
    {
        var tempRoot = Path.Combine( Path.GetTempPath(), "mongodb-scanner-" + Guid.NewGuid().ToString( "N" ) );
        Directory.CreateDirectory( tempRoot );
        try
        {
            File.WriteAllText( Path.Combine( tempRoot, "SeedUsers.cs" ), """
                using Hyperbee.Migrations;
                namespace App;
                [Migration(2000)]
                [DataMigration]
                public class SeedUsers : Migration
                {
                    public override async Task UpAsync(CancellationToken ct)
                    {
                        await collection.InsertOneAsync(new User { Id = "u1" });
                    }
                }
                """ );

            var strategy = new IntrospectionSnapshotStrategy(
                new MongoDBSnapshotCanonicalizer(),
                new MongoDBDataOpClassifier() )
            {
                MigrationSourceRoot = tempRoot
            };

            var result = await strategy.GenerateAsync(
                context: MakeContext(),
                descriptors: MakeDescriptors( 2000 ),
                options: new SquashGenerationOptions() );

            // Scan gate passes; topology capture fails against the
            // substitute client. Diagnostic must NOT mention ADR-0019 A5.
            result.Should().BeOfType<SquashGenerationResult.Failed>();
            var failed = (SquashGenerationResult.Failed) result;
            failed.Detail.Should().NotContain( "ADR-0019 A5" );
        }
        finally
        {
            try { Directory.Delete( tempRoot, recursive: true ); } catch { }
        }
    }
}
