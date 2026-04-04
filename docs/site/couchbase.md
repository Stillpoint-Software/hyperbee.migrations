---
layout: default
title: Couchbase Provider
nav_order: 8
---

# Couchbase Provider

The `Hyperbee.Migrations.Providers.Couchbase` package provides Couchbase support for Hyperbee Migrations.
It manages buckets, scopes, collections, indexes, and document seeding through code and resource-based migrations.
For cross-cutting concepts like profiles, cron, and journaling, see [Concepts](concepts.md).

## Installation

```shell
dotnet add package Hyperbee.Migrations.Providers.Couchbase
```

## Configuration

Register Couchbase services and migration services with the DI container:

```csharp
services.AddCouchbase( options =>
{
    options.ConnectionString = "couchbase://localhost";
    options.UserName = "Administrator";
    options.Password = "password";
});

services.AddCouchbaseMigrations( options =>
{
    options.BucketName = "sample";                  // required
    options.ScopeName = "migrations";               // default
    options.CollectionName = "ledger";              // default
});
```

| Option                  | Type       | Default        |
|-------------------------|------------|----------------|
| BucketName              | string     | (required)     |
| ScopeName               | string     | "migrations"   |
| CollectionName          | string     | "ledger"       |
| ClusterReadyTimeout     | TimeSpan   | 5 minutes      |
| ProvisionRetryInterval  | TimeSpan   | 1 second       |
| ProvisionAttempts       | int        | 30             |

## Locking

The provider uses a Couchbase document-based distributed lock with automatic renewal.

```csharp
services.AddCouchbaseMigrations( options =>
{
    options.LockingEnabled = true;                                  // default
    options.LockName = "migration_lock";                            // lock document key
    options.LockMaxLifetime = TimeSpan.FromHours( 1 );             // max time-to-live
    options.LockExpireInterval = TimeSpan.FromMinutes( 5 );        // expire heartbeat
    options.LockRenewInterval = TimeSpan.FromMinutes( 2 );         // renewal heartbeat
});
```

## Code Migration Example

Inject `IClusterProvider` to interact with Couchbase directly:

```csharp
[Migration( 3000 )]
public class SeedData( IClusterProvider clusterProvider, ILogger<SeedData> logger ) : Migration
{
    public override async Task UpAsync( CancellationToken cancellationToken = default )
    {
        logger.LogInformation( "Seeding data via code migration" );

        var cluster = await clusterProvider.GetClusterAsync();
        var bucket = await cluster.BucketAsync( "sample" );
        var collection = bucket.DefaultCollection();

        await collection.UpsertAsync( "user-003", new
        {
            name = "Bob Johnson",
            email = "bob@example.com",
            active = true
        } );
    }
}
```

## Resource Migration Example

Use `CouchbaseResourceRunner<T>` to execute embedded resource files:

```csharp
[Migration( 1000 )]
public class CreateInitialSchema( CouchbaseResourceRunner<CreateInitialSchema> resourceRunner ) : Migration
{
    public override async Task UpAsync( CancellationToken cancellationToken = default )
    {
        await resourceRunner.StatementsFromAsync( [
            "statements.json",
            "sample/statements.json"
        ], cancellationToken );

        await resourceRunner.DocumentsFromAsync( [
            "sample/_default"
        ], cancellationToken );
    }
}
```

## Statement Syntax

Statements use N1QL (SQL++) syntax inside a JSON wrapper. Supported operations include
`CREATE BUCKET`, `CREATE SCOPE`, `CREATE COLLECTION`, `CREATE INDEX`, `CREATE PRIMARY INDEX`,
and `BUILD INDEX`.

```json
{
  "statements": [
    { "statement": "CREATE PRIMARY INDEX ON sample USING GSI" },
    { "statement": "CREATE INDEX idx_email ON sample(email) USING GSI WITH {'defer_build':true}" },
    { "statement": "BUILD INDEX ON sample(idx_email)" }
  ]
}
```

## Document Format

Documents are JSON files stored at `bucket/scope/key.json`. The filename (without extension) becomes
the document key in Couchbase.

```
Resources/1000-CreateInitialSchema/
  statements.json
  sample/_default/
    ccuser001.json
    ccuser002.json
```

Example document (`sample/_default/ccuser001.json`):

```json
{
  "name": "Alice Smith",
  "email": "alice@example.com",
  "active": true
}
```

## Provider Options Reference

| Option                  | Type       | Default        |
|-------------------------|------------|----------------|
| BucketName              | string     | (required)     |
| ScopeName               | string     | "migrations"   |
| CollectionName          | string     | "ledger"       |
| ClusterReadyTimeout     | TimeSpan   | 5 minutes      |
| ProvisionRetryInterval  | TimeSpan   | 1 second       |
| ProvisionAttempts       | int        | 30             |
| LockName                | string     | "migration_lock" |
| LockMaxLifetime         | TimeSpan   | 1 hour         |
| LockExpireInterval      | TimeSpan   | 5 minutes      |
| LockRenewInterval       | TimeSpan   | 2 minutes      |
| LockingEnabled          | bool       | true           |
