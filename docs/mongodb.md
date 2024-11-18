---
layout: default
title: Migrations MongoDB Provider
nav_order: 3
---
# Hyperbee Migrations MongoDB Provider

Welcome to `Hyperbee.Migrations.Providers.MongoDB`, an extension to the foundational `Hyperbee.Migrations` library. This extension is specifically designed to incorporate support for MongoDB.

## Introduction

The `Hyperbee.Migrations.Providers.MongoDB` library is an tool for developers seeking to perform database migrations in a MongoDB environment using the `Hyperbee.Migrations` library. This library furnishes a comprehensive suite of tools and functionalities that integrate effortlessly with `Hyperbee.Migrations`, thereby enabling developers to harness the robust capabilities of MongoDB in their applications.

The `Hyperbee.Migrations.Providers.MongoDB` library is equipped to assist developers in creating, updating, and managing their MongoDB databases. With the aid of this library, developers can concentrate their efforts on the development of their application, while the library handles the intricacies of database management.

We are committed to continuous improvement and feature enhancement. We appreciate your interest and look forward to your valuable feedback.

Please see [Hyperbee Migrations' Read Me](index.md) for non-database specific usage.


## Concepts

Every migration has several elements you need to be aware of.

* You can create a StartMethod method that resolves to **Task \<bool>**, in order to tell the runner when to start.
* You can create a StopMethod method that resolves to **Task \<bool>**, in order to tell the runner when to stop.
* You can set whether or not you want to journal the migration.

## Configuration

### Add MongoDB Services
```c#
// In Startup.cs
public void ConfigureServices(IServiceCollection services)
{
    // Configure MongoDB
    services.AddTransient<IMongoClient, MongoClient>( _ => new MongoClient( connectionString ) );

    // Add the MigrationRunner
    services.AddMongoDBMigrations(...);
}

public void Configure(IApplicationBuilder app, ...)
{
    // Run pending migrations.
    var migrationService = app.ApplicationServices.GetRequiredService<MigrationRunner>();
    migrationService.Run();
}
```
### Preventing simultaneous migrations

By default, Hyperbee Migrations prevents parallel migration runner execution. If you have 2 instances of your
app running, and both try to run migrations, Hyperbee Migrations will prevent the second instance from running
migrations and will log a warning.

Hyperbee Migrations accomplishes this by using a distributed lock at the database layer. The default
implementation is based on the provider and uses a timeout and an auto-renewal interval to prevent orphaned locks.

If you want to change this behavior you can override the default options:

```c#
services.AddMongoDBMigrations( options =>
{
    // Locking is on by default. Set to false to allow simultaneous runners - but don't be that guy.
    options.LockingEnabled = false;

    // You can change locking behavior. Defaults shown.
    options.LockMaxLifetime = TimeSpan.FromMinutes( 1 );         // max time-to-live
    options.LockName = "ledger"
});
```

### Migrations
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

### Dependency Injection

Hyperbee Migrations relies on dependency injection to pass services to your migration.  For MongoDB you can directly use the `IMongoClient` and interact with MongoDB directly.

```c#
[Migration(1)]
public class MyMigration : Migration
{
	private IMongoClient _mongoClient;
    private ILogger _logger;

	// Injected services registered with the container
	public MyMigration( IMongoClient mongoClient, ILogger<MyMigration> logger )
	{
        _mongoClient = mongoClient;
		_logger = logger;
	}

	public async override Task UpAsync( CancellationToken cancellationToken = default )
	{
		// do something with mongoClient
	}
}
```

The MongoDB provider also provides a `MongoDBResourceRunner<MyMigration>` that adds helpful functionality when using embedded resources.  
 - `DocumentsFromAsync` inserts documents into database/collections within MongoDB.  This is normally use for pre seeding the database.
 - `StartMethod` determines when the migration should start (optional)
 - `StopMethod` determines when the migration should stop (optional)
 - `false` determines if you want to journal (default = true)

```c#
[Migration(1, "StartMethod", "StopMethod", false)]
public class MyMigration : Migration
{
    private readonly MongoDBResourceRunner<MyMigration> _resourceRunner;

    public CreateInitialBuckets( MongoDBResourceRunner<MyMigration> resourceRunner )
    {
        _resourceRunner = resourceRunner;
    }

    public override async Task UpAsync( CancellationToken cancellationToken = default )
    {
        // run a `resource` migration to create initial state.
        await resourceRunner.DocumentsFromAsync( [
            "administration/users/user.json"
        ], cancellationToken );
    }

    public Task<bool> StartMethod()
    {
      //create process here        
    }
    
    public Task<bool> StopMethod()
    {
      //create process here    
    }
}
```