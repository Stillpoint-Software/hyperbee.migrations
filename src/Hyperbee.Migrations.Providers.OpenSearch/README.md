# Hyperbee.Migrations.Providers.OpenSearch

The OpenSearch provider for [Hyperbee.Migrations](https://github.com/Stillpoint-Software/hyperbee.migrations) -- a versioned, journaled migration framework for .NET. Targets OpenSearch 2.x.

## Install

```bash
dotnet add package Hyperbee.Migrations.Providers.OpenSearch
```

For AWS Managed OpenSearch with SigV4 request signing, also install:

```bash
dotnet add package Hyperbee.Migrations.Providers.OpenSearch.Aws
```

## Quick start

```csharp
using OpenSearch.Client;
using Hyperbee.Migrations.Providers.OpenSearch;

var builder = WebApplication.CreateBuilder( args );

builder.Services.AddSingleton<IOpenSearchClient>( sp =>
{
    var settings = new ConnectionSettings(
        new Uri( builder.Configuration["OpenSearch:Endpoint"]! ) );
    return new OpenSearchClient( settings );
} );

builder.Services
    .WithProductionDefaults()
    .AddOpenSearchMigrations( opts =>
    {
        opts.LedgerIndex = ".migrations";
        opts.LockIndex   = ".migrations-lock";
        opts.LockName    = "host-lock";
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

For configuration reference, statement grammar (`CREATE INDEX`, `MIGRATE INDEX`, `ALIAS SWAP`, ISM policies, etc.), three-form body resolution, AWS auth modes, ledger forensics, and operational guidance, see:

**https://stillpoint-software.github.io/hyperbee.migrations/opensearch.html**

A common-questions FAQ for index-template behavior is at:

**https://stillpoint-software.github.io/hyperbee.migrations/opensearch-template-propagation-faq.html**

A working sample lives in [`runners/samples/Hyperbee.Migrations.OpenSearch.Samples/`](https://github.com/Stillpoint-Software/hyperbee.migrations/tree/main/runners/samples/Hyperbee.Migrations.OpenSearch.Samples).

## Multi-provider hosts

For applications that host more than one provider in the same `IServiceCollection`, resolve `OpenSearchMigrationRunner` directly rather than the base `MigrationRunner` (which throws in multi-provider hosts per [ADR-0023](https://github.com/Stillpoint-Software/hyperbee.migrations/blob/main/docs/decisions/0023-multi-runner-not-meta-runner.md)). See the [multi-provider hosts guide](https://stillpoint-software.github.io/hyperbee.migrations/multi-provider-hosts.html) for the failure-isolation, parallel-composition, and expand/contract patterns.

## License

[MIT](https://github.com/Stillpoint-Software/hyperbee.migrations/blob/main/LICENSE)
