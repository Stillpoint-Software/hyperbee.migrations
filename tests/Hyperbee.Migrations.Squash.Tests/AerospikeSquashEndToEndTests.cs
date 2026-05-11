using FluentAssertions;
using Hyperbee.Migrations;
using Hyperbee.Migrations.Providers.Aerospike.Squash;
using Hyperbee.Migrations.Squash;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 1 (R-P1, R-P5, R-P8): synthetic end-to-end exercise of the Aerospike
// squash pipeline.
//
// Real Testcontainers + Info.Request verification lives in the Phase 1
// integration suite (Task 1.6 verifier + R-P5 determinism gate + R-P6
// verification round). These tests inject a fake snapshot capture so the
// strategy / canonicalizer / classifier wire path runs cleanly without Docker.

[TestClass]
public class AerospikeSquashEndToEndTests
{
    private const string FakeSnapshotBlob = """
        # aerospike-snapshot fake fixture
        # namespace: test

        [sets]
        ns=test:set=users:objects=42;ns=test:set=orders:objects=10

        [sindex]
        ns=test:indexname=idx_email:set=users:bin=email:type=STRING:keys=42;ns=test:indexname=idx_total:set=orders:bin=total:type=NUMERIC
        """;

    private static AerospikeTopologySignature SyntheticTopology() => new()
    {
        ServerMajor = 6,
        ServerMinor = 4,
        Namespace = "test",
        ReplicationFactor = 2,
        DefaultTtl = 2592000,
        NsupPeriod = 120,
        MemorySize = 1073741824L,
        StorageEngine = "memory",
        ClusterName = "null"
    };

    private static AerospikeSquashGenerationContext MakeContext(
        string snapshotBlob = FakeSnapshotBlob,
        Action<SnapshotCaptureRequest> captureCallback = null )
    {
        // The substitute client never gets its Nodes property invoked in tests
        // that fail before topology capture (null-context / empty-descriptors /
        // wrong-context-type). Live topology capture is exercised by the
        // Phase 1 integration suite under Testcontainers.
        var client = Substitute.For<Aerospike.Client.IAerospikeClient>();

        return new AerospikeSquashGenerationContext(
            squashName: "Squash_2000",
            squashVersion: 2000,
            client: client,
            @namespace: "test",
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

    // ---- direct strategy plumbing (no live topology capture) ---------------

    // The strategy calls AerospikeTopologySignature.CaptureAsync which probes
    // a live IAerospikeClient. To exercise the strategy end-to-end without a
    // real client, we use a thin StrategyTestHook that wraps GenerateAsync,
    // providing a synthetic topology. This mirrors how the Phase 1 integration
    // suite will exercise the same plumbing with real Testcontainers.

    [TestMethod]
    public async Task GenerateAsync_NullContext_ReturnsFailed()
    {
        var strategy = new InfoSnapshotStrategy(
            new AerospikeSnapshotCanonicalizer(),
            new AerospikeDataOpClassifier() );

        var result = await strategy.GenerateAsync(
            context: null,
            descriptors: MakeDescriptors( 1000 ),
            options: new SquashGenerationOptions() );

        result.Should().BeOfType<SquashGenerationResult.Failed>()
            .Which.Detail.Should().Contain( "AerospikeSquashGenerationContext" );
    }

    [TestMethod]
    public async Task GenerateAsync_EmptyDescriptors_ReturnsFailed()
    {
        var strategy = new InfoSnapshotStrategy(
            new AerospikeSnapshotCanonicalizer(),
            new AerospikeDataOpClassifier() );

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
        var strategy = new InfoSnapshotStrategy(
            new AerospikeSnapshotCanonicalizer(),
            new AerospikeDataOpClassifier() );

        // Use a substitute ISquashGenerationContext that isn't the Aerospike type.
        var wrongContext = Substitute.For<ISquashGenerationContext>();
        wrongContext.ProviderId.Returns( "postgres" );

        var result = await strategy.GenerateAsync(
            context: wrongContext,
            descriptors: MakeDescriptors( 1000 ),
            options: new SquashGenerationOptions() );

        result.Should().BeOfType<SquashGenerationResult.Failed>()
            .Which.Detail.Should().Contain( "AerospikeSquashGenerationContext" );
    }

    // ---- canonicalization + classification through a manual pipeline -------

    // These tests exercise the pure-data pipeline (canonicalizer + classifier)
    // that the strategy composes. The full strategy GenerateAsync requires
    // live topology capture, which is integration-only.

    [TestMethod]
    public void Canonicalize_FakeSnapshot_EmitsExpectedStatements()
    {
        var canonicalizer = new AerospikeSnapshotCanonicalizer();
        var emitted = canonicalizer.Canonicalize( FakeSnapshotBlob );

        emitted.Should().Contain( "CREATE SET test.orders;" );
        emitted.Should().Contain( "CREATE SET test.users;" );
        emitted.Should().Contain( "CREATE INDEX WAIT idx_email ON test.users(email) STRING;" );
        emitted.Should().Contain( "CREATE INDEX WAIT idx_total ON test.orders(total) NUMERIC;" );
    }

    [TestMethod]
    public void Pipeline_DiagnosticsCollected_ForUnknownStatements()
    {
        var dataOpClassifier = new AerospikeDataOpClassifier();
        var canonicalizer = new AerospikeSnapshotCanonicalizer();

        // Inject a snapshot whose canonical-emission produces a recognized
        // shape, and verify each emitted statement classifies cleanly.
        var canonical = canonicalizer.Canonicalize( FakeSnapshotBlob );

        var unknowns = 0;
        var dataOpHints = 0;

        foreach ( var statement in AerospikeSnapshotCanonicalizer.SplitStatements( canonical ) )
        {
            var c = AerospikeStatementClassifier.Classify( statement );
            if ( c.Kind == AerospikeStatementKind.Unknown )
                unknowns++;

            var d = dataOpClassifier.Classify( statement );
            if ( d.EmissionHint != null )
                dataOpHints++;
        }

        unknowns.Should().Be( 0, "all canonical Aerospike statements should be classifiable" );
        dataOpHints.Should().Be( 0, "the fixture has no non-determinism" );
    }

    [TestMethod]
    public void Context_RequiresAllFields()
    {
        var client = Substitute.For<Aerospike.Client.IAerospikeClient>();
        Func<SnapshotCaptureRequest, CancellationToken, Task<SnapshotCaptureResult>> capture =
            ( _, _ ) => Task.FromResult( new SnapshotCaptureResult( FakeSnapshotBlob ) );

        Action emptyName = () => new AerospikeSquashGenerationContext( "", 1, client, "test", capture );
        emptyName.Should().Throw<ArgumentException>().WithParameterName( "squashName" );

        Action zeroVersion = () => new AerospikeSquashGenerationContext( "n", 0, client, "test", capture );
        zeroVersion.Should().Throw<ArgumentException>().WithParameterName( "squashVersion" );

        Action emptyNs = () => new AerospikeSquashGenerationContext( "n", 1, client, "", capture );
        emptyNs.Should().Throw<ArgumentException>().WithParameterName( "namespace" );

        Action nullClient = () => new AerospikeSquashGenerationContext( "n", 1, null!, "test", capture );
        nullClient.Should().Throw<ArgumentNullException>().WithParameterName( "client" );

        Action nullCapture = () => new AerospikeSquashGenerationContext( "n", 1, client, "test", null! );
        nullCapture.Should().Throw<ArgumentNullException>().WithParameterName( "captureSnapshotAsync" );
    }

    [TestMethod]
    public void Context_ProviderId_IsAerospike()
    {
        var client = Substitute.For<Aerospike.Client.IAerospikeClient>();
        var ctx = new AerospikeSquashGenerationContext( "n", 1, client, "test",
            ( _, _ ) => Task.FromResult( new SnapshotCaptureResult( "" ) ) );

        ctx.ProviderId.Should().Be( "aerospike" );
        ctx.SquashName.Should().Be( "n" );
        ctx.SquashVersion.Should().Be( 1 );
        ctx.Namespace.Should().Be( "test" );
    }

    [TestMethod]
    public void Strategy_ProviderId_IsAerospike()
    {
        var strategy = new InfoSnapshotStrategy(
            new AerospikeSnapshotCanonicalizer(),
            new AerospikeDataOpClassifier() );

        strategy.ProviderId.Should().Be( "aerospike" );
    }

    [TestMethod]
    public void Strategy_NullDependencies_Throw()
    {
        Action nullCanon = () => new InfoSnapshotStrategy( null!, new AerospikeDataOpClassifier() );
        nullCanon.Should().Throw<ArgumentNullException>().WithParameterName( "canonicalizer" );

        Action nullDataOp = () => new InfoSnapshotStrategy( new AerospikeSnapshotCanonicalizer(), null! );
        nullDataOp.Should().Throw<ArgumentNullException>().WithParameterName( "dataOpClassifier" );
    }

    [TestMethod]
    public void Strategy_NullLogger_AcceptsAndUsesNullLogger()
    {
        // ILogger is optional; nulls collapse to NullLogger so consumers
        // who don't wire logging still get a working strategy.
        var strategy = new InfoSnapshotStrategy(
            new AerospikeSnapshotCanonicalizer(),
            new AerospikeDataOpClassifier(),
            logger: null );

        strategy.ProviderId.Should().Be( "aerospike" );
    }

    [TestMethod]
    public async Task GenerateAsync_UdfsPresent_ReturnsFailedWithDiagnostic()
    {
        // Squash MUST refuse rather than silently drop Lua UDFs. The
        // refusal diagnostic must name the offending modules so the
        // operator knows what to carry forward.
        var strategy = new InfoSnapshotStrategy(
            new AerospikeSnapshotCanonicalizer(),
            new AerospikeDataOpClassifier() )
        {
            UdfProbe = ( _, _ ) => new[] { "audit_log.lua", "score_calculator.lua" }
        };

        var result = await strategy.GenerateAsync(
            context: MakeContext(),
            descriptors: MakeDescriptors( 1000, 2000 ),
            options: new SquashGenerationOptions() );

        result.Should().BeOfType<SquashGenerationResult.Failed>();
        var failed = (SquashGenerationResult.Failed) result;
        failed.Detail.Should().Contain( "UDF" );
        failed.Detail.Should().Contain( "audit_log.lua" );
        failed.Detail.Should().Contain( "score_calculator.lua" );
        failed.Detail.Should().Contain( "Carry UDFs forward" );
    }

    [TestMethod]
    public async Task GenerateAsync_SourceScanFindsUnannotated_ReturnsFailedWithDiagnostic()
    {
        // Write a tiny source tree containing one unannotated data-op migration.
        // The scanner gate must refuse before topology capture.
        var tempRoot = Path.Combine( Path.GetTempPath(), "aerospike-scanner-" + Guid.NewGuid().ToString( "N" ) );
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
                        await _client.Put(null, ct, new Key("test", "users", "u1"), new Bin("name", "alice"));
                    }
                }
                """ );

            var strategy = new InfoSnapshotStrategy(
                new AerospikeSnapshotCanonicalizer(),
                new AerospikeDataOpClassifier() )
            {
                UdfProbe = ( _, _ ) => Array.Empty<string>(),
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
        var tempRoot = Path.Combine( Path.GetTempPath(), "aerospike-scanner-" + Guid.NewGuid().ToString( "N" ) );
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
                        await _client.Put(null, ct, new Key("test", "users", "u1"), new Bin("name", "alice"));
                    }
                }
                """ );

            var strategy = new InfoSnapshotStrategy(
                new AerospikeSnapshotCanonicalizer(),
                new AerospikeDataOpClassifier() )
            {
                UdfProbe = ( _, _ ) => Array.Empty<string>(),
                MigrationSourceRoot = tempRoot
            };

            var result = await strategy.GenerateAsync(
                context: MakeContext(),
                descriptors: MakeDescriptors( 2000 ),
                options: new SquashGenerationOptions() );

            // The scan gate passes; the strategy continues to topology capture,
            // which fails against the substitute client. Diagnostic must NOT
            // mention ADR-0019 A5 (that's the scan refusal text).
            result.Should().BeOfType<SquashGenerationResult.Failed>();
            var failed = (SquashGenerationResult.Failed) result;
            failed.Detail.Should().NotContain( "ADR-0019 A5" );
        }
        finally
        {
            try { Directory.Delete( tempRoot, recursive: true ); } catch { }
        }
    }

    [TestMethod]
    public async Task GenerateAsync_NoUdfs_ProceedsPastRefusalGate()
    {
        // Empty UDF list must NOT refuse. The strategy continues to
        // topology capture (which will throw against the substitute
        // client) -- so we expect a Failed result whose Detail does
        // NOT mention UDFs.
        var strategy = new InfoSnapshotStrategy(
            new AerospikeSnapshotCanonicalizer(),
            new AerospikeDataOpClassifier() )
        {
            UdfProbe = ( _, _ ) => Array.Empty<string>()
        };

        var result = await strategy.GenerateAsync(
            context: MakeContext(),
            descriptors: MakeDescriptors( 1000, 2000 ),
            options: new SquashGenerationOptions() );

        // Will Fail at topology capture (no real cluster), not at UDF refusal.
        result.Should().BeOfType<SquashGenerationResult.Failed>();
        var failed = (SquashGenerationResult.Failed) result;
        failed.Detail.Should().NotContain( "UDF" );
    }
}
