---
layout: default
title: Hyperbee Migrations
nav_order: 1
---

# Welcome to Hyperbee Migrations

Hyperbee Migrations is a database migration framework for .NET. It gives your team a
structured, version-controlled way to evolve database schemas and data across every
environment -- from local dev boxes to production clusters. Instead of maintaining
loose SQL scripts, you describe changes in C# classes or embedded resource files that
are discovered, ordered, and executed automatically.

## Key Features

- Supports **Aerospike**, **Couchbase**, **MongoDB**, **OpenSearch**, and **PostgreSQL**
- Code migrations with full dependency injection
- Resource migrations with embedded SQL, N1QL, AQL, MongoDB commands, and OpenSearch DDL
- Document seeding from JSON files
- Distributed locking to prevent concurrent migrations
- Profile-based environment scoping
- Cron-based lifecycle control
- Migration journaling and record tracking
- Standalone runners with Docker support

## A Quick Example

A migration is a simple class decorated with a version attribute:

```csharp
[Migration(1)]
public class CreatePeopleCollection : Migration
{
    public override async Task UpAsync(CancellationToken cancellationToken = default)
    {
        // apply the change
    }

    public override async Task DownAsync(CancellationToken cancellationToken = default)
    {
        // optionally reverse the change
    }
}
```

Migrations are discovered by reflection, executed in version order, and journaled so
they only run once.

## Documentation Guide

| Page | What You Will Find |
|------|--------------------|
| [Concepts](concepts.md) | Core concepts: migration types, versioning, idempotency, profiles, locking |
| [Getting Started](getting-started.md) | Step-by-step setup for each provider |
| [Code Migrations](code-migrations.md) | Writing C# code migrations with dependency injection |
| [Continuous Migrations](continuous-migrations.md) | Long-running, scheduled, and repeating migrations |
| [Resource Migrations](resource-migrations.md) | Declarative migrations with embedded resource files |
| [Runners](runners.md) | Standalone runners, CLI reference, Docker |
| [Multi-Provider Hosts](multi-provider-hosts.md) | Composing two or more providers in a single application |
| [Aerospike](aerospike.md) | Aerospike provider reference |
| [Couchbase](couchbase.md) | Couchbase provider reference |
| [MongoDB](mongodb.md) | MongoDB provider reference |
| [OpenSearch](opensearch.md) | OpenSearch provider reference |
| [OpenSearch FAQ - Template Propagation](opensearch-template-propagation-faq.md) | Detailed FAQ on OpenSearch index-template behavior |
| [PostgreSQL](postgresql.md) | PostgreSQL provider reference |
| [Squashing Migrations](squashing-migrations.md) | Compact long migration histories into a single fast-applying migration |
| [Troubleshooting](troubleshooting.md) | Common errors, recovery flows, frequently asked questions |
| [Supported Versions](supported-versions.md) | .NET TFMs and provider-server versions |
| [Advanced Topics](advanced.md) | Custom providers, retry strategies, locking internals |

## Installation

Install the provider package that matches your database:

```bash
dotnet add package Hyperbee.Migrations.Providers.Aerospike
dotnet add package Hyperbee.Migrations.Providers.Couchbase
dotnet add package Hyperbee.Migrations.Providers.MongoDB
dotnet add package Hyperbee.Migrations.Providers.OpenSearch
dotnet add package Hyperbee.Migrations.Providers.Postgres
```

The core library is referenced transitively by every provider package; install it directly only if you are writing a custom record store:

```bash
dotnet add package Hyperbee.Migrations
```

## Credits

The Hyperbee Migrations API is heavily influenced by
[Fluent Migrator](https://github.com/schambers/fluentmigrator),
[Raven Migrations](https://github.com/migrating-ravens/RavenMigrations), and
[DbUp](https://github.com/DbUp/DbUp). Special thanks to:

- [Cronos](https://github.com/HangfireIO/Cronos) -- cron expression support
- [Couchbase .NET Client](https://github.com/couchbase/couchbase-net-client) -- Couchbase connectivity and DI extensions
- [Raven Migrations](https://github.com/migrating-ravens/RavenMigrations) -- migration pattern inspiration

## Contributing

We welcome contributions! See the
[repo-local CONTRIBUTING guide](https://github.com/Stillpoint-Software/hyperbee.migrations/blob/main/CONTRIBUTING.md)
for the development setup, coding conventions, and the provider-author DI
checklist (per ADR-0023).
