# Hyperbee Migrations

## Introduction

Hyperbee Migrations is a migration framework for .NET. Migrations are a structured way to alter your database
schema and are an alternative to creating lots of database scripts that have to be run manually by every
developer involved. Migrations solve the problem of evolving a database schema (and data) for multiple databases
(for example, the developer's local database, the test database and the production database). Database changes
are described in classes written in C# that can be checked into a version control system.

The framework API is heavily influenced by [Fluent Migrator](https://github.com/schambers/fluentmigrator), [Raven Migrations](https://github.com/migrating-ravens/RavenMigrations) and [DbUp](https://github.com/DbUp/DbUp)

## Concepts

Every migration has several elements you need to be aware of.

### A Migration

A migration looks like the following:

```c#
// #1 - specify the migration number
[Migration(1)]
public class PeopleHaveFullNames : Migration // #2 inherit from Migration
{
    // #3 do the migration
    public async override Task UpAsync( CancellationToken cancellationToken = default )
    {
    }

    // #4 optional: undo the migration
    public async override Task DownAsync( CancellationToken cancellationToken = default )
    {
    }
}
```

For simple applications migrations can be run from an ASP.NET Core app.

```c#
// In Startup.cs
public void ConfigureServices(IServiceCollection services)
{
    // Configure couchbase
    services.AddCouchbase(...);

    // Add the MigrationRunner
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

At the heart of migrations is the **MigrationRunner**. The migration runner scans all provided assemblies for
classes deriving from the **Migration** base class and then orders them according to their migration attribute
value.

After each migration is executed a **MigrationRecord** is inserted into your database. This ensures that the
next time the runner is executed, previously completed migrations are not executed again. When a migration is
rolled back the **MigrationRecord** is removed.

You can modify the runner options by passing an action to the **.AddCouchbaseMigrations** call:

```c#
// In Startup.cs
public void ConfigureServices( IServiceCollection services )
{
    services.AddCouchbaseMigrations( options =>
    {
        // Configure migration options
        options.Direction = Direction.Down;
    });
}
```

#### Preventing simultaneous migrations

By default, Hyperbee Migrations prevents parallel migration runner execution. If you have 2 instances of your
app running, and both try to run migrations, Hyperbee Migrations will prevent the second instance from running
migrations and will log a warning.

Hyperbee Migrations accomplishes this by using a distributed lock at the database layer. The default
implementation is based on the provider and uses a timeout and an auto-renewal interval to prevent orphaned locks.

If you want to change this behavior you can override the default options:

```c#
services.AddCouchbaseMigrations( options =>
{
    // Locking is on by default. Set to false to allow simultaneous runners - but don't be that guy.
    options.LockingEnabled = false;

    // You can change locking behavior. Defaults shown.
    options.LockMaxLifetime = TimeSpan.FromHours( 1 );         // max time-to-live
    options.LockExpireInterval = TimeSpan.FromMinutes( 5 );    // expire heartbeat
    options.LockRenewInterval = TimeSpan.FromMinutes( 2 );     // renewal heartbeat
});
```

### Profiles

There are times when you may want to scope migrations to specific environments. To allow this Hyperbee Migrations
supports profiles. For instance, some migrations might only run during development. By decorating your migration
with the profile of _"development"_ and setting **options** to include only that profile, you can control which
migrations run in which environments.

```c#
[Migration(3, "development")]
public class DevelopmentOnlyMigration : Migration
{
    public async override Task UpAsync( CancellationToken cancellationToken = default )
    {
        // do something nice for local developers
    }
}

...

// In Startup.cs
public void ConfigureServices( IServiceCollection services )
{
    services.AddCouchbaseMigrations( options =>
    {
        // Configure to only run development migrations
         options.Profiles = new[] { "development" } };
    });
}
```

A migration may belong to multiple profiles.

```c#
[Migration(3, "development", "staging")]
public class TargetedMigration : Migration
{
    // ...
}
```

This migration will run if either the **development** or the **stating** profile is specified in
**MigrationOptions**.

#### Migrations and Dependency Injection

Hyperbee Migrations relies on dependency injection to pass services to your migration.

```c#
[Migration(1)]
public class MyMigration : Migration
{
	private IClusterProvider _clusterProvider;
    private ILogger _logger;

	// Injected services registered with the container
	public MyMigration( IClusterProvider clusterProvider, ILogger<MyMigration> logger )
	{
        _clusterProvider = clusterProvider;
		_logger = logger;
	}

	public async override Task UpAsync( CancellationToken cancellationToken = default )
	{
		// do something with clusterProvider
	}
}
```

## Integration

We suggest you run the migrations at the start of your application to ensure that any new changes you have made
apply to your application before you application starts. If you do not want to do it here, you can choose to do it
out of band using a seperate application. If you're using ASP.NET Core, you can run them in your Startup.cs

```c#
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

Running a Console App? You can still use Dependency Injection:

```c#
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

Don't want to use Dependency Injection? Derive from **IMigrationActivator** and have it your way:

```c#
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
public async Task Main()
{
    // configure
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

Hyperbee Migrations currently supports **Couchbase** databases but it can easily be extended.
The steps are:

1. Derive from IMigrationRecordStore
2. Derive from MigrationOptions to add any store specific configuration
3. Implement ServiceCollectionExtensions to register your implementation

See the Couchbase implementation for reference.

## Configure Local Solution

To run the migration solution you will need to add some local configuration.

`appsettings.developer.json`

```json
{
  "Couchbase": {
    "ConnectionString": "couchbase://localhost"
  }
}
```

`Manage User Secrets`

```json
{
  "Couchbase:UserName": "Administrator",
  "Couchbase:Password": "_YOUR_PASSWORD_"
}
```

## Installing The MigrationRunner

The runner can be installed as a dotnet tool.

```powershell
# install the runner from nuget
dotnet tool install Hyperbee.MigrationRunner --global --add-source {NUGET_SOURCE}
```

```powershell
# install the runner from a local copy
dotnet tool install Hyperbee.MigrationRunner --global --version 1.0.0-local.{version}
```

```powershell
# uninstall the runner
dotnet tool list -g
dotnet tool uninstall Hyperbee.MigrationRunner --global
```

### Running From The Command Line

Once installed as a dotnet tool, the runner can be run from the command line. The runner expects, and will use settings
from the `appSettings.json` in the execution folder. Arguments can also be provided from the command line.

#### Command Line Options

| Switch | Alias        | Description           |
| ------ | ------------ | --------------------- |
| -f     | --file       | From Paths Array      |
| -a     | --assembly   | From Assemblies Array |
| -p     | --profile    | Profiles Array        |
| -b     | --bucket     | Bucket Name           |
| -s     | --scope      | Scope Name            |
| -c     | --collection | Collection Name       |
| -usr   | --user       | Database User         |
| -pwd   | --password   | Database Password     |
| -cs    | --connection | Database Connection String |

#### Runtime Configuration

```json
{
  "Couchbase": {
    "ConnectionString": "__CONNECTION_STRING_HERE__",
    "UserName": "__SECRET_HERE__",
    "Password": "__SECRET_HERE__",
    "MaxConnectionLimit": 20
  },
  "Migrations": {
    "BucketName": "hyperbee",
    "ScopeName": "migrations",
    "CollectionName": "ledger",
    "Lock": {
      "Enabled": false,
      "Name": "migration-runner-mutex",
      "MaxLifetime": 3600,
      "ExpireInterval": 300,
      "RenewInterval": 120
    },
    "FromPaths": [
      "c:\\my-migration-assembly.dll"
    ],
    "FromAssemblies": [
    ]
  },
  "Serilog": {
    "MinimumLevel": {
      "Default": "Debug",
      "Override": {
        "Couchbase": "Warning",
        "Microsoft": "Warning",
        "Microsoft.Hosting.Lifetime": "Information",
        "System": "Warning"
      }
    }
  }
}
```


#### Examples

```powershell
# example: run using values in settings file (default)
hyperbee.migrationrunner

# example: pass multiple migration assemblies to the runner
hyperbee.migrationrunner -a AssemblyName1 -a AssemblyName2 

# example: pass multiple migration assemblies by path to the runner
hyperbee.migrationrunner -f "c:\folder\AssemblyName1.dll" -f "c:\folder\AssemblyName2.dll" 

# example: pass multiple migration assemblies by path to the runner
hyperbee.migrationrunner -f "c:\folder\AssemblyName1.dll" -f "c:\folder\AssemblyName2.dll" 

```

# TODO

* Dynamic provider loading
* Option to specify provider through configuration and/or command line
* Providers should have own startup/registration
* Add Hyperbee.Migration.Providers.Mongodb
  * Mongo bootstrapper
  * Mongo distributed lock
  * Mongo resource runner and basic statements.json parser
* Relocate shared functionality for providers
  * WaitHelper
* Provider samples
  * Rename Hyperbee.Migrations.Samples to Hyperbee.Migrations.Samples.Couchbase
  * Create Hyperbee.Migrations.Samples.Mongodb
