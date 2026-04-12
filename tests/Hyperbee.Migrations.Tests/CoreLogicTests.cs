
using System;
using System.Threading;
using System.Threading.Tasks;
using Hyperbee.Migrations;
using Hyperbee.Migrations.Wait;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.VisualStudio.TestTools.UnitTesting;

[TestClass]
public class CoreLogicTests
{
    [TestMethod]
    public void DefaultMigrationActivator_Throws_OnNullServiceProvider()
    {
        try
        {
            new DefaultMigrationActivator( null );
            Assert.Fail( "Expected ArgumentNullException was not thrown." );
        }
        catch ( ArgumentNullException )
        {
            // Success
        }
    }

    [TestMethod]
    public void DefaultMigrationActivator_CreatesInstance()
    {
        var sp = new ServiceCollection().BuildServiceProvider();
        var activator = new DefaultMigrationActivator( sp );
        var instance = activator.CreateInstance( typeof( DummyMigration ) );
        Assert.IsInstanceOfType( instance, typeof( DummyMigration ) );
    }

    private class DummyMigration : Migration
    {
        public override Task UpAsync( CancellationToken cancellationToken = default ) => Task.CompletedTask;
    }

    [TestMethod]
    public async Task RetryStrategy_WaitsAndBacksOff()
    {
        var strategy = new BackoffRetryStrategy( TimeSpan.FromMilliseconds( 10 ), TimeSpan.FromMilliseconds( 40 ) );
        var delays = new System.Collections.Generic.List<TimeSpan>();
        // Use a local subclass to set WaitAction (init-only workaround)
        var localStrategy = new BackoffRetryStrategy( TimeSpan.FromMilliseconds( 10 ), TimeSpan.FromMilliseconds( 40 ) )
        {
            WaitAction = s => delays.Add( s.Delay )
        };
        await localStrategy.WaitAsync();
        await localStrategy.WaitAsync();
        await localStrategy.WaitAsync();
        Assert.IsGreaterThanOrEqualTo( 3, delays.Count );
        Assert.IsTrue( delays[1] > delays[0] );
    }

    [TestMethod]
    public async Task PauseRetryStrategy_Waits()
    {
        var strategy = new PauseRetryStrategy( TimeSpan.FromMilliseconds( 5 ) );
        var before = strategy.Delay;
        await strategy.WaitAsync();
        Assert.AreEqual( before, strategy.Delay );
    }

    [TestMethod]
    public async Task WaitHelper_WaitUntilAsync_Succeeds()
    {
        int count = 0;
        await WaitHelper.WaitUntilAsync( async ct => { count++; return count > 2; } );

        Assert.IsGreaterThan( 2, count );
    }

    [TestMethod]
    public async Task WaitHelper_WaitUntilAsync_TimesOut()
    {
        bool threw = false;
        try
        {
            await WaitHelper.WaitUntilAsync( async ct => false, TimeSpan.FromMilliseconds( 10 ) );
        }
        catch ( RetryTimeoutException )
        {
            threw = true;
        }
        Assert.IsTrue( threw, "Expected RetryTimeoutException was not thrown." );
    }

    [TestMethod]
    public async Task WaitHelper_WaitUntilAsync_ThrowsWrapped()
    {
        bool threw = false;
        try
        {
            await WaitHelper.WaitUntilAsync( ct => throw new InvalidOperationException( "fail" ), TimeSpan.FromMilliseconds( 50 ) );
        }
        catch ( RetryStrategyException )
        {
            threw = true;
        }
        Assert.IsTrue( threw, "Expected RetryStrategyException was not thrown." );
    }
}
