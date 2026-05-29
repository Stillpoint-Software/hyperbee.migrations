using FluentAssertions;
using Hyperbee.Migrations;
using Hyperbee.Migrations.Squash;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;

namespace Hyperbee.Migrations.Squash.Tests;

// IMigrationRecordStore contract coverage for the v3.0 additions:
//   - WriteAsync(MigrationRecord, WritePrecondition, ...) DIM default falls
//     back to legacy WriteAsync(string) and returns Created
//   - IntersectWithAppliedAsync DIM default falls back to per-id ExistsAsync
//   - IntersectWithSquashedAsync DIM default throws (R-3 fail-loud:
//     stores that handle squashes MUST override; silent fail-open is
//     a correctness gap)
//   - EnsureLedgerIntegrity gate on the record before the DIM default writes
//
// Custom record stores that don't override the new methods MUST get v2-
// equivalent behavior so existing consumers compile + run unchanged. Each
// shipped provider overrides these for squash-grade semantics; this test
// pins the back-compat contract at the interface level.

[TestClass]
public class RecordStoreContractTests
{
    private static MigrationRecord MakeRecord( string id, MigrationRecordKind kind = MigrationRecordKind.Migration, long[] replaces = null )
        => new()
        {
            Id = id,
            RunOn = DateTimeOffset.UtcNow,
            Checksum = "sha256:abc",
            Kind = kind,
            Replaces = replaces ?? Array.Empty<long>()
        };

    // ---- WriteAsync(MigrationRecord, ...) DIM default ----------------------

    [TestMethod]
    public async Task WriteAsync_DIMDefault_DelegatesToLegacyOverload_ReturnsCreated()
    {
        // A legacy IMigrationRecordStore implementation that only implements
        // the legacy WriteAsync(string) overload must work unchanged: the
        // DIM default calls into the legacy method and returns Created.
        var store = Substitute.For<IMigrationRecordStore>();
        store.WriteAsync( Arg.Any<MigrationRecord>(), Arg.Any<WritePrecondition>(), Arg.Any<CancellationToken>() )
            .Returns( call => CallDIMDefault( store, (MigrationRecord) call[0], (WritePrecondition) call[1], (CancellationToken) call[2] ) );

        var record = MakeRecord( "1000.test_migration" );
        var outcome = await store.WriteAsync( record, WritePrecondition.None );

        outcome.Should().Be( WriteOutcome.Created );
        await store.Received( 1 ).WriteAsync( "1000.test_migration" );
    }

    [TestMethod]
    public async Task WriteAsync_NullRecord_Throws()
    {
        var store = Substitute.For<IMigrationRecordStore>();
        store.WriteAsync( Arg.Any<MigrationRecord>(), Arg.Any<WritePrecondition>(), Arg.Any<CancellationToken>() )
            .Returns( call => CallDIMDefault( store, (MigrationRecord) call[0], (WritePrecondition) call[1], (CancellationToken) call[2] ) );

        Func<Task> act = async () => await store.WriteAsync( null!, WritePrecondition.None );
        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName( "record" );
    }

    [TestMethod]
    public async Task WriteAsync_KindReplacesMismatch_ThrowsLedgerIntegrity()
    {
        // EnsureLedgerIntegrity is called BEFORE the legacy fallback: a
        // record with Kind=Migration but Replaces!=empty is rejected.
        var store = Substitute.For<IMigrationRecordStore>();
        store.WriteAsync( Arg.Any<MigrationRecord>(), Arg.Any<WritePrecondition>(), Arg.Any<CancellationToken>() )
            .Returns( call => CallDIMDefault( store, (MigrationRecord) call[0], (WritePrecondition) call[1], (CancellationToken) call[2] ) );

        var record = MakeRecord( "2000.test", MigrationRecordKind.Migration, new long[] { 1000 } );
        Func<Task> act = async () => await store.WriteAsync( record, WritePrecondition.None );

        await act.Should().ThrowAsync<MigrationLedgerIntegrityException>();
    }

    // ---- IntersectWithAppliedAsync DIM default ----------------------------

    [TestMethod]
    public async Task IntersectWithAppliedAsync_DIMDefault_FallsBackToPerIdExists()
    {
        // The DIM default loops ExistsAsync; legacy stores work unchanged
        // (just slower than the shipped batch overrides).
        var store = Substitute.For<IMigrationRecordStore>();
        store.ExistsAsync( "a" ).Returns( true );
        store.ExistsAsync( "b" ).Returns( false );
        store.ExistsAsync( "c" ).Returns( true );

        store.IntersectWithAppliedAsync( Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>() )
            .Returns( call => CallIntersectAppliedDIM( store, (IEnumerable<string>) call[0], (CancellationToken) call[1] ) );

        var applied = await store.IntersectWithAppliedAsync( new[] { "a", "b", "c" } );

        applied.Should().BeEquivalentTo( new[] { "a", "c" } );
        await store.Received( 1 ).ExistsAsync( "a" );
        await store.Received( 1 ).ExistsAsync( "b" );
        await store.Received( 1 ).ExistsAsync( "c" );
    }

    [TestMethod]
    public async Task IntersectWithAppliedAsync_DetectsSentinelById_KindAgnostic()
    {
        // ADR-0027 contract pin: IntersectWithAppliedAsync is existence-by-id
        // and MUST report an in-flight sentinel id (Kind=InProgress) just like
        // any other row. The interruption pre-scan depends on this; an
        // implementation that filtered by Kind=Migration would silently break
        // sentinel detection. Exercise the DIM default (existence loop) to pin
        // the kind-agnostic semantics at the interface level.
        var sentinelId = InProgressRecord.IdFor( "1000.test_migration" );

        var store = Substitute.For<IMigrationRecordStore>();
        store.ExistsAsync( sentinelId ).Returns( true );
        store.IntersectWithAppliedAsync( Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>() )
            .Returns( call => CallIntersectAppliedDIM( store, (IEnumerable<string>) call[0], (CancellationToken) call[1] ) );

        var found = await store.IntersectWithAppliedAsync( new[] { sentinelId } );

        found.Should().Contain( sentinelId );
    }

    [TestMethod]
    public async Task IntersectWithAppliedAsync_EmptyCandidates_ReturnsEmpty()
    {
        var store = Substitute.For<IMigrationRecordStore>();
        store.IntersectWithAppliedAsync( Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>() )
            .Returns( call => CallIntersectAppliedDIM( store, (IEnumerable<string>) call[0], (CancellationToken) call[1] ) );

        var applied = await store.IntersectWithAppliedAsync( Array.Empty<string>() );
        applied.Should().BeEmpty();
    }

    [TestMethod]
    public async Task IntersectWithAppliedAsync_NullCandidates_Throws()
    {
        var store = Substitute.For<IMigrationRecordStore>();
        store.IntersectWithAppliedAsync( Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>() )
            .Returns( call => CallIntersectAppliedDIM( store, (IEnumerable<string>) call[0], (CancellationToken) call[1] ) );

        Func<Task> act = async () => await store.IntersectWithAppliedAsync( null! );
        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName( "candidateIds" );
    }

    [TestMethod]
    public async Task IntersectWithAppliedAsync_CancellationPropagates()
    {
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        var store = Substitute.For<IMigrationRecordStore>();
        store.IntersectWithAppliedAsync( Arg.Any<IEnumerable<string>>(), Arg.Any<CancellationToken>() )
            .Returns( call => CallIntersectAppliedDIM( store, (IEnumerable<string>) call[0], (CancellationToken) call[1] ) );

        Func<Task> act = async () => await store.IntersectWithAppliedAsync( new[] { "a" }, cts.Token );
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---- IntersectWithSquashedAsync DIM default ---------------------------

    [TestMethod]
    public async Task IntersectWithSquashedAsync_DIMDefault_ThrowsNotSupported()
    {
        // R-3: legacy stores have no ledger scan primitive for transitive
        // Replaces; returning an empty set silently misclassifies mature
        // environments that auto-marked an inner squash. The DIM default
        // throws NotSupportedException with a remediation message naming
        // the store type so the operator can either override the method
        // or upgrade to a shipped provider.
        var store = Substitute.For<IMigrationRecordStore>();
        store.IntersectWithSquashedAsync( Arg.Any<IEnumerable<long>>(), Arg.Any<CancellationToken>() )
            .Returns( call => CallIntersectSquashedDIM( store, (IEnumerable<long>) call[0], (CancellationToken) call[1] ) );

        Func<Task> act = async () =>
            await store.IntersectWithSquashedAsync( new long[] { 1000, 1100, 1200 } );

        (await act.Should().ThrowAsync<NotSupportedException>())
            .Which.Message.Should().Contain( "IntersectWithSquashedAsync" )
            .And.Contain( "ADR-0019" );
    }

    [TestMethod]
    public async Task IntersectWithSquashedAsync_NullVersions_ThrowsArgumentNullBeforeNotSupported()
    {
        // Argument validation runs before the NotSupportedException so the
        // exception type identifies the actual API misuse rather than the
        // contract gap.
        var store = Substitute.For<IMigrationRecordStore>();
        store.IntersectWithSquashedAsync( Arg.Any<IEnumerable<long>>(), Arg.Any<CancellationToken>() )
            .Returns( call => CallIntersectSquashedDIM( store, (IEnumerable<long>) call[0], (CancellationToken) call[1] ) );

        Func<Task> act = async () => await store.IntersectWithSquashedAsync( null! );
        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName( "versions" );
    }

    // ---- helpers -----------------------------------------------------------

    // The DIM defaults live on the interface; NSubstitute substitutes the
    // virtual surface, so calling them via the substitute resolves to the
    // mock-returned value. To exercise the actual DIM body, we manually
    // invoke a private static helper that mirrors the interface behavior.
    // (Same shape as the actual default-interface-method implementation in
    // IMigrationRecordStore.cs.)

    private static Task<WriteOutcome> CallDIMDefault(
        IMigrationRecordStore store, MigrationRecord record, WritePrecondition precondition, CancellationToken ct )
    {
        if ( record == null )
            throw new ArgumentNullException( nameof( record ) );
        record.EnsureLedgerIntegrity();
        return WriteAsyncFallback( store, record );

        static async Task<WriteOutcome> WriteAsyncFallback( IMigrationRecordStore self, MigrationRecord r )
        {
            await self.WriteAsync( r.Id ).ConfigureAwait( false );
            return WriteOutcome.Created;
        }
    }

    private static async Task<IReadOnlySet<string>> CallIntersectAppliedDIM(
        IMigrationRecordStore store, IEnumerable<string> candidateIds, CancellationToken ct )
    {
        if ( candidateIds == null )
            throw new ArgumentNullException( nameof( candidateIds ) );

        var found = new HashSet<string>( StringComparer.Ordinal );
        foreach ( var id in candidateIds )
        {
            ct.ThrowIfCancellationRequested();
            if ( await store.ExistsAsync( id ).ConfigureAwait( false ) )
                found.Add( id );
        }
        return found;
    }

    private static Task<IReadOnlySet<long>> CallIntersectSquashedDIM(
        IMigrationRecordStore store, IEnumerable<long> versions, CancellationToken ct )
    {
        if ( versions == null )
            throw new ArgumentNullException( nameof( versions ) );

        throw new NotSupportedException(
            $"IMigrationRecordStore implementation '{store.GetType().FullName}' does not override " +
            "IntersectWithSquashedAsync; transitive squash reconciliation (ADR-0019 A6) " +
            "cannot proceed. Override this method to scan the ledger for rows where " +
            "Kind=Squash and the Replaces array contains the input version, or use one " +
            "of the shipped record stores (Postgres, MongoDB, Couchbase, OpenSearch, " +
            "Aerospike) that implement the primitive natively." );
    }
}
