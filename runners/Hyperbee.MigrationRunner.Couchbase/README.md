# Hyperbee.MigrationRunner.Couchbase

Command-line migration runner for Couchbase. Loads migration assemblies at runtime and executes pending migrations against a Couchbase instance.

## Prerequisites

- .NET 10 SDK
- A running Couchbase instance

## Configuration

Configure via `appsettings.json` or environment variables:

| Key | Description | Default |
|-----|-------------|---------|
| `Couchbase:ConnectionString` | Couchbase cluster connection string | |
| `Couchbase:UserName` | Couchbase username | |
| `Couchbase:Password` | Couchbase password | |
| `Migrations:BucketName` | Bucket for migration records | `hyperbee` |
| `Migrations:ScopeName` | Scope for migration records | `migrations` |
| `Migrations:CollectionName` | Collection for migration records | `ledger` |
| `Migrations:Lock:Enabled` | Enable distributed locking | `false` |
| `Migrations:FromPaths` | Assembly file paths to load migrations from | |
| `Migrations:FromAssemblies` | Assembly names to load migrations from | |

## Running Locally

```bash
dotnet run
```

## Running with Docker

```bash
docker build -t couchbase-migrations -f Dockerfile ../..
docker run couchbase-migrations
```

## CLI Arguments

| Flag | Description |
|------|-------------|
| `-f`, `--file` | Migration assembly paths |
| `-a`, `--assembly` | Migration assembly names |
| `-p`, `--profile` | Migration profiles |
| `-b`, `--bucket` | Couchbase bucket name |
| `-s`, `--scope` | Couchbase scope name |
| `-c`, `--collection` | Couchbase collection name |
| `-usr`, `--user` | Couchbase username |
| `-pwd`, `--password` | Couchbase password |
| `-cs`, `--connection` | Couchbase connection string |

## Sample Migrations

This runner loads migrations from the companion `Hyperbee.Migrations.Couchbase.Samples` project via the `FromPaths` configuration. See `../samples/Hyperbee.Migrations.Couchbase.Samples/` for example migrations.
