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
    // Configure couchbase
    services...

    // Add the MigrationRunner into the dependency injection container.
    services.AddCouchbaseMigrations();
}

public void Configure(IApplicationBuilder app, ...)
{
    // Run pending Hyperbee migrations.
    var migrationService = app.ApplicationServices.GetRequiredService<MigrationRunner>();
    migrationService.Run();
}
```

### The Runner

The migration runner scans all provided assemblies for any classes implementing the 
**Migration** base class and then orders them according to their migration value.

After each migration is executed, a document of type **MigrationDocument** is inserted into your database, to 
ensure the next time the runner is executed that migration is not executed again. When a migration is rolled 
back the document is removed.

You can modify the runner options by passing an action to the **.AddCouchbaseMigrations** call:

``` c#
// In Startup.cs
public void ConfigureServices(IServiceCollection services)
{
    services.AddCouchbaseMigrations( options => 
    {
        // Configure the migration options here
        options.Direction = Direction.Down;
    });
}
```
#### Preventing simultaneous migrations

By default, Hyperbee Migrations uses a mutex to prevent simultaneous migrations runs. 
An example: if you have 2 instances of your app running, and both try to run migrations. When Hyperbee Migrations
detects this, it will prevent the other instances from running migrations and log a warning.

Hyperbee Migrations accomplishes this using a distributed mutex at the database layer to prevent more than one 
migration runner from running. The default implementation uses a timeout value and an auto-renewal interval to
prevent orphaned locks.

If you wish to allow multiple simultaneous migrations or change the migration timeout lock, you can do so using 
override:

``` c#
services.AddCouchbaseMigrations(options =>
{
    // To allow simultaneous migrations - beware, here be dragons. Defaults to true.
    options.MutexEnabled = false;

    // To change how long the migrations lock can be held for. Defaults shown.
    options.MutexMaxLifetime = TimeSpan.FromHours( 1 );         // max time-to-live
    options.MutexExpireInterval = TimeSpan.FromMinutes( 5 );    // expire heartbeat
    options.MutexRenewInterval = TimeSpan.FromMinutes( 2 );     // renewal heartbeat
});
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
        // do something nice for developers
    }
}

...
// Add the MigrationRunner and configure it to run development migrations only.
services.AddCouchbaseMigrations(options => options.Profiles = new[] { "development" } });

```

You can also specify that a particular profile belongs in more than one profile by setting multiple profile names in 
the attribute.

``` c#
[Migration(3, "development", "demo")]
```

This migration would run if either (or both) the development and demo profiles were specified in the MigrationOptions.

#### Migrations using dependency injection services
``` c#
[Migration(1)]
public class MyMigrationUsingServices : Migration
{
	private IClusterProvider _clusterProvider;
    private ILogger<MyMigrationUsingServices> _logger;

	// Inject an IFoo for use in our patch.
	public MyMigrationUsingServices(IClusterProvider clusterProvider,ILogger<MyMigrationUsingServices> logger)
	{
        _clusterProvider = clusterProvider;
		_logger = logger;
	}

	public override void Up()
	{
		// do something with clusterProvider
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
    // Add the MigrationRunner into the dependency injection container.
    services.AddCouchbaseMigrations();

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


