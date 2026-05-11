# Hyperbee.Migrations.Providers.Couchbase

The Couchbase provider for [Hyperbee.Migrations](https://github.com/Stillpoint-Software/hyperbee.migrations) -- a versioned, journaled migration framework for .NET. Targets Couchbase Server 7+.

## Install

```bash
dotnet add package Hyperbee.Migrations.Providers.Couchbase
```

## Quick start

```csharp
using Couchbase.Extensions.DependencyInjection;
using Hyperbee.Migrations.Providers.Couchbase;

var builder = WebApplication.CreateBuilder( args );

// Couchbase cluster registration (Couchbase.Extensions.DependencyInjection).
builder.Services.AddCouchbase( opts =>
{
    opts.ConnectionString = builder.Configuration["Couchbase:ConnectionString"];
    opts.UserName         = builder.Configuration["Couchbase:Username"];
    opts.Password         = builder.Configuration["Couchbase:Password"];
} );

builder.Services.AddCouchbaseMigrations( opts =>
{
    opts.BucketName     = "hyperbee";
    opts.ScopeName      = "migrations";
    opts.CollectionName = "ledger";
    opts.LockingEnabled = true;
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

For configuration reference, N1QL statement grammar, resource layout, locking semantics, and operational guidance, see:

**https://stillpoint-software.github.io/hyperbee.migrations/couchbase.html**

A working sample lives in [`runners/samples/Hyperbee.Migrations.Couchbase.Samples/`](https://github.com/Stillpoint-Software/hyperbee.migrations/tree/main/runners/samples/Hyperbee.Migrations.Couchbase.Samples).

## License

[MIT](https://github.com/Stillpoint-Software/hyperbee.migrations/blob/main/LICENSE)
