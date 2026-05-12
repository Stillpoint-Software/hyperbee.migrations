using System.Text.Json.Nodes;
using Couchbase;
using FluentAssertions;
using Hyperbee.Migrations;
using Hyperbee.Migrations.Providers.Couchbase.Services;
using Hyperbee.Migrations.Providers.Couchbase.Squash;
using Hyperbee.Migrations.Squash;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 4 Task 4.5: HybridStrategy + context + capture-helper unit coverage.
// Live cluster GenerateAsync happy path lives in the Phase 4 integration
// suite (Testcontainers Couchbase); these tests exercise the strategy's
// guard rails, the context's argument validation, and the capture helper's
// pure-logic ComposeBlob path. Mirrors MongoDB Phase 3 Task 3.5 shape.

[TestClass]
public class CouchbaseSquashEndToEndTests
{
    // ---- ComposeBlob (capture helper, pure-logic) --------------------------

    [TestMethod]
    public void ComposeBlob_ProducesSectionHeaderedFormat()
    {
        var bucketDetails = JsonNode.Parse( """{"bucketType":"membase","ramQuotaMB":256}""" );
        var keyspaces = new List<JsonNode>
        {
            JsonNode.Parse( """{"id":"myapp/_default/_default","name":"_default","keyspace_id":"myapp"}""" )
        };
        var indexes = new List<JsonNode>
        {
            JsonNode.Parse( """{"name":"idx_email","keyspace_id":"myapp","scope_id":"_default"}""" )
        };

        var blob = CouchbaseSnapshotCapture.ComposeBlob( "myapp", bucketDetails, keyspaces, indexes );

        blob.Should().Contain( "# couchbase-snapshot v1" );
        blob.Should().Contain( "# bucket: myapp" );
        blob.Should().Contain( "[buckets]" );
        blob.Should().Contain( "[keyspaces]" );
        blob.Should().Contain( "[indexes]" );
        blob.Should().Contain( "myapp" );
        blob.Should().Contain( "idx_email" );
    }

    [TestMethod]
    public void ComposeBlob_EmitsSectionsInOrder_BucketsKeyspacesIndexes()
    {
        var bucketDetails = JsonNode.Parse( "{}" );
        var keyspaces = new List<JsonNode> { JsonNode.Parse( """{"id":"k1"}""" ) };
        var indexes = new List<JsonNode> { JsonNode.Parse( """{"name":"i1","keyspace_id":"myapp"}""" ) };

        var blob = CouchbaseSnapshotCapture.ComposeBlob( "myapp", bucketDetails, keyspaces, indexes );

        var bktIdx = blob.IndexOf( "[buckets]", StringComparison.Ordinal );
        var ksIdx = blob.IndexOf( "[keyspaces]", StringComparison.Ordinal );
        var idxIdx = blob.IndexOf( "[indexes]", StringComparison.Ordinal );

        bktIdx.Should().BeGreaterThan( -1 );
        bktIdx.Should().BeLessThan( ksIdx );
        ksIdx.Should().BeLessThan( idxIdx );
    }

    [TestMethod]
    public void ComposeBlob_AllEmpty_EmitsHeaderOnly()
    {
        var blob = CouchbaseSnapshotCapture.ComposeBlob(
            "myapp",
            bucketDetails: null,
            keyspaceRows: Array.Empty<JsonNode>(),
            indexRows: Array.Empty<JsonNode>() );

        blob.Should().Contain( "# couchbase-snapshot v1" );
        blob.Should().Contain( "# bucket: myapp" );
        blob.Should().NotContain( "[buckets]" );
        blob.Should().NotContain( "[keyspaces]" );
        blob.Should().NotContain( "[indexes]" );
    }

    [TestMethod]
    public void ComposeBlob_RoundTripsThroughCanonicalizer()
    {
        // The composed blob must canonicalize cleanly. Index id + bucket
        // docCount ephemerals strip; state=online drops; deferred preserves.
        var bucketDetails = JsonNode.Parse( """{"bucketType":"membase","ramQuotaMB":256,"docCount":12345}""" );
        var keyspaces = new List<JsonNode>
        {
            JsonNode.Parse( """{"id":"myapp/_default/_default","name":"_default","keyspace_id":"myapp"}""" )
        };
        var indexes = new List<JsonNode>
        {
            JsonNode.Parse( """{"id":"abc","name":"idx_email","keyspace_id":"myapp","state":"online"}""" ),
            JsonNode.Parse( """{"id":"def","name":"idx_phone","keyspace_id":"myapp","state":"deferred"}""" )
        };

        var blob = CouchbaseSnapshotCapture.ComposeBlob( "myapp", bucketDetails, keyspaces, indexes );
        var canon = new CouchbaseSnapshotCanonicalizer().Canonicalize( blob );

        canon.Should().Contain( "idx_email" );
        canon.Should().Contain( "idx_phone" );
        // ephemerals stripped
        canon.Should().NotContain( "docCount" );
        canon.Should().NotContain( "\"id\":" );
        canon.Should().NotContain( "\"abc\"" );
        canon.Should().NotContain( "\"def\"" );
        // state handling: online dropped, deferred preserved
        canon.Should().NotContain( "online" );
        canon.Should().Contain( "deferred" );
    }

    [TestMethod]
    public void ComposeBlob_EmptyBucketName_Throws()
    {
        Action act = () => CouchbaseSnapshotCapture.ComposeBlob(
            "",
            bucketDetails: null,
            keyspaceRows: Array.Empty<JsonNode>(),
            indexRows: Array.Empty<JsonNode>() );
        act.Should().Throw<ArgumentException>().WithParameterName( "bucketName" );
    }

    // ---- CaptureAsync guard rails ------------------------------------------

    [TestMethod]
    public async Task CaptureAsync_NullCluster_Throws()
    {
        var restApi = Substitute.For<ICouchbaseRestApiService>();
        Func<Task> act = () => CouchbaseSnapshotCapture.CaptureAsync( null, restApi, "myapp" );
        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName( "cluster" );
    }

    [TestMethod]
    public async Task CaptureAsync_NullRestApi_Throws()
    {
        var cluster = Substitute.For<ICluster>();
        Func<Task> act = () => CouchbaseSnapshotCapture.CaptureAsync( cluster, null, "myapp" );
        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName( "restApi" );
    }

    [TestMethod]
    public async Task CaptureAsync_EmptyBucketName_Throws()
    {
        var cluster = Substitute.For<ICluster>();
        var restApi = Substitute.For<ICouchbaseRestApiService>();
        Func<Task> act = () => CouchbaseSnapshotCapture.CaptureAsync( cluster, restApi, "" );
        await act.Should().ThrowAsync<ArgumentException>().WithParameterName( "bucketName" );
    }

    // ---- Strategy guard rails ----------------------------------------------

    private static CouchbaseSquashGenerationContext MakeContext(
        string snapshotBlob = "[buckets]\n{}",
        Action<SnapshotCaptureRequest> captureCallback = null )
    {
        var cluster = Substitute.For<ICluster>();
        var restApi = Substitute.For<ICouchbaseRestApiService>();

        return new CouchbaseSquashGenerationContext(
            squashName: "Squash_2000",
            squashVersion: 2000,
            cluster: cluster,
            restApi: restApi,
            bucketName: "myapp",
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
        var strategy = new HybridStrategy(
            new CouchbaseSnapshotCanonicalizer(),
            new CouchbaseDataOpClassifier() );

        var result = await strategy.GenerateAsync(
            context: null,
            descriptors: MakeDescriptors( 1000 ),
            options: new SquashGenerationOptions() );

        result.Should().BeOfType<SquashGenerationResult.Failed>()
            .Which.Detail.Should().Contain( "CouchbaseSquashGenerationContext" );
    }

    [TestMethod]
    public async Task GenerateAsync_EmptyDescriptors_ReturnsFailed()
    {
        var strategy = new HybridStrategy(
            new CouchbaseSnapshotCanonicalizer(),
            new CouchbaseDataOpClassifier() );

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
        var strategy = new HybridStrategy(
            new CouchbaseSnapshotCanonicalizer(),
            new CouchbaseDataOpClassifier() );

        var wrongContext = Substitute.For<ISquashGenerationContext>();
        wrongContext.ProviderId.Returns( "mongodb" );

        var result = await strategy.GenerateAsync(
            context: wrongContext,
            descriptors: MakeDescriptors( 1000 ),
            options: new SquashGenerationOptions() );

        result.Should().BeOfType<SquashGenerationResult.Failed>()
            .Which.Detail.Should().Contain( "CouchbaseSquashGenerationContext" );
    }

    [TestMethod]
    public void Strategy_ProviderId_IsCouchbase()
    {
        var strategy = new HybridStrategy(
            new CouchbaseSnapshotCanonicalizer(),
            new CouchbaseDataOpClassifier() );

        strategy.ProviderId.Should().Be( "couchbase" );
    }

    [TestMethod]
    public void Strategy_NullDependencies_Throw()
    {
        Action nullCanon = () => new HybridStrategy( null, new CouchbaseDataOpClassifier() );
        nullCanon.Should().Throw<ArgumentNullException>().WithParameterName( "canonicalizer" );

        Action nullClassifier = () => new HybridStrategy( new CouchbaseSnapshotCanonicalizer(), null );
        nullClassifier.Should().Throw<ArgumentNullException>().WithParameterName( "dataOpClassifier" );
    }

    [TestMethod]
    public void Strategy_NullLogger_AcceptsAndUsesNullLogger()
    {
        var strategy = new HybridStrategy(
            new CouchbaseSnapshotCanonicalizer(),
            new CouchbaseDataOpClassifier(),
            logger: null );

        strategy.ProviderId.Should().Be( "couchbase" );
    }

    // ---- Context validation ------------------------------------------------

    [TestMethod]
    public void Context_RequiresAllFields()
    {
        var cluster = Substitute.For<ICluster>();
        var restApi = Substitute.For<ICouchbaseRestApiService>();
        Func<SnapshotCaptureRequest, CancellationToken, Task<SnapshotCaptureResult>> capture =
            ( _, _ ) => Task.FromResult( new SnapshotCaptureResult( "[buckets]\n{}" ) );

        Action emptyName = () => new CouchbaseSquashGenerationContext( "", 1, cluster, restApi, "myapp", capture );
        emptyName.Should().Throw<ArgumentException>().WithParameterName( "squashName" );

        Action zeroVersion = () => new CouchbaseSquashGenerationContext( "n", 0, cluster, restApi, "myapp", capture );
        zeroVersion.Should().Throw<ArgumentException>().WithParameterName( "squashVersion" );

        Action emptyBucket = () => new CouchbaseSquashGenerationContext( "n", 1, cluster, restApi, "", capture );
        emptyBucket.Should().Throw<ArgumentException>().WithParameterName( "bucketName" );

        Action nullCluster = () => new CouchbaseSquashGenerationContext( "n", 1, null, restApi, "myapp", capture );
        nullCluster.Should().Throw<ArgumentNullException>().WithParameterName( "cluster" );

        Action nullRestApi = () => new CouchbaseSquashGenerationContext( "n", 1, cluster, null, "myapp", capture );
        nullRestApi.Should().Throw<ArgumentNullException>().WithParameterName( "restApi" );

        Action nullCapture = () => new CouchbaseSquashGenerationContext( "n", 1, cluster, restApi, "myapp", null );
        nullCapture.Should().Throw<ArgumentNullException>().WithParameterName( "captureSnapshotAsync" );
    }

    [TestMethod]
    public void Context_ProviderId_IsCouchbase()
    {
        var ctx = MakeContext();
        ctx.ProviderId.Should().Be( "couchbase" );
        ctx.SquashName.Should().Be( "Squash_2000" );
        ctx.SquashVersion.Should().Be( 2000 );
        ctx.BucketName.Should().Be( "myapp" );
    }

    // ---- Source-scan refusal gate (Task 4.7) -------------------------------

    [TestMethod]
    public async Task GenerateAsync_SourceScanFindsUnannotated_ReturnsFailedWithDiagnostic()
    {
        var tempRoot = Path.Combine( Path.GetTempPath(), "couchbase-scanner-" + Guid.NewGuid().ToString( "N" ) );
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
                        await collection.UpsertAsync("u1", new { name = "alpha" });
                    }
                }
                """ );

            var strategy = new HybridStrategy(
                new CouchbaseSnapshotCanonicalizer(),
                new CouchbaseDataOpClassifier() )
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
        var tempRoot = Path.Combine( Path.GetTempPath(), "couchbase-scanner-" + Guid.NewGuid().ToString( "N" ) );
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
                        await collection.UpsertAsync("u1", new { name = "alpha" });
                    }
                }
                """ );

            var strategy = new HybridStrategy(
                new CouchbaseSnapshotCanonicalizer(),
                new CouchbaseDataOpClassifier() )
            {
                MigrationSourceRoot = tempRoot
            };

            var result = await strategy.GenerateAsync(
                context: MakeContext(),
                descriptors: MakeDescriptors( 2000 ),
                options: new SquashGenerationOptions() );

            // Scan gate passes; topology capture fails against the
            // substitute cluster/restApi. Diagnostic must NOT mention
            // ADR-0019 A5.
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
