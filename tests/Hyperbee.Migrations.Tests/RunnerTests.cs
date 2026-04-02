using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;
using Hyperbee.Migrations.Helper;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Time.Testing;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;

// ReSharper disable UnusedMember.Global
// ReSharper disable InconsistentNaming

namespace Hyperbee.Migrations.Tests;

[TestClass]
public class RunnerTests
{
    private CancellationTokenSource _cancellationTokenSource;

    [TestInitialize]
    public void Setup()
    {
        _cancellationTokenSource = new CancellationTokenSource();
    }

    [TestMethod]
    public async Task Migrations_run_with_up_direction_in_order()
    {
        // arrange
        var store = new List<(string Id, MigrationRecord Record)>();
        var recordStore = InitializeStore( store );
        var options = GetMigrationOptions();

        var logger = Substitute.For<ILogger<MigrationRunner>>();
        var migrationRunner = new MigrationRunner( recordStore, options, logger );

        // act
        await migrationRunner.RunAsync();

        // assert
        Assert.HasCount( 5, store );
        Assert.AreEqual( "record.1.first-migration", store.First().Id );
        Assert.AreEqual( "record.2.second-migration", store.Skip( 1 ).First().Id );
        Assert.AreEqual( "record.7.cron-delay-no-stop-migration", store.Skip( 2 ).First().Id );
        Assert.AreEqual( "record.8.cron-delay-with-stop-migration", store.Skip( 3 ).First().Id );
        Assert.AreEqual( "record.9.stop-migration", store.Skip( 4 ).First().Id );

    }

    [TestMethod]
    public async Task Migrations_run_with_up_direction_to_version()
    {
        // arrange
        var store = new List<(string Id, MigrationRecord Record)>();
        var recordStore = InitializeStore( store );
        var options = GetMigrationOptions();
        options.ToVersion = 1;

        var logger = Substitute.For<ILogger<MigrationRunner>>();
        var migrationRunner = new MigrationRunner( recordStore, options, logger );

        // act
        await migrationRunner.RunAsync();

        // assert
        Assert.HasCount( 1, store );
        Assert.AreEqual( "record.1.first-migration", store.First().Id );
    }

    [TestMethod]
    public async Task Migrations_run_with_down_direction()
    {
        // arrange
        var store = new List<(string Id, MigrationRecord Record)>
        {
            new("record.1.first-migration", new MigrationRecord()),
            new("record.2.second-migration", new MigrationRecord()),
        };
        var recordStore = InitializeStore( store );
        var options = GetMigrationOptions();
        options.Direction = Direction.Down;

        var logger = Substitute.For<ILogger<MigrationRunner>>();

        var migrationRunner = new MigrationRunner( recordStore, options, logger );

        // act
        await migrationRunner.RunAsync();


        // assert
        Assert.IsEmpty( store );
    }

    [TestMethod]
    public async Task Migrations_run_with_down_direction_to_version()
    {
        // arrange
        var store = new List<(string Id, MigrationRecord Record)>
        {
        new("record.1.first-migration", new MigrationRecord()),
        new("record.2.second-migration", new MigrationRecord()),
        };
        var recordStore = InitializeStore( store );
        var options = GetMigrationOptions();
        options.Direction = Direction.Down;
        options.ToVersion = 2;

        var logger = Substitute.For<ILogger<MigrationRunner>>();
        var migrationRunner = new MigrationRunner( recordStore, options, logger );

        // act
        await migrationRunner.RunAsync();

        // assert
        Assert.HasCount( 1, store );
        Assert.AreEqual( "record.1.first-migration", store.First().Id );
    }

    [TestMethod]
    public async Task Migrations_run_with_up_direction_in_development()
    {
        // arrange
        var store = new List<(string Id, MigrationRecord Record)>();
        var recordStore = InitializeStore( store );
        var options = GetMigrationOptions();
        options.Profiles.Add( "development" );

        var logger = Substitute.For<ILogger<MigrationRunner>>();
        var migrationRunner = new MigrationRunner( recordStore, options, logger );

        // act
        await migrationRunner.RunAsync();

        // assert
        Assert.HasCount( 6, store );
        Assert.AreEqual( "record.1.first-migration", store.First().Id );
        Assert.AreEqual( "record.2.second-migration", store.Skip( 1 ).First().Id );
        Assert.AreEqual( "record.3.development-migration", store.Skip( 2 ).First().Id );
        Assert.AreEqual( "record.7.cron-delay-no-stop-migration", store.Skip( 3 ).First().Id );
        Assert.AreEqual( "record.8.cron-delay-with-stop-migration", store.Skip( 4 ).First().Id );
        Assert.AreEqual( "record.9.stop-migration", store.Skip( 5 ).First().Id );
    }

    public async Task Migrations_run_with_up_direction_with_inheritance()
    {
        // arrange
        var store = new List<(string Id, MigrationRecord Record)>();
        var recordStore = InitializeStore( store );
        var options = GetMigrationOptions();
        options.Profiles.Add( "uses-BaseMigration" );
        var logger = Substitute.For<ILogger<MigrationRunner>>();
        var migrationRunner = new MigrationRunner( recordStore, options, logger );

        // act
        await migrationRunner.RunAsync();

        // assert
        Assert.HasCount( 3, store );
        Assert.AreEqual( "record.1.first-migration", store.First().Id );
        Assert.AreEqual( "record.2.second-migration", store.Skip( 1 ).First().Id );
        Assert.AreEqual( "record.4.subclass-of-basemigration", store.Skip( 2 ).First().Id );
        Assert.AreEqual( "record.7.cron-delay-no-stop-migration", store.Skip( 3 ).First().Id );
        Assert.AreEqual( "record.8.cron-delay-with-stop-migration", store.Skip( 4 ).First().Id );
        Assert.AreEqual( "record.9.stop-migration", store.Skip( 5 ).First().Id );
    }

    [TestMethod]
    public async Task Migrations_run_with_up_direction_with_complex_convention_names()
    {
        // arrange
        var store = new List<(string Id, MigrationRecord Record)>();
        var recordStore = InitializeStore( store );
        var options = GetMigrationOptions();
        options.Profiles.Add( "exclude-me" );
        var logger = Substitute.For<ILogger<MigrationRunner>>();

        var migrationRunner = new MigrationRunner( recordStore, options, logger );

        // act
        await migrationRunner.RunAsync();

        // assert
        Assert.HasCount( 6, store );
        Assert.AreEqual( "record.1.first-migration", store.First().Id );
        Assert.AreEqual( "record.2.second-migration", store.Skip( 1 ).First().Id );
        Assert.AreEqual( "record.5.has-problems-with-underscores", store.Skip( 2 ).First().Id );
        Assert.AreEqual( "record.7.cron-delay-no-stop-migration", store.Skip( 3 ).First().Id );
        Assert.AreEqual( "record.8.cron-delay-with-stop-migration", store.Skip( 4 ).First().Id );
        Assert.AreEqual( "record.9.stop-migration", store.Skip( 5 ).First().Id );
    }


    [TestMethod]
    public async Task Migration_cron_helper()
    {
        // arrange
        var _timeProvider = Substitute.For<FakeTimeProvider>();
        var helper = new MigrationCronHelper();

        // act
        var results = await helper.CronDelayAsync( "* * * * *" );

        // assert
        Assert.IsTrue( results );
    }


    private static MigrationOptions GetMigrationOptions()
    {
        var activator = Substitute.For<IMigrationActivator>();
        activator.CreateInstance( Arg.Any<Type>() ).Returns( args => Activator.CreateInstance( args.Arg<Type>() ) );

        var options = new MigrationOptions( activator );
        options.Assemblies.Add( Assembly.GetExecutingAssembly() );

        return options;
    }

    private static IMigrationRecordStore InitializeStore( ICollection<(string Id, MigrationRecord Record)> store )
    {
        var recordStore = Substitute.For<IMigrationRecordStore>();

        recordStore.InitializeAsync().Returns( Task.CompletedTask );
        recordStore.CreateLockAsync().Returns( Task.FromResult( new FakeLock() ) );

        recordStore.ExistsAsync( Arg.Any<string>() ).Returns( args => Task.FromResult( store.Any( x => x.Id == args.Arg<string>() ) ) );
        recordStore.DeleteAsync( Arg.Any<string>() ).Returns( args =>
        {
            var record = store.FirstOrDefault( x => x.Id == args.Arg<string>() );
            store.Remove( record );
            return Task.CompletedTask;
        } );
        recordStore.WriteAsync( Arg.Any<string>() ).Returns( args =>
        {
            var id = args.Arg<string>();
            store.Add( (args.Arg<string>(), new MigrationRecord { Id = id }) );
            return Task.CompletedTask;
        } );

        return recordStore;
    }

    public TimeProvider GetTestTimeProvider()
    {
        var fakeTimeProvider = new FakeTimeProvider();
        fakeTimeProvider.SetUtcNow( new DateTimeOffset( 2024, 1, 9, 1, 0, 0, TimeSpan.Zero ) );
        fakeTimeProvider.SetLocalTimeZone( TimeZoneInfo.Utc );

        return fakeTimeProvider;
    }

}

// test support

public class FakeLock : IDisposable
{
    public void Dispose()
    {
    }
}


[Migration( 1 )]
public class First_Migration : Migration
{
    public override Task UpAsync( CancellationToken cancellationToken = default ) => Task.CompletedTask;
    public override Task DownAsync( CancellationToken cancellationToken = default ) => Task.CompletedTask;
}

[Migration( 2 )]
public class Second_Migration : Migration
{
    public override Task UpAsync( CancellationToken cancellationToken = default ) => Task.CompletedTask;
    public override Task DownAsync( CancellationToken cancellationToken = default ) => Task.CompletedTask;
}

[Migration( 3, null, null, true, "development", "demo" )]
public class Development_Migration : Migration
{
    public override Task UpAsync( CancellationToken cancellationToken = default ) => Task.CompletedTask;
}

[Migration( 4, null, null, true, "uses-BaseMigration" )]
public class Subclass_of_BaseMigration : BaseMigration
{
    public override Task UpAsync( CancellationToken cancellationToken = default ) => Task.CompletedTask;
}

[Migration( 5, null, null, true, "exclude-me" )]
public class _has_problems__with_underscores___ : Migration
{
    public override Task UpAsync( CancellationToken cancellationToken = default ) => Task.CompletedTask;
}

[Migration( 6, journal: false )]
public class No_Jounal_Migration : Migration
{
    public override Task UpAsync( CancellationToken cancellationToken = default ) => Task.CompletedTask;
    public override Task DownAsync( CancellationToken cancellationToken = default ) => Task.CompletedTask;
}

[Migration( 7, nameof( StartMethod ), nameof( StopMethod ) )]
public class Cron_Delay_No_Stop_Migration : Migration
{
    private readonly FakeTimeProvider _fake = new( DateTimeOffset.UtcNow );
    private int count = 0;

    public override Task UpAsync( CancellationToken cancellationToken = default ) => Task.CompletedTask;
    public override Task DownAsync( CancellationToken cancellationToken = default ) => Task.CompletedTask;

    public async Task<bool> StartMethod()
    {
        await Task.Delay( TimeSpan.FromSeconds( 5 ) );
        return true;
    }

    public async Task<bool> StopMethod()
    {
        if ( count == 1 )
            return await Task.FromResult( true );

        count++;
        return await Task.FromResult( false );
    }
}

[Migration( 8, nameof( StartMethod ), nameof( StopMethod ), true )]
public class Cron_Delay_With_Stop_Migration : Migration
{
    public override Task UpAsync( CancellationToken cancellationToken = default ) => Task.CompletedTask;
    public override Task DownAsync( CancellationToken cancellationToken = default ) => Task.CompletedTask;
    public async Task<bool> StartMethod()
    {
        await Task.Delay( TimeSpan.FromSeconds( 10 ) );
        return await Task.FromResult( true );
    }

    public async Task<bool> StopMethod() { return await Task.FromResult( true ); }
}

[Migration( 9, null, nameof( StopMethod ), true )]
public class Stop_Migration : Migration
{
    public override Task UpAsync( CancellationToken cancellationToken = default ) => Task.CompletedTask;
    public override Task DownAsync( CancellationToken cancellationToken = default ) => Task.CompletedTask;
    public async Task<bool> StopMethod()
    {
        return await Task.FromResult( true );
    }
}

public abstract class BaseMigration : Migration
{
    public override Task UpAsync( CancellationToken cancellationToken = default ) => Task.CompletedTask;
}

