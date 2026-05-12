# Hyperbee.Migrations

The core library for [Hyperbee.Migrations](https://github.com/Stillpoint-Software/hyperbee.migrations) -- a versioned, journaled migration framework for .NET. Multi-targets .NET 8, 9, and 10.

This package contains the `MigrationRunner`, the `IMigrationRecordStore` contract, the `[Migration]` attribute, conventions, and the resource-helper plumbing. **You typically install a provider package instead of this one directly** -- each provider package transitively pulls this in.

## Install

Install the provider package that matches your store:

```bash
dotnet add package Hyperbee.Migrations.Providers.Aerospike
dotnet add package Hyperbee.Migrations.Providers.Couchbase
dotnet add package Hyperbee.Migrations.Providers.MongoDB
dotnet add package Hyperbee.Migrations.Providers.OpenSearch
dotnet add package Hyperbee.Migrations.Providers.Postgres
```

Install this core package directly only if you are writing a custom record store:

```bash
dotnet add package Hyperbee.Migrations
```

## Quick start

```csharp
using Hyperbee.Migrations.Providers.Postgres;

var builder = WebApplication.CreateBuilder( args );

builder.Services.AddNpgsqlDataSource( builder.Configuration.GetConnectionString( "Migrations" ) );

builder.Services.AddPostgresMigrations( opts =>
{
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

A migration is a C# class:

```csharp
using Hyperbee.Migrations;

[Migration( 20260101_001 )]
public class CreateUsersTable : Migration
{
    public override Task UpAsync( CancellationToken ct = default )
    {
        // apply the change
        return Task.CompletedTask;
    }
}
```

## Documentation

For full configuration, statement-format reference, locking semantics, multi-provider hosts, squashing migrations, and operational guidance, see:

**https://stillpoint-software.github.io/hyperbee.migrations/**

## License

[MIT](https://github.com/Stillpoint-Software/hyperbee.migrations/blob/main/LICENSE)
