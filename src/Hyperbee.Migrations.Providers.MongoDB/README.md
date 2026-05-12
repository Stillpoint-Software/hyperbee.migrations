# Hyperbee.Migrations.Providers.MongoDB

The MongoDB provider for [Hyperbee.Migrations](https://github.com/Stillpoint-Software/hyperbee.migrations) -- a versioned, journaled migration framework for .NET. Targets MongoDB Server 6+ (replica set or standalone).

## Install

```bash
dotnet add package Hyperbee.Migrations.Providers.MongoDB
```

## Quick start

```csharp
using MongoDB.Driver;
using Hyperbee.Migrations.Providers.MongoDB;

var builder = WebApplication.CreateBuilder( args );

// MongoClient is thread-safe and intended to be a singleton.
builder.Services.AddSingleton<IMongoClient>(
    new MongoClient( builder.Configuration["Mongo:ConnectionString"] ) );

builder.Services.AddMongoDBMigrations( opts =>
{
    opts.DatabaseName    = "hyperbee";
    opts.CollectionName  = "ledger";
    opts.LockingEnabled  = true;
} );

var app = builder.Build();

using ( var scope = app.Services.CreateScope() )
{
    var runner = scope.ServiceProvider.GetRequiredService<MigrationRunner>();
    await runner.RunAsync( app.Lifetime.ApplicationStopping );
}

app.Run();
```

## Documentation

For configuration reference, statement grammar (`CREATE COLLECTION`, `CREATE [UNIQUE] INDEX ON db.col(...)`, etc.), resource layout, locking semantics, and operational guidance, see:

**https://stillpoint-software.github.io/hyperbee.migrations/mongodb.html**

A working sample lives in [`runners/samples/Hyperbee.Migrations.MongoDB.Samples/`](https://github.com/Stillpoint-Software/hyperbee.migrations/tree/main/runners/samples/Hyperbee.Migrations.MongoDB.Samples).

## Multi-provider hosts

For applications that host more than one provider in the same `IServiceCollection`, resolve `MongoDBMigrationRunner` directly rather than the base `MigrationRunner` (which throws in multi-provider hosts per [ADR-0023](https://github.com/Stillpoint-Software/hyperbee.migrations/blob/main/docs/decisions/0023-multi-runner-not-meta-runner.md)). See the [multi-provider hosts guide](https://stillpoint-software.github.io/hyperbee.migrations/multi-provider-hosts.html) for the failure-isolation, parallel-composition, and expand/contract patterns.

## License

[MIT](https://github.com/Stillpoint-Software/hyperbee.migrations/blob/main/LICENSE)
