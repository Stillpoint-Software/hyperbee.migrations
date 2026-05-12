# Hyperbee.Migrations

[![Build status](https://github.com/Stillpoint-Software/hyperbee.migrations/actions/workflows/pack_publish.yml/badge.svg)](https://github.com/Stillpoint-Software/hyperbee.migrations/actions/workflows/pack_publish.yml)
[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](LICENSE)
[![NuGet](https://img.shields.io/nuget/v/Hyperbee.Migrations.svg)](https://www.nuget.org/packages/Hyperbee.Migrations/)
[![.NET](https://img.shields.io/badge/.NET-8.0%20%7C%209.0%20%7C%2010.0-512BD4.svg)](https://dotnet.microsoft.com/)

A versioned, journaled migration framework for .NET that supports relational and NoSQL databases through a single, consistent API.

**Documentation:** https://stillpoint-software.github.io/hyperbee.migrations/

## Why

Database schema and data evolve over the life of an application. Hyperbee.Migrations gives you a structured, version-controlled way to evolve them across every environment -- local, test, staging, production -- with the same discipline you bring to source code. Migrations live in your repo as C# classes (or as embedded resource files), are discovered by reflection at runtime, and execute exactly once per environment.

## Supported providers

| Provider       | Package                                    | Statement format | Locking                         |
| -------------- | ------------------------------------------ | ---------------- | ------------------------------- |
| **Aerospike**  | `Hyperbee.Migrations.Providers.Aerospike`  | AQL-like         | CREATE_ONLY record + TTL        |
| **Couchbase**  | `Hyperbee.Migrations.Providers.Couchbase`  | N1QL             | Couchbase.Extensions.Locks      |
| **MongoDB**    | `Hyperbee.Migrations.Providers.MongoDB`    | shell-like       | document conditional write      |
| **OpenSearch** | `Hyperbee.Migrations.Providers.OpenSearch` | OpenSearch DSL   | `op_type=create` + realtime GET |
| **PostgreSQL** | `Hyperbee.Migrations.Providers.Postgres`   | raw `.sql` files | session-level advisory lock     |

Targets **.NET 8, 9, and 10**. Each provider package is shipped independently on NuGet.

## Install

```bash
dotnet add package Hyperbee.Migrations.Providers.Postgres
```

Or whichever provider matches your store. The core `Hyperbee.Migrations` library is referenced transitively.

## Quick start

```csharp
// Program.cs
using Hyperbee.Migrations.Providers.Postgres;

var builder = WebApplication.CreateBuilder( args );

// Register the Npgsql data source the migration runner reads from.
builder.Services.AddNpgsqlDataSource( builder.Configuration.GetConnectionString( "Migrations" ) );

builder.Services.AddPostgresMigrations( opts =>
{
    opts.SchemaName     = "migration";  // ledger schema (default: "migration")
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

A migration is a class:

```csharp
using Hyperbee.Migrations;

[Migration( 20260101_001 )]
public class CreateUsersTable( PostgresResourceRunner<CreateUsersTable> runner ) : Migration
{
    public override Task UpAsync( CancellationToken ct = default )
        => runner.AllSqlFromAsync( ct );
}
```

The companion resource file `Resources/20260101_001_CreateUsersTable.sql` ships in the assembly via `<EmbeddedResource>`.

For detailed walk-throughs by provider, see the documentation site:

- [Concepts](https://stillpoint-software.github.io/hyperbee.migrations/concepts.html)
- [Getting Started](https://stillpoint-software.github.io/hyperbee.migrations/getting-started.html)
- [PostgreSQL](https://stillpoint-software.github.io/hyperbee.migrations/postgresql.html) | [Aerospike](https://stillpoint-software.github.io/hyperbee.migrations/aerospike.html) | [Couchbase](https://stillpoint-software.github.io/hyperbee.migrations/couchbase.html) | [MongoDB](https://stillpoint-software.github.io/hyperbee.migrations/mongodb.html) | [OpenSearch](https://stillpoint-software.github.io/hyperbee.migrations/opensearch.html)

## Multi-provider hosts

A single application can register migrations for more than one provider:

```csharp
builder.Services
    .AddPostgresMigrations( opts => { /* ... */ } )
    .AddMongoDBMigrations(  opts => { /* ... */ } );

// Resolve typed runners; the base MigrationRunner resolution throws in
// multi-provider hosts to prevent silent shadowing.
var pg = sp.GetRequiredService<PostgresMigrationRunner>();
var mg = sp.GetRequiredService<MongoDBMigrationRunner>();
```

See [Multi-Provider Hosts](https://stillpoint-software.github.io/hyperbee.migrations/multi-provider-hosts.html) for the full pattern, failure-isolation samples, and the expand/contract recipe for cross-store changes.

## Project layout

| Path                                                   | Contents                                                                    |
| ------------------------------------------------------ | --------------------------------------------------------------------------- |
| [`src/Hyperbee.Migrations/`](src/Hyperbee.Migrations/) | Core: runner, options, record-store contract, conventions, resource helpers |
| [`src/Hyperbee.Migrations.Providers.*/`](src/)         | Per-provider implementations                                                |
| [`runners/Hyperbee.MigrationRunner.*/`](runners/)      | Per-provider standalone runner executables (Docker-ready)                   |
| [`runners/samples/`](runners/samples/)                 | Working samples per provider                                                |
| [`docs/site/`](docs/site/)                             | Jekyll documentation source (just-the-docs)                                 |
| [`docs/decisions/`](docs/decisions/)                   | Architecture Decision Records                                               |
| [`tests/`](tests/)                                     | Unit + Testcontainers integration tests                                     |

## Documentation

|                            |                                                            |
| -------------------------- | ---------------------------------------------------------- |
| **Concepts & guides**      | https://stillpoint-software.github.io/hyperbee.migrations/ |
| **Architecture decisions** | [`docs/decisions/`](docs/decisions/)                       |
| **Operator guides**        | [`docs/guides/`](docs/guides/)                             |
| **Changelog**              | [`CHANGELOG.md`](CHANGELOG.md)                             |

## Building from source

Requires .NET 8, 9, and 10 SDKs (the solution multi-targets all three for compatibility testing).

```bash
git clone https://github.com/Stillpoint-Software/hyperbee.migrations.git
cd hyperbee.migrations
dotnet build Hyperbee.Migrations.slnx -c Release
dotnet test  Hyperbee.Migrations.slnx -c Release
```

Integration tests use [Testcontainers](https://dotnet.testcontainers.org/) and require a Docker engine. They are gated behind `#if INTEGRATIONS`; enable with `-p:EnableIntegrationTests=true`.

## Acknowledgments

The framework API draws on prior art in the .NET migration space:

- [Fluent Migrator](https://github.com/schambers/fluentmigrator)
- [Raven Migrations](https://github.com/migrating-ravens/RavenMigrations)
- [DbUp](https://github.com/DbUp/DbUp)
- [Cronos](https://github.com/HangfireIO/Cronos) -- cron expression support
- [Couchbase .NET Client](https://github.com/couchbase/couchbase-net-client) -- Couchbase connectivity and DI extensions
- [Parlot](https://github.com/sebastienros/parlot) -- statement parsers across all providers

## Contributing

See [CONTRIBUTING.md](CONTRIBUTING.md) for the build/test/PR flow. We use a trunk-based GitHub flow; please open an issue to discuss substantial changes before sending a PR.

## License

Released under the [MIT License](LICENSE).
