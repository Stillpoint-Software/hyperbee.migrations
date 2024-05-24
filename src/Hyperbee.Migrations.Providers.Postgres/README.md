# Hyperbee Migrations Postgres Provider

Welcome to `Hyperbee.Migrations.Providers.Postgres`, an extension to the foundational `Hyperbee.Migrations` library. This extension is specifically designed to incorporate support for Postgres.

## Introduction

The `Hyperbee.Migrations.Providers.Postgres` library is an tool for developers seeking to perform database migrations in a Postgres environment using the `Hyperbee.Migrations` library. This library furnishes a comprehensive suite of tools and functionalities that integrate effortlessly with `Hyperbee.Migrations`, thereby enabling developers to harness the robust capabilities of Postgres in their applications.

The `Hyperbee.Migrations.Providers.Postgres` library is equipped to assist developers in creating, updating, and managing their Postgres databases. With the aid of this library, developers can concentrate their efforts on the development of their application, while the library handles the intricacies of database management.

We are committed to continuous improvement and feature enhancement. We appreciate your interest and look forward to your valuable feedback.

Please see [Hyperbee Migrations' Read Me](../Hyperbee.Migrations/README.md) for non-database specific usage.

## Configuration

### Add Postgres Services
```c#
// In Startup.cs
public void ConfigureServices(IServiceCollection services)
{
    // Configure Postgres
    services.AddNpgsqlDataSource( connectionString );

    // Add the MigrationRunner
    services.AddPostgresMigrations(...);
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
services.AddPostgresDBMigrations( options =>
{
    // Locking is on by default. Set to false to allow simultaneous runners - but don't be that guy.
    options.LockingEnabled = false;

    // You can change locking behavior. Defaults shown.
    options.LockMaxLifetime = TimeSpan.FromMinutes( 1 );         // max time-to-live
    options.LockName = "ledger"
});
```

### Dependency Injection

The Postgres provider also provides a `PostgresResourceRunner<MyMigration>` that adds helpful functionality when using embedded resources.  
 - `SqlFromAsync` runs sql statements and can do any sort of updates supported by Postgres
 - `AllSqlFromAsync` runs all the embedded resources with the resource named matching the Migration. See [1000-Initial](../../samples/Hyperbee.Migrations.Postgres.Samples/Migrations/1000-Initial.cs) as an example
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
        await resourceRunner.SqlFromAsync( [
            "1-MyMigration.sql"
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
