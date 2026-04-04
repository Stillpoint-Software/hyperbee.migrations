# Hyperbee.MigrationRunner.Postgres

Command-line migration runner for PostgreSQL. Loads migration assemblies at runtime and executes pending migrations against a PostgreSQL instance.

## Prerequisites

- .NET 10 SDK
- A running PostgreSQL instance

## Configuration

Configure via `appsettings.json` or environment variables:

| Key | Description | Default |
|-----|-------------|---------|
| `Postgresql:ConnectionString` | PostgreSQL connection string | |
| `Migrations:SchemaName` | Schema for migration records | `migration` |
| `Migrations:TableName` | Table for migration records | `ledger` |
| `Migrations:Lock:Enabled` | Enable distributed locking | `false` |
| `Migrations:FromPaths` | Assembly file paths to load migrations from | |
| `Migrations:FromAssemblies` | Assembly names to load migrations from | |

## Running Locally

```bash
dotnet run
```

## Running with Docker

```bash
docker build -t postgres-migrations -f Dockerfile ../..
docker run postgres-migrations
```

## CLI Arguments

| Flag | Description |
|------|-------------|
| `-f`, `--file` | Migration assembly paths |
| `-a`, `--assembly` | Migration assembly names |
| `-p`, `--profile` | Migration profiles |
| `-s`, `--schema` | PostgreSQL schema name |
| `-t`, `--table` | PostgreSQL table name |
| `-cs`, `--connection` | PostgreSQL connection string |

## Sample Migrations

This runner loads migrations from the companion `Hyperbee.Migrations.Postgres.Samples` project via the `FromPaths` configuration. See `../samples/Hyperbee.Migrations.Postgres.Samples/` for example migrations.
