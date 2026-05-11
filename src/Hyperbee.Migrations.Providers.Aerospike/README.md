# Hyperbee.Migrations.Providers.Aerospike

The Aerospike provider for [Hyperbee.Migrations](https://github.com/Stillpoint-Software/hyperbee.migrations) -- a versioned, journaled migration framework for .NET. Targets Aerospike Server 6+.

## Install

```bash
dotnet add package Hyperbee.Migrations.Providers.Aerospike
```

## Quick start

```csharp
using Aerospike.Client;
using Hyperbee.Migrations.Providers.Aerospike;

var builder = WebApplication.CreateBuilder( args );

// Aerospike client (single-node example).
builder.Services.AddSingleton<IAsyncClient>(
    new AsyncClient( "localhost", 3000 ) );

builder.Services.AddAerospikeMigrations( opts =>
{
    opts.Namespace      = "test";
    opts.MigrationSet   = "SchemaMigrations";
    opts.LockName       = "migration_lock";
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

For configuration reference, statement grammar (`CREATE INDEX WAIT`, secondary index types, etc.), resource layout, locking semantics, and operational guidance, see:

**https://stillpoint-software.github.io/hyperbee.migrations/aerospike.html**

A working sample lives in [`runners/samples/Hyperbee.Migrations.Aerospike.Samples/`](https://github.com/Stillpoint-Software/hyperbee.migrations/tree/main/runners/samples/Hyperbee.Migrations.Aerospike.Samples).

## License

[MIT](https://github.com/Stillpoint-Software/hyperbee.migrations/blob/main/LICENSE)
