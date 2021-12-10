using System.Collections.Generic;
using System.Reflection;
using System.Threading.Tasks;
using Hyperbee.Migrations.Tests.TestSupport;
using Microsoft.Extensions.Logging;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hyperbee.Migrations.Tests;

[TestClass]
public class MigrationTests
{
    private readonly ILogger _logger = new ConsoleLogger();
    private readonly List<Assembly> _assemblies = new() { typeof(MigrationTests).Assembly };

    [TestMethod]
    public void Can_migrate_up()
    {
        //using var store = GetDocumentStore();
        //var professorZoom = InitialiseWithPerson(store, "Professor", "Zoom");

        var migration = new AddFullName();
        migration.Setup( new MigrationOptions(), _logger);

        migration.Up();

        using var session = store.OpenSession();
        var loaded = session.Load<Person>(professorZoom.Id);
        loaded.FullName.Should().Be("Professor Zoom");
    }

    [TestMethod]
    public void Can_migrate_down()
    {
        using var store = GetDocumentStore();
        var ladyDeathstrike = InitialiseWithPerson(store, "Lady", "Deathstrike");

        var migration = new AddFullName();
        migration.Setup( new MigrationOptions(), _logger);

        migration.Up();
        //WaitForIndexing(store);

        migration.Down();
        WaitForIndexing(store);

        using var session = store.OpenSession();
        var loaded = session.Load<Person>(ladyDeathstrike.Id);
        loaded.FullName.Should().Be(null);
    }

    [TestMethod]
    public async Task Calling_run_in_parallel_runs_migrations_only_once()
    {
        using var documentStore = GetDocumentStore();
        await new TestDocumentIndex().ExecuteAsync(documentStore);

        var instanceOne = new MigrationRunner(documentStore, new MigrationOptions() { Assemblies = _assemblies }, new ConsoleLogger());
        var instanceTwo = new MigrationRunner(documentStore, new MigrationOptions() { Assemblies = _assemblies }, new ConsoleLogger());

        var first = Task.Run( async () => await instanceOne.RunAsync());
        var second = Task.Run( async () => await instanceTwo.RunAsync());

        await Task.WhenAll(first, second);

        //WaitForIndexing(documentStore);
        WaitForUserToContinueTheTest(documentStore);

        using var session = documentStore.OpenSession();
        var testDocCount = session.Query<TestDocument, TestDocumentIndex>().Count();
        testDocCount
            .Should()
            .Be(1);
    }

    private Person InitialiseWithPerson(IDocumentStore store, string firstName, string lastName)
    {
        using var session = store.OpenSession();
        var person = new Person { Id = "People/1", FirstName = firstName, LastName = lastName };
        session.Store(person);
        session.SaveChanges();
        return person;
    }

    private void InitialiseWithPeople(IDocumentStore store, List<Person> people)
    {
        using (var session = store.OpenSession())
        {
            people.ForEach(p => session.Store(p));
            session.SaveChanges();
        }
        //WaitForIndexing(store);
    }
}

[Migration(1, "alter")]
public class AddFullName : Migration
{
    public override void Up()
    {
        PatchCollection("from People update { this.FullName = this.FirstName + ' ' + this.LastName; }");
    }

    public override void Down()
    {
        PatchCollection("from People update { delete this.FullName; }");
    }
}

public class FooBaz
{
    public int Id { get; set; }
    public string Bar { get; set; }
}

public class Person
{
    public string Id { get; set; }
    public string FirstName { get; set; }
    public string LastName { get; set; }
    public string FullName { get; set; }
}