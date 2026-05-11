using FluentAssertions;
using Hyperbee.Migrations;
using Hyperbee.Migrations.Providers.OpenSearch.Squash;
using Hyperbee.Migrations.Squash;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using OpenSearch.Client;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 2 Task 2.5: RestStateDiffStrategy + context + capture-helper unit
// coverage. Live cluster GenerateAsync happy path lives in the Phase 2
// integration suite (Testcontainers OpenSearch); these tests exercise the
// strategy's guard rails, the context's argument validation, and the capture
// helper's pure-logic ComposeBlob path.

[TestClass]
public class OpenSearchSquashEndToEndTests
{
    // ---- ComposeBlob (capture helper, pure-logic) --------------------------

    [TestMethod]
    public void ComposeBlob_ProducesSectionHeaderedFormat()
    {
        var bodies = new Dictionary<string, string>( StringComparer.Ordinal )
        {
            ["index_template"] = "{\"t1\": {}}",
            ["alias"] = "{\"a1\": {}}"
        };

        var blob = OpenSearchSnapshotCapture.ComposeBlob( bodies );

        blob.Should().Contain( "# opensearch-snapshot v1" );
        blob.Should().Contain( "[alias]" );
        blob.Should().Contain( "[index_template]" );
        blob.Should().Contain( "{\"t1\": {}}" );
        blob.Should().Contain( "{\"a1\": {}}" );
    }

    [TestMethod]
    public void ComposeBlob_EmitsSectionsInAlphabeticalOrder()
    {
        // Source dict order is implementation-defined, but the composed blob
        // must emit alphabetically so two captures of the same logical state
        // produce identical pre-canonical bytes.
        var bodies = new Dictionary<string, string>
        {
            ["zebra"] = "{}",
            ["alpha"] = "{}",
            ["mango"] = "{}"
        };

        var blob = OpenSearchSnapshotCapture.ComposeBlob( bodies );

        var alphaIdx = blob.IndexOf( "[alpha]", StringComparison.Ordinal );
        var mangoIdx = blob.IndexOf( "[mango]", StringComparison.Ordinal );
        var zebraIdx = blob.IndexOf( "[zebra]", StringComparison.Ordinal );

        alphaIdx.Should().BeLessThan( mangoIdx );
        mangoIdx.Should().BeLessThan( zebraIdx );
    }

    [TestMethod]
    public void ComposeBlob_EmptyDictionary_EmitsHeaderOnly()
    {
        var blob = OpenSearchSnapshotCapture.ComposeBlob( new Dictionary<string, string>() );

        blob.Should().StartWith( "# opensearch-snapshot v1" );
        blob.Should().NotContain( "[" );
    }

    [TestMethod]
    public void ComposeBlob_NullBodyEntry_TreatedAsEmpty()
    {
        var bodies = new Dictionary<string, string>
        {
            ["alias"] = null!
        };

        var blob = OpenSearchSnapshotCapture.ComposeBlob( bodies );

        // Section header still emits even if body is null; the canonicalizer
        // will catch a truly invalid body downstream.
        blob.Should().Contain( "[alias]" );
    }

    [TestMethod]
    public void ComposeBlob_NullDictionary_Throws()
    {
        Action act = () => OpenSearchSnapshotCapture.ComposeBlob( null! );
        act.Should().Throw<ArgumentNullException>();
    }

    [TestMethod]
    public void ComposeBlob_RoundTripsThroughCanonicalizer()
    {
        // The composed blob must canonicalize cleanly. Verify the end-to-end
        // pipeline produces expected sorted/normalized output.
        var bodies = new Dictionary<string, string>
        {
            ["index_template"] = "{\"users-template\": {\"creation_date\": \"111\", \"index_patterns\": [\"users-*\"]}}"
        };

        var blob = OpenSearchSnapshotCapture.ComposeBlob( bodies );
        var canon = new OpenSearchSnapshotCanonicalizer().Canonicalize( blob );

        canon.Should().Contain( "users-template" );
        canon.Should().Contain( "index_patterns" );
        canon.Should().NotContain( "creation_date" );
    }

    // ---- CaptureAsync guard rails ------------------------------------------

    [TestMethod]
    public async Task CaptureAsync_NullClient_Throws()
    {
        Func<Task> act = () => OpenSearchSnapshotCapture.CaptureAsync( null!, "_plugins/_ism" );
        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName( "client" );
    }

    [TestMethod]
    public async Task CaptureAsync_CancelledToken_Throws()
    {
        var client = Substitute.For<IOpenSearchClient>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => OpenSearchSnapshotCapture.CaptureAsync( client, "_plugins/_ism", cts.Token );
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---- Strategy guard rails ----------------------------------------------

    private static OpenSearchSquashGenerationContext MakeContext(
        string snapshotBlob = "[alias]\n{}",
        Action<SnapshotCaptureRequest> captureCallback = null )
    {
        var client = Substitute.For<IOpenSearchClient>();

        return new OpenSearchSquashGenerationContext(
            squashName: "Squash_2000",
            squashVersion: 2000,
            client: client,
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
        var strategy = new RestStateDiffStrategy(
            new OpenSearchSnapshotCanonicalizer(),
            new OpenSearchDataOpClassifier() );

        var result = await strategy.GenerateAsync(
            context: null!,
            descriptors: MakeDescriptors( 1000 ),
            options: new SquashGenerationOptions() );

        result.Should().BeOfType<SquashGenerationResult.Failed>()
            .Which.Detail.Should().Contain( "OpenSearchSquashGenerationContext" );
    }

    [TestMethod]
    public async Task GenerateAsync_EmptyDescriptors_ReturnsFailed()
    {
        var strategy = new RestStateDiffStrategy(
            new OpenSearchSnapshotCanonicalizer(),
            new OpenSearchDataOpClassifier() );

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
        var strategy = new RestStateDiffStrategy(
            new OpenSearchSnapshotCanonicalizer(),
            new OpenSearchDataOpClassifier() );

        var wrongContext = Substitute.For<ISquashGenerationContext>();
        wrongContext.ProviderId.Returns( "aerospike" );

        var result = await strategy.GenerateAsync(
            context: wrongContext,
            descriptors: MakeDescriptors( 1000 ),
            options: new SquashGenerationOptions() );

        result.Should().BeOfType<SquashGenerationResult.Failed>()
            .Which.Detail.Should().Contain( "OpenSearchSquashGenerationContext" );
    }

    [TestMethod]
    public void Strategy_ProviderId_IsOpenSearch()
    {
        var strategy = new RestStateDiffStrategy(
            new OpenSearchSnapshotCanonicalizer(),
            new OpenSearchDataOpClassifier() );

        strategy.ProviderId.Should().Be( "opensearch" );
    }

    [TestMethod]
    public void Strategy_NullDependencies_Throw()
    {
        Action nullCanon = () => new RestStateDiffStrategy( null!, new OpenSearchDataOpClassifier() );
        nullCanon.Should().Throw<ArgumentNullException>().WithParameterName( "canonicalizer" );

        Action nullClassifier = () => new RestStateDiffStrategy( new OpenSearchSnapshotCanonicalizer(), null! );
        nullClassifier.Should().Throw<ArgumentNullException>().WithParameterName( "dataOpClassifier" );
    }

    [TestMethod]
    public void Strategy_NullLogger_AcceptsAndUsesNullLogger()
    {
        // Per Aerospike Sev 2 G pattern: ILogger is optional; nulls collapse
        // to NullLogger so consumers without logging configured still get a
        // working strategy.
        var strategy = new RestStateDiffStrategy(
            new OpenSearchSnapshotCanonicalizer(),
            new OpenSearchDataOpClassifier(),
            logger: null );

        strategy.ProviderId.Should().Be( "opensearch" );
    }

    // ---- Context validation ------------------------------------------------

    [TestMethod]
    public void Context_RequiresAllFields()
    {
        var client = Substitute.For<IOpenSearchClient>();
        Func<SnapshotCaptureRequest, CancellationToken, Task<SnapshotCaptureResult>> capture =
            ( _, _ ) => Task.FromResult( new SnapshotCaptureResult( "[alias]\n{}" ) );

        Action emptyName = () => new OpenSearchSquashGenerationContext( "", 1, client, capture );
        emptyName.Should().Throw<ArgumentException>().WithParameterName( "squashName" );

        Action zeroVersion = () => new OpenSearchSquashGenerationContext( "n", 0, client, capture );
        zeroVersion.Should().Throw<ArgumentException>().WithParameterName( "squashVersion" );

        Action nullClient = () => new OpenSearchSquashGenerationContext( "n", 1, null!, capture );
        nullClient.Should().Throw<ArgumentNullException>().WithParameterName( "client" );

        Action nullCapture = () => new OpenSearchSquashGenerationContext( "n", 1, client, null! );
        nullCapture.Should().Throw<ArgumentNullException>().WithParameterName( "captureSnapshotAsync" );
    }

    [TestMethod]
    public void Context_ProviderId_IsOpenSearch()
    {
        var ctx = MakeContext();
        ctx.ProviderId.Should().Be( "opensearch" );
        ctx.SquashName.Should().Be( "Squash_2000" );
        ctx.SquashVersion.Should().Be( 2000 );
    }

    // ---- Source-scan refusal gate (Task 2.7) -------------------------------

    [TestMethod]
    public async Task GenerateAsync_SourceScanFindsUnannotated_ReturnsFailedWithDiagnostic()
    {
        var tempRoot = Path.Combine( Path.GetTempPath(), "opensearch-scanner-" + Guid.NewGuid().ToString( "N" ) );
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
                        await _client.IndexAsync(new User { Id = "u1" });
                    }
                }
                """ );

            var strategy = new RestStateDiffStrategy(
                new OpenSearchSnapshotCanonicalizer(),
                new OpenSearchDataOpClassifier() )
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
        var tempRoot = Path.Combine( Path.GetTempPath(), "opensearch-scanner-" + Guid.NewGuid().ToString( "N" ) );
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
                        await _client.IndexAsync(new User { Id = "u1" });
                    }
                }
                """ );

            var strategy = new RestStateDiffStrategy(
                new OpenSearchSnapshotCanonicalizer(),
                new OpenSearchDataOpClassifier() )
            {
                MigrationSourceRoot = tempRoot
            };

            var result = await strategy.GenerateAsync(
                context: MakeContext(),
                descriptors: MakeDescriptors( 2000 ),
                options: new SquashGenerationOptions() );

            // The scan gate passes; the strategy continues to topology
            // capture which fails against the substitute client. Diagnostic
            // must NOT mention ADR-0019 A5 (that's the scan refusal text).
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
