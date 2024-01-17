using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;
using System;
using System.Collections.Generic;
using System.Linq;
using System.Reflection;
using System.Threading;
using System.Threading.Tasks;

// ReSharper disable UnusedMember.Global
// ReSharper disable InconsistentNaming

namespace Hyperbee.Migrations.Tests;

[TestClass]
public class RunnerTests
{
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
        Assert.AreEqual( 2, store.Count );
        Assert.AreEqual( "record.1.first-migration", store.First().Id );
        Assert.AreEqual( "record.2.second-migration", store.Skip( 1 ).First().Id );
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
        Assert.AreEqual( 1, store.Count );
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
        Assert.AreEqual( 0, store.Count );
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
        Assert.AreEqual( 1, store.Count );
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
        Assert.AreEqual( 3, store.Count );
        Assert.AreEqual( "record.1.first-migration", store.First().Id );
        Assert.AreEqual( "record.2.second-migration", store.Skip( 1 ).First().Id );
        Assert.AreEqual( "record.3.development-migration", store.Skip( 2 ).First().Id );
    }

    [TestMethod]
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
        Assert.AreEqual( 3, store.Count );
        Assert.AreEqual( "record.1.first-migration", store.First().Id );
        Assert.AreEqual( "record.2.second-migration", store.Skip( 1 ).First().Id );
        Assert.AreEqual( "record.4.subclass-of-basemigration", store.Skip( 2 ).First().Id );
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
        Assert.AreEqual( 3, store.Count );
        Assert.AreEqual( "record.1.first-migration", store.First().Id );
        Assert.AreEqual( "record.2.second-migration", store.Skip( 1 ).First().Id );
        Assert.AreEqual( "record.5.has-problems-with-underscores", store.Skip( 2 ).First().Id );
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

[Migration( 3, "development", "demo" )]
public class Development_Migration : Migration
{
    public override Task UpAsync( CancellationToken cancellationToken = default ) => Task.CompletedTask;
}

[Migration( 4, "uses-BaseMigration" )]
public class Subclass_of_BaseMigration : BaseMigration
{
    public override Task UpAsync( CancellationToken cancellationToken = default ) => Task.CompletedTask;
}

[Migration( 5, "exclude-me" )]
public class _has_problems__with_underscores___ : Migration
{
    public override Task UpAsync( CancellationToken cancellationToken = default ) => Task.CompletedTask;
}

public abstract class BaseMigration : Migration
{
    public override Task UpAsync( CancellationToken cancellationToken = default ) => Task.CompletedTask;
}