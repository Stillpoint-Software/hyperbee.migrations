//#define INTEGRATIONS
using Hyperbee.Migrations.Integration.Tests.Container.MongoDb;
using Hyperbee.Migrations.Providers.MongoDB;
using Microsoft.Extensions.Logging.Abstractions;

namespace Hyperbee.Migrations.Integration.Tests;

#if INTEGRATIONS
// Record-store-level integration tests against a real MongoDB, mirroring the
// shape of PostgresInterruptionSafetyIntegrationTests. The other MongoDB
// integration tests drive whole migration containers, which is the wrong
// granularity for asserting a single ledger query's behavior.
//
// IntersectWithSquashedAsync had no coverage at any tier, which is how a filter
// that could never match shipped in v3.0.0 and stayed silent through v3.1.0:
// it returns an empty set on failure, and an empty set is also the correct
// answer whenever nothing is squashed. See ADR-0029.

[TestClass]
// Gating (ADR-0031): one shared MongoDB container via the assembly fixture, no
// Docker image build, seconds to run. IntersectWithSquashedAsync is exactly the
// kind of silently-wrong query this tier exists to catch.
[TestCategory( "Gating" )]
public class MongoDBRecordStoreIntegrationTests
{
    private static MongoDBRecordStore BuildStore( MongoDBMigrationOptions options ) =>
        new( MongoDbTestContainer.Client, options, NullLogger<MongoDBRecordStore>.Instance );

    private static MongoDBMigrationOptions UniqueOptions( string testName ) => new()
    {
        DatabaseName = "migration",
        CollectionName = $"ledger-{testName.ToLowerInvariant()}-{Guid.NewGuid():n}"
    };

    private static Task CleanupAsync( MongoDBMigrationOptions options ) =>
        MongoDbTestContainer.Client
            .GetDatabase( options.DatabaseName )
            .DropCollectionAsync( options.CollectionName );

    [TestMethod]
    [TestCategory( "MongoDB" )]
    public async Task IntersectWithSquashed_ReturnsVersionsCoveredByASquashRow()
    {
        var options = UniqueOptions( nameof( IntersectWithSquashed_ReturnsVersionsCoveredByASquashRow ) );
        var store = BuildStore( options );

        try
        {
            await store.InitializeAsync();

            await store.WriteAsync( new MigrationRecord
            {
                Id = "2000.squash-alpha",
                Checksum = "sha256:squash",
                Kind = MigrationRecordKind.Squash,
                Replaces = [1000L, 1001L, 1002L]
            } );

            // 1003 is not covered by the squash; the other three are.
            var covered = await store.IntersectWithSquashedAsync( [1000L, 1002L, 1003L] );

            CollectionAssert.AreEquivalent( new[] { 1000L, 1002L }, covered.ToArray() );
        }
        finally
        {
            await CleanupAsync( options );
        }
    }

    [TestMethod]
    [TestCategory( "MongoDB" )]
    public async Task IntersectWithSquashed_IgnoresNonSquashRows()
    {
        // A plain migration row must never satisfy a version, even if a future
        // schema change gave it a Replaces array. The Kind predicate carries that.
        var options = UniqueOptions( nameof( IntersectWithSquashed_IgnoresNonSquashRows ) );
        var store = BuildStore( options );

        try
        {
            await store.InitializeAsync();
            await store.WriteAsync( "1000.plain-migration" );

            var covered = await store.IntersectWithSquashedAsync( [1000L] );

            Assert.IsEmpty( covered );
        }
        finally
        {
            await CleanupAsync( options );
        }
    }

    [TestMethod]
    [TestCategory( "MongoDB" )]
    public async Task IntersectWithApplied_ReturnsOnlyWrittenIds()
    {
        var options = UniqueOptions( nameof( IntersectWithApplied_ReturnsOnlyWrittenIds ) );
        var store = BuildStore( options );

        var written = new[] { "1000.applied-one", "1002.applied-two" };

        try
        {
            await store.InitializeAsync();

            foreach ( var id in written )
                await store.WriteAsync( id );

            var applied = await store.IntersectWithAppliedAsync(
                written.Concat( ["1001.never-run", "1003.never-run"] ) );

            CollectionAssert.AreEquivalent( written, applied.ToArray() );
        }
        finally
        {
            await CleanupAsync( options );
        }
    }
}
#endif
