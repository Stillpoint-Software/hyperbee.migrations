---
layout: default
title: Migrations Couchbase Provider
nav_order: 2
---

# Hyperbee Migrations Couchbase Provider

Welcome to `Hyperbee.Migrations.Providers.Couchbase`, an extension to the foundational `Hyperbee.Migrations` library. This extension is specifically designed to incorporate support for Couchbase.

## Introduction

The `Hyperbee.Migrations.Providers.Couchbase` library is an tool for developers seeking to perform database migrations in a Couchbase environment using the `Hyperbee.Migrations` library. This library furnishes a comprehensive suite of tools and functionalities that integrate effortlessly with `Hyperbee.Migrations`, thereby enabling developers to harness the robust capabilities of Couchbase in their applications.

The `Hyperbee.Migrations.Providers.Couchbase` library is equipped to assist developers in creating, updating, and managing their Couchbase databases. With the aid of this library, developers can concentrate their efforts on the development of their application, while the library handles the intricacies of database management.

We are committed to continuous improvement and feature enhancement. We appreciate your interest and look forward to your valuable feedback.

Please see [Hyperbee Migrations' Read Me](index.md) for non-database specific usage.


## Concepts

Every migration has several elements you need to be aware of.

* You can create a StartMethod method that resolves to **Task \<bool>**, in order to tell the runner when to start.
* You can create a StopMethod method that resolves to **Task \<bool>**, in order to tell the runner when to stop.
* You can set whether or not you want to journal the migration.

## Configuration

### Add Couchbase Services
```c#
// In Startup.cs
public void ConfigureServices(IServiceCollection services)
{
    // Configure MongoDB
    services.AddCouchbase();

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
### Preventing simultaneous migrations

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

The MongoDB provider also provides a `MongoDBResourceRunner<MyMigration>` that adds helpful functionality when using embedded resources.  
 - `StatementsFromAsync` run SQL++ (N1QL) statements for different bucket/scope/collections within Couchbase.
 - `DocumentsFromAsync` Upserts documents into Couchbase. This is normally use for pre-seeding.
 - `StartMethod` determines when the migration should start (optional)
 - `StopMethod` determines when the migration should stop (optional)
 - `false` determines if you want to journal (default = true)

```c#
[Migration(1, "StartMethod", "StopMethod", false)]
public class MyMigration : Migration
{
    private readonly CouchbaseResourceRunner<MyMigration> _resourceRunner;

    public CreateInitialBuckets( CouchbaseResourceRunner<MyMigration> resourceRunner )
    {
        _resourceRunner = resourceRunner;
    }

    public override async Task UpAsync( CancellationToken cancellationToken = default )
    {
        // run a `resource` migration to create initial buckets and state.
        // resource migrations are atypical; prefer `n1ql` migrations.

        await _resourceRunner.StatementsFromAsync( new[]
            {
                "statements.json",
                "migrationbucket/statements.json"
            },
            cancellationToken
        );

        await _resourceRunner.DocumentsFromAsync( new[]
            {
                "migrationbucket/_default"
            },
            cancellationToken
        );
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

### Cron Settings
```c#
[Migration(3, "StartMethod", "StopMethod")]
public class DevelopmentOnlyMigration : Migration
{
    public async override Task UpAsync( CancellationToken cancellationToken = default )
    {
        // do something nice for local developers
    }

     public async Task<bool> StartMethod()
    {
        var helper = new MigrationCronHelper();
        var results = await helper.CronDelayAsync( "* * * * *" );
        return results;       
    }

    public Task<bool> StopMethod()
    {
       var helper = new MigrationCronHelper();
       var results = await helper.CronDelayAsync( "4 * * * *" );
       return results;   
    }
}
```

### Journaling
Journaling is a bool indicator.  Null indicates there are no start or stop methods.
```c#
[Migration(3, null, null, false)]
public class DevelopmentOnlyMigration : Migration
{
    public async override Task UpAsync( CancellationToken cancellationToken = default )
    {
        // do something nice for local developers
    }
}