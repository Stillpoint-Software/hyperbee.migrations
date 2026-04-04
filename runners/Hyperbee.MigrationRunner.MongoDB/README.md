# Hyperbee.MigrationRunner.MongoDB

Command-line migration runner for MongoDB. Loads migration assemblies at runtime and executes pending migrations against a MongoDB instance.

## Prerequisites

- .NET 10 SDK
- A running MongoDB instance

## Configuration

Configure via `appsettings.json` or environment variables:

| Key | Description | Default |
|-----|-------------|---------|
| `MongoDb:ConnectionString` | MongoDB connection string | |
| `Migrations:DatabaseName` | Database for migration records | `migration` |
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
docker build -t mongodb-migrations -f Dockerfile ../..
docker run mongodb-migrations
```

## CLI Arguments

| Flag | Description |
|------|-------------|
| `-f`, `--file` | Migration assembly paths |
| `-a`, `--assembly` | Migration assembly names |
| `-p`, `--profile` | Migration profiles |
| `-d`, `--database` | MongoDB database name |
| `-v`, `--collection` | MongoDB collection name |
| `-cs`, `--connection` | MongoDB connection string |

## Sample Migrations

This runner loads migrations from the companion `Hyperbee.Migrations.MongoDB.Samples` project via the `FromPaths` configuration. See `../samples/Hyperbee.Migrations.MongoDB.Samples/` for example migrations.
