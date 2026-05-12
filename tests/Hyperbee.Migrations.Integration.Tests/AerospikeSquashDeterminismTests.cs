//#define INTEGRATIONS
using Aerospike.Client;
using Hyperbee.Migrations;
using Hyperbee.Migrations.Integration.Tests.Container.Aerospike;
using Hyperbee.Migrations.Providers.Aerospike.Extensions;
using Hyperbee.Migrations.Providers.Aerospike.Squash;
using Hyperbee.Migrations.Squash;

namespace Hyperbee.Migrations.Integration.Tests;

#if INTEGRATIONS

// Phase 1 (R-P5): Aerospike squash codegen determinism gate (C12).
//
// Contract: given identical cluster state, two GenerateAsync calls must
// produce byte-equal SquashGenerationResult.Generated.Content. This
// guarantees the canonicalizer fully removes ephemeral fields (objects
// counts, memory_used, state, etc.) and that Info.Request orderings are
// either inherently stable or normalized away.
//
// These tests exercise the FULL strategy pipeline -- topology capture +
// snapshot capture + canonicalize + statement classification + emit --
// against a live Aerospike Testcontainers fixture. Tests are guarded by
// `#if INTEGRATIONS`; run locally with /p:EnableIntegrationTests=true.

[TestClass]
[DoNotParallelize]
public class AerospikeSquashDeterminismTests
{
    private IAsyncClient _client;
    private const string Namespace = "test";

    [TestInitialize]
    public void Setup()
    {
        _client = AerospikeTestContainer.AsyncClient;
        Assert.IsNotNull( _client, "AerospikeTestContainer must initialize before this test class runs." );
    }

    [TestMethod]
    public async Task EmptyNamespace_TwoRuns_ProduceByteEqualContent()
    {
        var content1 = await GenerateOnceAsync();
        var content2 = await GenerateOnceAsync();

        Assert.AreEqual( content1, content2, "Two GenerateAsync runs against an empty namespace must produce byte-equal output." );
    }

    [TestMethod]
    public async Task PopulatedNamespace_TwoRuns_ProduceByteEqualContent()
    {
        // Create structural state directly via the client. Indexes are
        // deterministically named/ordered by the canonicalizer regardless of
        // the order they were created in.
        await _client.CreateIndexAsync( Namespace, "users", "idx_email_det", "email", IndexType.STRING );
        await _client.CreateIndexAsync( Namespace, "users", "idx_age_det", "age", IndexType.NUMERIC );

        try
        {
            // Write a sentinel record so the `users` set is reported by sets/<ns>.
            await _client.Put(
                new WritePolicy { expiration = 0 },
                CancellationToken.None,
                new Key( Namespace, "users", "det-probe" ),
                new Bin( "ready", 1 ) );

            var content1 = await GenerateOnceAsync();
            var content2 = await GenerateOnceAsync();

            Assert.AreEqual( content1, content2,
                "Two GenerateAsync runs against the same populated state must produce byte-equal output." );

            // Defense-in-depth: ensure the populated content actually contains
            // the structural objects we created (so we're testing real data, not
            // an empty-namespace pass-through).
            StringAssert.Contains( content1, "idx_age_det" );
            StringAssert.Contains( content1, "idx_email_det" );
        }
        finally
        {
            // Cleanup so other tests start from a clean structural state.
            try { await _client.DropIndexAsync( Namespace, "users", "idx_email_det" ); } catch { }
            try { await _client.DropIndexAsync( Namespace, "users", "idx_age_det" ); } catch { }
            try { await _client.Delete( null, CancellationToken.None, new Key( Namespace, "users", "det-probe" ) ); } catch { }
        }
    }

    [TestMethod]
    public async Task IndexCreationOrder_DoesNotAffectCanonicalOutput()
    {
        // Determinism implies: the canonicalizer sorts entries so the order
        // of creation does not influence the bytes. Create indexes in one
        // order, capture, then drop + recreate in the reverse order and
        // capture again. The two snapshots must be byte-equal.

        await _client.CreateIndexAsync( Namespace, "products", "idx_b", "b_field", IndexType.STRING );
        await _client.CreateIndexAsync( Namespace, "products", "idx_a", "a_field", IndexType.NUMERIC );
        await _client.Put( new WritePolicy { expiration = 0 }, CancellationToken.None,
            new Key( Namespace, "products", "order-probe" ), new Bin( "ready", 1 ) );

        try
        {
            var firstOrder = await GenerateOnceAsync();

            // Drop both and recreate in reverse order.
            await _client.DropIndexAsync( Namespace, "products", "idx_a" );
            await _client.DropIndexAsync( Namespace, "products", "idx_b" );

            await _client.CreateIndexAsync( Namespace, "products", "idx_a", "a_field", IndexType.NUMERIC );
            await _client.CreateIndexAsync( Namespace, "products", "idx_b", "b_field", IndexType.STRING );

            var secondOrder = await GenerateOnceAsync();

            Assert.AreEqual( firstOrder, secondOrder,
                "Index creation order must not affect canonical output bytes." );
        }
        finally
        {
            try { await _client.DropIndexAsync( Namespace, "products", "idx_a" ); } catch { }
            try { await _client.DropIndexAsync( Namespace, "products", "idx_b" ); } catch { }
            try { await _client.Delete( null, CancellationToken.None, new Key( Namespace, "products", "order-probe" ) ); } catch { }
        }
    }

    // ---- helpers -----------------------------------------------------------

    private async Task<string> GenerateOnceAsync()
    {
        var ctx = new AerospikeSquashGenerationContext(
            squashName: "Squash_2000",
            squashVersion: 2000,
            client: _client,
            @namespace: Namespace,
            captureSnapshotAsync: async ( _, ct ) =>
            {
                var blob = await AerospikeSnapshotCapture.CaptureAsync( _client, Namespace, ct );
                return new SnapshotCaptureResult( blob );
            } );

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

        var generated = (SquashGenerationResult.Generated) result;
        return generated.Content;
    }
}

#endif
