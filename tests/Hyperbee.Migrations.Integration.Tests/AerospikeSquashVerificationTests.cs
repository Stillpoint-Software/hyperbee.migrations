//#define INTEGRATIONS
using Aerospike.Client;
using Hyperbee.Migrations;
using Hyperbee.Migrations.Integration.Tests.Container.Aerospike;
using Hyperbee.Migrations.Providers.Aerospike.Extensions;
using Hyperbee.Migrations.Providers.Aerospike.Parsers;
using Hyperbee.Migrations.Providers.Aerospike.Squash;
using Hyperbee.Migrations.Squash;

namespace Hyperbee.Migrations.Integration.Tests;

#if INTEGRATIONS

// Phase 1 (R-P6): Aerospike squash verification round (A4).
//
// Contract: re-applying the historical migration range produces the same
// canonicalized state as applying the GENERATED squash content. This is the
// load-bearing equivalence proof that the squash codegen captures the full
// structural state.
//
// Round-trip flow:
//   1. Set up structural state in the namespace (sets + indexes).
//   2. Capture state A (the "historical" snapshot).
//   3. Run InfoSnapshotStrategy.GenerateAsync -> Generated.Content (AQL).
//   4. Wipe the namespace (drop indexes, delete sentinel records).
//   5. Apply Generated.Content via parsed statements -> client calls.
//   6. Capture state B.
//   7. AerospikeSquashVerifier.VerifyAsync -> expect Success.
//
// Guarded by `#if INTEGRATIONS`; run locally with /p:EnableIntegrationTests=true.
//
// [TestCategory("LocalOnly")]: same environment-sensitivity as
// AerospikeSquashDeterminismTests -- Docker resource contention +
// container reuse surfaces as flaky AssemblyInitialize in the full
// integration suite. Verifier correctness is byte-tested by
// AerospikeSquashVerifierTests in the unit suite.

[TestClass]
[DoNotParallelize]
[TestCategory( "LocalOnly" )]
public class AerospikeSquashVerificationTests
{
    private IAsyncClient _client;
    private const string Namespace = "test";
    private const string SetName = "verify_set";

    [TestInitialize]
    public void Setup()
    {
        _client = AerospikeTestContainer.AsyncClient;
        Assert.IsNotNull( _client, "AerospikeTestContainer must initialize before this test class runs." );
    }

    [TestMethod]
    public async Task EmptyNamespace_RoundTrip_ReturnsSuccess()
    {
        // Trivial baseline: empty-namespace round-trip. Both A and B return
        // an empty canonical script; verifier must Succeed. Proves the
        // wiring works end-to-end before the populated test exercises real
        // structural state.
        var ctx = MakeContext();
        var generated = await GenerateAsync( ctx );

        var verifier = new AerospikeSquashVerifier( new AerospikeSnapshotCanonicalizer() )
        {
            CaptureFromGeneratedAsync = async ( _, _, ct ) =>
            {
                var blob = await AerospikeSnapshotCapture.CaptureAsync( _client, Namespace, ct );
                return new SnapshotCaptureResult( blob );
            }
        };

        var result = await verifier.VerifyAsync( ctx, generated );

        if ( result is VerificationResult.Failed failed )
            Assert.Fail( $"Verification failed: {failed.Detail}\n{failed.DiffSummary}" );

        Assert.IsInstanceOfType<VerificationResult.Success>( result );
    }

    [TestMethod]
    public async Task PopulatedNamespace_RoundTrip_ReturnsSuccess()
    {
        // Set up structural state: two indexes + sentinel records so the set
        // appears in sets/<ns>.
        await _client.CreateIndexAsync( Namespace, SetName, "idx_email_verify", "email", IndexType.STRING );
        await _client.CreateIndexAsync( Namespace, SetName, "idx_age_verify", "age", IndexType.NUMERIC );
        await _client.Put( new WritePolicy { expiration = 0 }, CancellationToken.None,
            new Key( Namespace, SetName, "__verify_init__" ), new Bin( "init", 1 ) );

        try
        {
            var ctx = MakeContext();
            var generated = await GenerateAsync( ctx );

            // Defense-in-depth: confirm the squash captured the structural
            // objects we created. If this fails, the strategy isn't producing
            // a meaningful squash and the verifier test below is testing
            // nothing useful.
            StringAssert.Contains( generated.Content, "idx_email_verify" );
            StringAssert.Contains( generated.Content, "idx_age_verify" );

            var verifier = new AerospikeSquashVerifier( new AerospikeSnapshotCanonicalizer() )
            {
                // CaptureFromGenerated: wipe the namespace, apply the generated
                // content via parsed statements, capture the post-apply state.
                // This is the production "apply-AQL-then-Info.Request" path.
                CaptureFromGeneratedAsync = async ( content, _, ct ) =>
                {
                    await WipeNamespaceAsync( ct );
                    await ApplyGeneratedAsync( content, ct );
                    var blob = await AerospikeSnapshotCapture.CaptureAsync( _client, Namespace, ct );
                    return new SnapshotCaptureResult( blob );
                }
            };

            var result = await verifier.VerifyAsync( ctx, generated );

            if ( result is VerificationResult.Failed failed )
                Assert.Fail( $"Verification failed: {failed.Detail}\n{failed.DiffSummary}" );

            Assert.IsInstanceOfType<VerificationResult.Success>( result );
        }
        finally
        {
            await WipeNamespaceAsync( CancellationToken.None );
        }
    }

    // ---- helpers ------------------------------------------------------------

    private AerospikeSquashGenerationContext MakeContext() => new(
        squashName: "Squash_2000",
        squashVersion: 2000,
        client: _client,
        @namespace: Namespace,
        captureSnapshotAsync: async ( _, ct ) =>
        {
            var blob = await AerospikeSnapshotCapture.CaptureAsync( _client, Namespace, ct );
            return new SnapshotCaptureResult( blob );
        } );

    private async Task<SquashGenerationResult.Generated> GenerateAsync( AerospikeSquashGenerationContext ctx )
    {
        var descriptors = new[]
        {
            new MigrationDescriptor( typeof( object ), new MigrationAttribute( 1000 ), Array.Empty<long>() ),
            new MigrationDescriptor( typeof( object ), new MigrationAttribute( 2000 ), Array.Empty<long>() ),
        };

        var strategy = new InfoSnapshotStrategy(
            new AerospikeSnapshotCanonicalizer(),
            new AerospikeDataOpClassifier() );

        var result = await strategy.GenerateAsync( ctx, descriptors, new SquashGenerationOptions() );

        if ( result is SquashGenerationResult.Failed failed )
            Assert.Fail( $"GenerateAsync returned Failed: {failed.Detail}" );

        return (SquashGenerationResult.Generated) result;
    }

    private async Task ApplyGeneratedAsync( string content, CancellationToken ct )
    {
        // Use the parser's ParseScript which routes through ScriptStatementSplitter
        // (the shared core-lib splitter that strips `--` line comments per ADR-0022).
        var parser = new AerospikeStatementParser();

        foreach ( var item in parser.ParseScript( content ) )
        {
            switch ( item.StatementType )
            {
                case AerospikeStatementType.CreateSet:
                    // Aerospike sets are implicit; sentinel record materializes them.
                    await _client.Put(
                        new WritePolicy { expiration = 0 },
                        ct,
                        new Key( item.Namespace, item.SetName, "__set_init__" ),
                        new Bin( "init", 1 ) ).ConfigureAwait( false );
                    break;

                case AerospikeStatementType.CreateIndex:
                    var indexType = item.IndexType switch
                    {
                        AerospikeIndexType.Numeric => IndexType.NUMERIC,
                        AerospikeIndexType.Geo2DSphere => IndexType.GEO2DSPHERE,
                        _ => IndexType.STRING
                    };
                    await _client.CreateIndexAsync(
                        ns: item.Namespace,
                        setName: item.SetName,
                        indexName: item.IndexName,
                        binName: item.BinName,
                        indexType: indexType,
                        mode: IndexCreateMode.Missing,
                        waitReady: true,
                        cancellationToken: ct ).ConfigureAwait( false );
                    break;

                // Insert / Delete / DropIndex statements are not emitted by the
                // v1 canonicalizer; skip silently if they ever appear.
            }
        }
    }

    private async Task WipeNamespaceAsync( CancellationToken ct )
    {
        // Drop all indexes in the canonical set; delete sentinel records.
        // Best-effort; tolerate AerospikeException on already-dropped paths.
        var indexes = new[] { "idx_email_verify", "idx_age_verify" };
        foreach ( var idx in indexes )
        {
            try { await _client.DropIndexAsync( Namespace, SetName, idx ); } catch { }
        }

        try { await _client.Delete( null, ct, new Key( Namespace, SetName, "__verify_init__" ) ).ConfigureAwait( false ); } catch { }
        try { await _client.Delete( null, ct, new Key( Namespace, SetName, "__set_init__" ) ).ConfigureAwait( false ); } catch { }
    }
}

#endif
