---
layout: default
title: Getting Started
nav_order: 3
---

# Getting Started

This guide walks you through adding Hyperbee.Migrations to a .NET project and running your first migration.

## Installation

Install the NuGet package for your database provider:

```bash
dotnet add package Hyperbee.Migrations.Providers.Aerospike
dotnet add package Hyperbee.Migrations.Providers.Couchbase
dotnet add package Hyperbee.Migrations.Providers.MongoDB
dotnet add package Hyperbee.Migrations.Providers.Postgres
```

You only need the package for the provider you are using.

## Create Your First Migration

A migration is a class that inherits from `Migration` and is decorated with a `[Migration]` attribute. The numeric argument determines execution order.

```csharp
using Hyperbee.Migrations;

[Migration(1)]
public class CreateUsersTable : Migration
{
    public override async Task UpAsync(CancellationToken cancellationToken = default)
    {
        // Your database operations here
    }
}
```

Migrations run in order and are tracked so they execute only once.

## Register Services

Register the migration framework in your DI container. Each provider has its own extension method.

### PostgreSQL

```csharp
services.AddNpgsqlDataSource(connectionString);
services.AddPostgresMigrations(options =>
{
    options.Assemblies.Add(typeof(CreateUsersTable).Assembly);
});
```

### MongoDB

```csharp
services.AddTransient<IMongoClient>(_ => new MongoClient(connectionString));
services.AddMongoDBMigrations(options =>
{
    options.Assemblies.Add(typeof(CreateUsersTable).Assembly);
});
```

### Aerospike

```csharp
services.AddSingleton<IAsyncClient>(new AsyncClient(host, port));
services.AddSingleton<IAerospikeClient>(provider =>
    (IAerospikeClient)provider.GetRequiredService<IAsyncClient>());
services.AddAerospikeMigrations(options =>
{
    options.Assemblies.Add(typeof(CreateUsersTable).Assembly);
});
```

### Couchbase

```csharp
services.AddCouchbase(options => { /* cluster config */ });
services.AddCouchbaseMigrations(options =>
{
    options.BucketName = "myapp";
    options.Assemblies.Add(typeof(CreateUsersTable).Assembly);
});
```

## Run Migrations

### In-Process (Application Startup)

Resolve the `MigrationRunner` from the service provider and call `RunAsync`. This is the simplest approach -- migrations execute when your application starts.

```csharp
var runner = app.Services.GetRequiredService<MigrationRunner>();
await runner.RunAsync();
```

### Standalone Runner

For production environments you may prefer to run migrations outside your application process. See the sample runners in the `runners/` directory for ready-made examples. You can also use `FromPaths` in configuration to load migration assemblies at runtime, which is useful for deploying migrations independently from the application.

## Next Steps

- [Concepts](concepts.html) -- Understand how the migration framework works
- [Code Migrations](code-migrations.html) -- Write code-based migrations with full provider access
- [Resource Migrations](resource-migrations.html) -- Use declarative, file-based migrations
- [PostgreSQL](postgresql.html), [MongoDB](mongodb.html), [Couchbase](couchbase.html), [Aerospike](aerospike.html) -- Provider-specific details
