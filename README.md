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
    public async override Task UpAsync()
    {
    }
    // #4 optional: undo the migration
    public async override Task DownAsync()
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
    services.AddCouchbase(...);

    // Add the MigrationRunner into the container.
    services.AddCouchbaseMigrations(...);
}

public void Configure(IApplicationBuilder app, ...)
{
    // Run pending migrations.
    var migrationService = app.ApplicationServices.GetRequiredService<MigrationRunner>();
    migrationService.Run();
}
```

### The Runner

The migration runner scans all provided assemblies for any classes implementing the 
**Migration** base class and then orders them according to their migration value.

After each migration is executed, a **MigrationRecord** is inserted into your database, to ensure the 
next time the runner is executed that migration is not executed again. When a migration is rolled back 
the document is removed.

You can modify the runner options by passing an action to the **.AddCouchbaseMigrations** call:

``` c#
// In Startup.cs
public void ConfigureServices(IServiceCollection services)
{
    services.AddCouchbaseMigrations( options => 
    {
        // Configure migration options
        options.Direction = Direction.Down;
    });
}
```
#### Preventing simultaneous migrations

By default, Hyperbee Migrations uses a mutex to prevent parallel migration runs. 
If you have 2 instances of your app running, and both try to run migrations, Hyperbee Migrations
will prevent the second instance from running migrations and will log a warning.

Hyperbee Migrations accomplishes this by using a distributed mutex implemented at the database layer. 
The default implementation uses a timeout value and an auto-renewal interval to prevent orphaned locks.

If you wish to allow multiple simultaneous migrations, or change the migration timeout lock, you can do 
so by overriding the default migration options:

``` c#
services.AddCouchbaseMigrations( options =>
{
    // To allow simultaneous migrations - don't be that guy. Defaults to true.
    options.MutexEnabled = false;

    // To change how long the migrations lock can be held for. Defaults shown.
    options.MutexMaxLifetime = TimeSpan.FromHours( 1 );         // max time-to-live
    options.MutexExpireInterval = TimeSpan.FromMinutes( 5 );    // expire heartbeat
    options.MutexRenewInterval = TimeSpan.FromMinutes( 2 );     // renewal heartbeat
});
```

### Profiles

We understand there are times when you want to run specific migrations in certain environments. To allow this
Hyperbee Migrations supports profiles. For instance, some migrations might only run during development, by 
decorating your migration with the profile of *"development"* and setting the options to include only that 
profile, you can control which migrations run in which environments.

``` c#
[Migration(3, "development")]
public class Development_Migration : Migration
{
    public async override Task UpAsync()
    {
        // do something nice for local developers
    }
}

...
// Add the MigrationRunner and configure it to run development migrations only.
services.AddCouchbaseMigrations( options => options.Profiles = new[] { "development" } } );

```

You can also specify that a particular profile belongs in more than one profile by setting multiple profile names in 
the attribute.

``` c#
[Migration(3, "development", "demo")]
```

This migration would run if either (or both) the **development** and **demo** profiles were specified in 
**MigrationOptions**.

#### Migrations and dependency injection
Hyperbee.Migrations relies on dependency injection to pass services to your migration.

``` c#
[Migration(1)]
public class MyMigration : Migration
{
	private IClusterProvider _clusterProvider;
    private ILogger _logger;

	// Inject services registered with the container.
	public MyMigrationUsingServices( IClusterProvider clusterProvider,ILogger<MyMigration> logger )
	{
        _clusterProvider = clusterProvider;
		_logger = logger;
	}

	public async override Task UpAsync()
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
migration uses N1QL to split out the first and last names:

``` c#
[Migration(1)]
public class PersonNameMigration : Migration
{
	private IClusterProvider _clusterProvider;
    private ILogger _logger;

	public MyMigrationUsingServices( IClusterProvider clusterProvider,ILogger<PersonNameMigration> logger )
	{
        _clusterProvider = clusterProvider;
		_logger = logger;
	}

    public async override Task UpAsync()
    {
        var cluster = await _clusterProvider.GetClusterAsync();

        await cluster.QueryAsync( @"
            UPDATE `travel-sample`.inventory.airport
            SET FirstName = SPLIT(Name, ' ')[0],
                LastName = SPLIT(Names,' ')[1]
            UNSET Name" 
        ).ConfigureAwait( false );
    }

    // Undo
    public async override Task DownAsync()
    {
        var cluster = await _clusterProvider.GetClusterAsync();

        await cluster.QueryAsync( @"
            UPDATE `travel-sample`.inventory.airport
            SET Name = FirstName + ' ' + LastName
            UNSET FirstName, LastName" 
        ).ConfigureAwait( false );
    }
}
```

## Integration

We suggest you run the migrations at the start of your application to ensure that any new changes you have made 
apply to your application before you application starts. If you do not want to do it here, you can choose to do it 
out of band using a seperate application. If you're using ASP.NET Core, you can run them in your Startup.cs

``` c#
public void ConfigureServices( IServiceCollection services )
{
    // Add the MigrationRunner into the dependency injection container.
    services.AddCouchbaseMigrations();

    // ...

    // Get the migration runner and execute pending migrations.
    var migrationRunner = services.BuildServiceProvider().GetRequiredService<MigrationRunner>();
    migrationRunner.Run();
}
```

Not using ASP.NET Core? You can still use Dependency Injection:
``` c#
// create a host
var host = Host.CreateDefaultBuilder()
    .ConfigureServices( ( context, services ) =>
    {
        services.AddCouchbase(...);
        services.AddCouchbaseMigrations(...);
    } )
    .Build();

// create a container scope
using var serviceScope = host.Services.CreateScope();
{
    await serviceScope
        .ServiceProvider
        .GetRequiredService<Hyperbee.Migrations.MigrationRunner>()
        .RunAsync();
}
```

Don't want to use Dependency Injection? Derive from **IMigrationActivator** and have it your way. :

``` c#
// Derive from IMigrationActivator
public class MyCustomActivator : IMigrationActivator
{
    private IClusterProvider _clusterProvider;
    private ILoggerFactory _factory;

    public MyCustomActivator( IClusterProvider clusterProvider, ILoggerFactory factory )
    {
        _clusterProvider = clusterProvider;
        _factory = factory;
    }

    public Migration CreateInstance( Type migrationType )
        => (Migration) Activator.CreateInstance( migrationType, clusterProvider, _factory.CreateLogger( migrationType ) );
}

// Run migrations
public async Task MainAsync()
{
    // configure your store
    IClusterProvider clusterProvider = ...;
    ILoggerFactory factory = ...;

    var options = CouchbaseMigrationOptions
    {
        MigrationActivator = new MyCustomActivator( clusterProvider, factory ),
        Assemblies = new List<Assembly> { Assembly.GetEntryAssembly() }
    } );

    var store = new CouchbaseRecordStore( clusterProvider, options, logger );

    // run your migrations
    var runner = new MigrationRunner( store, options, logger );
    await runner.RunAsync();
}
```

### The Record Store
Hyperbee.Migrations currently supports **Couchbase** databases but it can easily be extended.
The steps are:

* #1 Derive from IMigrationRecordStore
* #2 Derive from MigrationOptions to add any store specific configuration
* #3 Implement ServiceCollectionExtensions to register your implementation

See the Couchbase implementation for reference.

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


