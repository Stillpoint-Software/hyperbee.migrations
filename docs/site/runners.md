---
layout: default
title: Runners
nav_order: 6
---

# Runners

## Running Migrations -- Two Approaches

### 1. In-Process

Run migrations at application startup as part of your app's initialization:

```csharp
var runner = app.Services.GetRequiredService<MigrationRunner>();
await runner.RunAsync(cancellationToken);
```

Best for: development, simple deployments, applications that own their database.

### 2. Standalone Runner

A dedicated console application that runs migrations independently. The project
includes reference runners for each provider under `runners/`.

Best for: CI/CD pipelines, Docker-based deployments, separation of concerns.

## Runner Configuration

All standalone runners share the same `appsettings.json` structure:

```json
{
  "Migrations": {
    "FromPaths": ["path/to/migrations.dll"],
    "FromAssemblies": [],
    "Lock": {
      "Enabled": true,
      "Name": "migration_lock",
      "MaxLifetime": 3600
    }
  }
}
```

### Assembly Discovery

Migrations are discovered from multiple sources, and all sources are merged:

- **FromPaths** -- loads DLLs by file path. Useful when migration assemblies are
  deployed alongside the runner.
- **FromAssemblies** -- loads by assembly name. Useful when assemblies are already
  on the probing path.
- **options.Assemblies** -- adds assemblies directly in code during DI registration.

## MigrationRunner Options

| Option | Description |
|--------|-------------|
| Direction | `Up` (default) or `Down` |
| Profiles | List of active profiles to run |
| ToVersion | Stop at a specific migration version |
| LockingEnabled | Enable distributed locking |

## CLI Reference

### Shared Flags (All Runners)

| Flag | Long Form | Description |
|------|-----------|-------------|
| `-f` | `--file` | Migration assembly paths (repeatable) |
| `-a` | `--assembly` | Migration assembly names (repeatable) |
| `-p` | `--profile` | Active profiles |
| `-cs` | `--connection` | Database connection string |

### Provider-Specific Flags

| Flag | Long Form | Provider | Description |
|------|-----------|----------|-------------|
| `-n` | `--namespace` | Aerospike | Namespace |
| `-s` | `--set` | Aerospike | Migration set |
| `-b` | `--bucket` | Couchbase | Bucket name |
| `-s` | `--scope` | Couchbase | Scope name |
| `-c` | `--collection` | Couchbase | Collection name |
| `-usr` | `--user` | Couchbase | Username |
| `-pwd` | `--password` | Couchbase | Password |
| `-d` | `--database` | MongoDB | Database name |
| `-v` | `--collection` | MongoDB | Collection name |
| `-s` | `--schema` | Postgres | Schema name |
| `-t` | `--table` | Postgres | Table name |

**Note:** The `-s` flag has different meanings per provider.

## Docker Support

Each runner includes a Dockerfile for containerized execution:

```bash
docker build -t my-migrations -f runners/Hyperbee.MigrationRunner.Postgres/Dockerfile .
docker run -e "Postgresql__ConnectionString=..." my-migrations
```

Environment variables override `appsettings.json` using the standard .NET
double-underscore convention. For example, `Migrations__Lock__Enabled=true`
overrides the `Lock.Enabled` setting in configuration.
