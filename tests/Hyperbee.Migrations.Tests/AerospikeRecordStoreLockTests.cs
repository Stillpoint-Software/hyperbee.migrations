using Aerospike.Client;
using Hyperbee.Migrations;
using Hyperbee.Migrations.Providers.Aerospike;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using NSubstitute.ExceptionExtensions;

[TestClass]
public class AerospikeRecordStoreLockTests
{
    // Tests for the auto-renew locking behavior added to AerospikeRecordStore.
    //
    // Strategy:
    //  - Inject a FakeTimeProvider so the renewal loop's Task.Delay is virtual time.
    //  - Substitute IAsyncClient with NSubstitute and assert on Put/Touch/Delete calls.
    //  - To synchronize on the renewal loop's async progress, gate each renewal Touch
    //    on a TaskCompletionSource the test awaits.

    private static AerospikeMigrationOptions Options(
        TimeSpan? expire = null, TimeSpan? renew = null, TimeSpan? maxLifetime = null )
    {
        return new AerospikeMigrationOptions
        {
            Namespace = "test",
            MigrationSet = "SchemaMigrations",
            LockName = "migration_lock",
            LockExpireInterval = expire ?? TimeSpan.FromSeconds( 60 ),
            LockRenewInterval = renew ?? TimeSpan.FromSeconds( 30 ),
            LockMaxLifetime = maxLifetime ?? TimeSpan.FromMinutes( 10 )
        };
    }

    private static AerospikeRecordStore CreateStore(
        IAsyncClient client, AerospikeMigrationOptions options, TimeProvider timeProvider )
    {
        return new AerospikeRecordStore( client, options, timeProvider, NullLogger<AerospikeRecordStore>.Instance );
    }

    [TestMethod]
    public async Task CreateLockAsync_acquires_with_short_ttl()
    {
        var client = Substitute.For<IAsyncClient>();
        var time = new FakeTimeProvider( DateTimeOffset.UtcNow );
        var store = CreateStore( client, Options( expire: TimeSpan.FromSeconds( 60 ) ), time );

        using var handle = await store.CreateLockAsync();

        await client.Received( 1 ).Put(
            Arg.Is<WritePolicy>( p =>
                p.recordExistsAction == RecordExistsAction.CREATE_ONLY &&
                p.expiration == 60 ),
            Arg.Any<CancellationToken>(),
            Arg.Is<Key>( k => k.ns == "test" && k.setName == "SchemaMigrations" && k.userKey.ToString() == "migration_lock" ),
            Arg.Any<Bin[]>() );
    }

    [TestMethod]
    public async Task CreateLockAsync_throws_unavailable_when_key_exists()
    {
        var client = Substitute.For<IAsyncClient>();
        client
            .Put( Arg.Any<WritePolicy>(), Arg.Any<CancellationToken>(), Arg.Any<Key>(), Arg.Any<Bin[]>() )
            .Throws( new AerospikeException( ResultCode.KEY_EXISTS_ERROR ) );

        var store = CreateStore( client, Options(), new FakeTimeProvider() );

        await Assert.ThrowsExactlyAsync<MigrationLockUnavailableException>(
            () => store.CreateLockAsync() );
    }

    [TestMethod]
    public async Task CreateLockAsync_validates_renew_shorter_than_expire()
    {
        var client = Substitute.For<IAsyncClient>();
        var bad = Options( expire: TimeSpan.FromSeconds( 30 ), renew: TimeSpan.FromSeconds( 30 ) );
        var store = CreateStore( client, bad, new FakeTimeProvider() );

        await Assert.ThrowsExactlyAsync<MigrationException>( () => store.CreateLockAsync() );
    }

    [TestMethod]
    public async Task RenewLoop_touches_lock_on_each_interval()
    {
        var client = Substitute.For<IAsyncClient>();

        // Gate each Touch on a TCS so we can synchronize with the loop deterministically.
        var touch1 = new TaskCompletionSource();
        var touch2 = new TaskCompletionSource();
        var touchCount = 0;

        client
            .Touch( Arg.Any<WritePolicy>(), Arg.Any<CancellationToken>(), Arg.Any<Key>() )
            .Returns( _ =>
            {
                var n = Interlocked.Increment( ref touchCount );
                if ( n == 1 ) touch1.TrySetResult();
                else if ( n == 2 ) touch2.TrySetResult();
                return Task.CompletedTask;
            } );

        var time = new FakeTimeProvider( DateTimeOffset.UtcNow );
        var store = CreateStore( client,
            Options( expire: TimeSpan.FromSeconds( 60 ), renew: TimeSpan.FromSeconds( 30 ), maxLifetime: TimeSpan.FromHours( 1 ) ),
            time );

        using var handle = await store.CreateLockAsync();

        // First renewal interval
        time.Advance( TimeSpan.FromSeconds( 30 ) );
        await touch1.Task.WaitAsync( TimeSpan.FromSeconds( 5 ) );

        // Second renewal interval
        time.Advance( TimeSpan.FromSeconds( 30 ) );
        await touch2.Task.WaitAsync( TimeSpan.FromSeconds( 5 ) );

        Assert.AreEqual( 2, touchCount );

        await client.Received( 2 ).Touch(
            Arg.Is<WritePolicy>( p => p.expiration == 60 ),
            Arg.Any<CancellationToken>(),
            Arg.Is<Key>( k => k.userKey.ToString() == "migration_lock" ) );
    }

    [TestMethod]
    public async Task RenewLoop_stops_when_max_lifetime_reached()
    {
        var client = Substitute.For<IAsyncClient>();

        var touched = new TaskCompletionSource();
        client
            .Touch( Arg.Any<WritePolicy>(), Arg.Any<CancellationToken>(), Arg.Any<Key>() )
            .Returns( _ =>
            {
                touched.TrySetResult();
                return Task.CompletedTask;
            } );

        var time = new FakeTimeProvider( DateTimeOffset.UtcNow );
        var store = CreateStore( client,
            Options( expire: TimeSpan.FromSeconds( 60 ), renew: TimeSpan.FromSeconds( 30 ), maxLifetime: TimeSpan.FromMinutes( 1 ) ),
            time );

        using var handle = await store.CreateLockAsync();

        // First renewal still inside the window
        time.Advance( TimeSpan.FromSeconds( 30 ) );
        await touched.Task.WaitAsync( TimeSpan.FromSeconds( 5 ) );
        Assert.AreEqual( 1, TouchCallCount( client ) );

        // Advance past LockMaxLifetime — next iteration should *not* call Touch.
        // Loop wakes, sees deadline exceeded, returns.
        time.Advance( TimeSpan.FromSeconds( 30 ) ); // total 60s — at deadline
        time.Advance( TimeSpan.FromSeconds( 30 ) ); // wake the next interval; should bail without touching

        // Give the loop a chance to run to completion.
        for ( var i = 0; i < 10; i++ ) await Task.Yield();

        Assert.AreEqual( 1, TouchCallCount( client ) );
    }

    private static int TouchCallCount( IAsyncClient client ) =>
        client.ReceivedCalls().Count( c => c.GetMethodInfo().Name == "Touch" );

    [TestMethod]
    public async Task Dispose_cancels_renewal_and_deletes_lock()
    {
        var client = Substitute.For<IAsyncClient>();
        var time = new FakeTimeProvider( DateTimeOffset.UtcNow );
        var store = CreateStore( client, Options(), time );

        var handle = await store.CreateLockAsync();

        var beforeDispose = TouchCallCount( client );

        handle.Dispose();

        // After dispose, advancing time should not produce more Touch calls.
        time.Advance( TimeSpan.FromMinutes( 5 ) );
        for ( var i = 0; i < 10; i++ ) await Task.Yield();

        Assert.AreEqual( beforeDispose, TouchCallCount( client ) );

        await client.Received( 1 ).Delete(
            null,
            Arg.Any<CancellationToken>(),
            Arg.Is<Key>( k => k.userKey.ToString() == "migration_lock" ) );
    }

    [TestMethod]
    public async Task Dispose_is_idempotent()
    {
        var client = Substitute.For<IAsyncClient>();
        var store = CreateStore( client, Options(), new FakeTimeProvider() );

        var handle = await store.CreateLockAsync();
        handle.Dispose();
        handle.Dispose(); // second dispose must be a no-op

        await client.Received( 1 ).Delete(
            null, Arg.Any<CancellationToken>(), Arg.Any<Key>() );
    }

    [TestMethod]
    public async Task RenewLoop_stops_on_key_not_found()
    {
        // If the lock record is gone (TTL already lapsed), keep-renewing-anyway is unsafe —
        // another runner may have acquired the lock. The loop must stop and log.

        var client = Substitute.For<IAsyncClient>();

        var firstTouch = new TaskCompletionSource();
        client
            .Touch( Arg.Any<WritePolicy>(), Arg.Any<CancellationToken>(), Arg.Any<Key>() )
            .Returns( _ =>
            {
                firstTouch.TrySetResult();
                throw new AerospikeException( ResultCode.KEY_NOT_FOUND_ERROR );
            } );

        var time = new FakeTimeProvider( DateTimeOffset.UtcNow );
        var store = CreateStore( client, Options( expire: TimeSpan.FromSeconds( 60 ), renew: TimeSpan.FromSeconds( 30 ) ), time );

        using var handle = await store.CreateLockAsync();

        time.Advance( TimeSpan.FromSeconds( 30 ) );
        await firstTouch.Task.WaitAsync( TimeSpan.FromSeconds( 5 ) );

        // Subsequent intervals should NOT trigger more Touch calls.
        time.Advance( TimeSpan.FromSeconds( 30 ) );
        time.Advance( TimeSpan.FromSeconds( 30 ) );
        for ( var i = 0; i < 10; i++ ) await Task.Yield();

        Assert.AreEqual( 1, TouchCallCount( client ) );
    }

    [TestMethod]
    public async Task RenewLoop_retries_on_transient_error()
    {
        // A transient (non-KEY_NOT_FOUND) failure should not stop renewal — the lock
        // TTL gives us a buffer to retry on the next interval.

        var client = Substitute.For<IAsyncClient>();

        var touchCount = 0;
        var touch1 = new TaskCompletionSource();
        var touch2 = new TaskCompletionSource();

        client
            .Touch( Arg.Any<WritePolicy>(), Arg.Any<CancellationToken>(), Arg.Any<Key>() )
            .Returns( _ =>
            {
                var n = Interlocked.Increment( ref touchCount );
                if ( n == 1 )
                {
                    touch1.TrySetResult();
                    throw new AerospikeException( ResultCode.TIMEOUT );
                }
                touch2.TrySetResult();
                return Task.CompletedTask;
            } );

        var time = new FakeTimeProvider( DateTimeOffset.UtcNow );
        var store = CreateStore( client, Options( expire: TimeSpan.FromSeconds( 60 ), renew: TimeSpan.FromSeconds( 30 ) ), time );

        using var handle = await store.CreateLockAsync();

        time.Advance( TimeSpan.FromSeconds( 30 ) );
        await touch1.Task.WaitAsync( TimeSpan.FromSeconds( 5 ) );

        time.Advance( TimeSpan.FromSeconds( 30 ) );
        await touch2.Task.WaitAsync( TimeSpan.FromSeconds( 5 ) );

        Assert.AreEqual( 2, touchCount );
    }

    [TestMethod]
    public async Task WriteAsync_uses_time_provider_for_executed_at()
    {
        var client = Substitute.For<IAsyncClient>();
        var fixedNow = new DateTimeOffset( 2026, 1, 15, 12, 0, 0, TimeSpan.Zero );
        var time = new FakeTimeProvider( fixedNow );
        var store = CreateStore( client, Options(), time );

        await store.WriteAsync( "Record.1000.SeedData" );

        await client.Received( 1 ).Put(
            null,
            Arg.Any<CancellationToken>(),
            Arg.Is<Key>( k => k.userKey.ToString() == "Record.1000.SeedData" ),
            Arg.Is<Bin[]>( b => b.Any( x => x.name == "ExecutedAt" && (long) x.value.Object == fixedNow.ToUnixTimeSeconds() ) ) );
    }
}
