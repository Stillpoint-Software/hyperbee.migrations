# Hyperbee.MigrationRunner.Aerospike

Command-line migration runner for Aerospike. Loads migration assemblies at runtime and executes pending migrations against an Aerospike instance.

## Prerequisites

- .NET 10 SDK
- A running Aerospike instance

## Configuration

Configure via `appsettings.json` or environment variables:

| Key | Description | Default |
|-----|-------------|---------|
| `Aerospike:ConnectionString` | Aerospike host and port | `localhost:3000` |
| `Migrations:Namespace` | Aerospike namespace for migration records | `test` |
| `Migrations:MigrationSet` | Set name for migration records | `SchemaMigrations` |
| `Migrations:Lock:Enabled` | Enable distributed locking | `false` |
| `Migrations:FromPaths` | Assembly file paths to load migrations from | |
| `Migrations:FromAssemblies` | Assembly names to load migrations from | |

## Running Locally

```bash
dotnet run
```

## Running with Docker

```bash
docker build -t aerospike-migrations -f Dockerfile ../..
docker run aerospike-migrations
```

## CLI Arguments

| Flag | Description |
|------|-------------|
| `-f`, `--file` | Migration assembly paths |
| `-a`, `--assembly` | Migration assembly names |
| `-p`, `--profile` | Migration profiles |
| `-n`, `--namespace` | Aerospike namespace |
| `-s`, `--set` | Aerospike migration set name |
| `-cs`, `--connection` | Aerospike connection string |

## Sample Migrations

This runner loads migrations from the companion `Hyperbee.Migrations.Aerospike.Samples` project via the `FromPaths` configuration. See `../samples/Hyperbee.Migrations.Aerospike.Samples/` for example migrations.
