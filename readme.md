# Hyperbee Migrations

## Introduction

Hyperbee Migrations is a migration framework to help with common tasks you might have to do over time. 
The framework API is heavily influenced by [Fluent Migrator](https://github.com/schambers/fluentmigrator)
and [Raven Migrations](https://github.com/migrating-ravens/RavenMigrations).

## Philosophy

We believe any changes to your domain should be visible in your code and reflected as such. Changing things
 "on the fly", can lead to issues, where as migrations can be tested and throughly vetted before being exposed 
into your production environment. 

This is important, once a migration is in your production environment, **NEVER** modify it in your code. 
Treat a migration like a historical record of changes.

## Concepts

Every migration has several elements you need to be aware of. Additionally, there are over arching concepts that
will help you structure your project to take advantage of this library.

### A Migration

A migration looks like the following:

``` c#
// #1 - specify the migration number
[Migration(1)]                 
public class PeopleHaveFullNames : Migration // #2 inherit from Migration
{
    // #3 Do the migration using RQL.
    public override void Up()
    {
    }
    // #4 optional: undo the migration
    public override void Down()
    {
    }
}
```

To run the migrations, here's how it'd look in an ASP.NET Core app.

``` c#
// In Startup.cs
public void ConfigureServices(IServiceCollection services)
{
    // Add the MigrationRunner into the dependency injection container.
    services.AddMigrations();
}

public void Configure(IApplicationBuilder app, ...)
{
    // Run pending Hyperbee migrations.
    var migrationService = app.ApplicationServices.GetRequiredService<MigrationRunner>();
    migrationService.Run();
}
```

### Runner

Hyperbeen Migrations comes with a migration runner. It scans all provided assemblies for any classes implementing
the **Migration** base class and then orders them according to their migration value.

After each migration is executed, a document of type **MigrationDocument** is inserted into your database, to 
ensure the next time the runner is executed that migration is not executed again. When a migration is rolled 
back the document is removed.

You can modify the runner options by passing an action to the .AddHyperbeeDbMigrations call:

``` c#
services.AddHyperbeeDbMigrations(options =>
{
   // Configure the migration options here
});
```

#### Preventing simultaneous migrations

By default, Hyperbee Migrations sets a Hyperbee compare/exchange value to prevent simultaneous migrations runs. 
An example: if you have 2 instances of your app running, and both try to run migrations. When Hyperbee Migrations
detects this, it will prevent the other instances from running migrations and log a warning.

Hyperbee Migrations accomplishes this using a Hyperbee compare/exchange value to ensure no more than a single 
migration is running. It will set a `hyperbee-migrations-lock` compare/exchange value in your database during
migration run; its value set to a timeout date.

Be aware that if you abnormally terminate migrations -- for example, if you kill or your web host kills your app
during migration -- migrations will not be run until either you delete the `hyperbee-migrations-lock` 
compare/exchange value, or until its timeout passes. By default, the timeout is 1 hour.

If you wish to allow multiple simultaneous migrations or change the migration timeout lock, you can do so using 
override:

``` c#
services.AddHyperbeeDbMigrations(options =>
{
    // Allow simultaneous migrations - beware, here be dragons. Defautls to true.
    options.PreventSimultaneousMigrations = false;

    // Change how long the migrations lock can be held for. Defautls to 1 hour.
   options.SimultaneousMigrationTimeout = TimeSpan.FromMinutes(5);
});
```

``` c#

// In Startup.cs
public void ConfigureServices(IServiceCollection services)
{
    // pass the option single instance, the rest stays as is.
    services.AddHyperbeeDbMigrations(singleInstance: true);
}

... 

```


### Profiles

We understand there are times when you want to run specific migrations in certain environments, so Hyperbee Migrations 
supports profiles. For instance, some migrations might only run during development, by decorating your migration with 
the profile of *"development"* and setting the options to include the profile will execute that migration.

``` c#
[Migration(3, "development")]
public class Development_Migration : Migration
{
    public override void Up()
    {
        using (var session = Db.OpenSession())
        {
            session.Store(new { Id = "development-1" });
            session.SaveChanges();
        }
    }
}

...
// Add the MigrationRunner and configure it to run development migrations only.
services.AddHyperbeeDbMigrations(options => options.Profiles = new[] { "development" } });

```

You can also specify that a particular profile belongs in more than one profile by setting multiple profile names in 
the attribute.

``` c#
[Migration(3, "development", "demo")]
```

This migration would run if either (or both) the development and demo profiles were specified in the MigrationOptions.

### Advanced Migrations
Inside each of your Migration instances, you should use HyperbeeDB's <a href="https://Hyperbeedb.net/docs/article-page/4.0/csharp/client-api/operations/patching/set-based">patching APIs</a> to perform updates to collections and documents. We also provide helper methods on the Migration class for easy access, see below for examples.

#### Migration.PatchCollection
```Migration.PatchCollection``` is a helper method that <a href="https://Hyperbeedb.net/docs/article-page/4.0/csharp/client-api/operations/patching/set-based">patches a collection via RQL</a>.

``` c#
public override void Up()
{
   this.PatchCollection("from People update { p.Foo = 'Hello world!' }");
}
```

#### Migrations using dependency injection services
``` c#
[Migration(1)]
public class MyMigrationUsingServices : Migration
{
	private IFoo foo;

	// Inject an IFoo for use in our patch.
	public MyMigrationUsingServices(IFoo foo)
	{
		this.foo = foo;
	}

	public override void Up()
	{
		// Do something with foo
	}
}
```

#### Example: Adding and deleting properties
Let's say you start using a single Name property:

``` c#
public class Person
{
    public string Id { get; set; }
    public string Name { get; set; }
}
```
But then want to change using two properties, FirstName and LastName:
``` c#
public class Person
{
    public string Id { get; set; }
    public string FirstName { get; set; }
    public string LastName { get; set; }
}
```
You now need to migrate your documents or you will lose data when you load your new ```Person```.  The following 
migration uses RQL to split out the first and last names:

``` c#
[Migration(1)]
public class PersonNameMigration : Migration
{
    public override void Up()
    {
        this.PatchCollection(@"
            from People as p
            update {
                var names = p.Name.split(' ');
                p.FirstName = names[0];
                p.LastName = names[1];
                delete p.Name;
            }
        ");
    }

    // Undo the patch
    public override void Down()
    {
        this.PatchCollection("this.Name = this.FirstName + ' ' + this.LastName;");
    }
}
```

## Integration

We suggest you run the migrations at the start of your application to ensure that any new changes you have made 
apply to your application before you application starts. If you do not want to do it here, you can choose to do it 
out of band using a seperate application. If you're using ASP.NET Core, you can run them in your Startup.cs

``` c#
public void ConfigureServices(IServiceCollection services)
{
    // Add the MigrationRunner singleton into the dependency injection container.
    services.AddHyperbeeDbMigrations();

    // ...

    // Get the migration runner and execute pending migrations.
    var migrationRunner = services.BuildServiceProvider().GetRequiredService<MigrationRunner>();
    migrationRunner.Run();
}
```

Not using ASP.NET Core? You can create the runner manually:
``` c#
// Skip dependency injection and run the migrations.

// Create migration options, using all Migration objects found in the current assembly.
var options = new MigrationOptions();
options.Assemblies.Add(Assembly.GetExecutingAssembly());

// Create a new migration runner. docStore is your HyperbeeDB IDocumentStore. Logger is an ILogger<MigrationRunner>.
var migrationRunner = new MigrationRunner(docStore, options, logger);
migrationRunner.Run();
```

### Solution Structure

We recommend you create a folder called Migrations, then name your files according to the migration number and name:

```
\Migrations
    - 001_FirstMigration.cs
    - 002_SecondMigration.cs
    - 003_ThirdMigration.cs
```

The advantage to this approach, is that your IDE will order the migrations alpha-numerically allowing you to easily 
find the first and last migration.


