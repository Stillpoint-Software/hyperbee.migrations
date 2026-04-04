---
layout: default
title: MongoDB Provider
nav_order: 9
---

# MongoDB Provider

The `Hyperbee.Migrations.Providers.MongoDB` package provides MongoDB support for Hyperbee Migrations.
It handles index management, collection setup, and document seeding through code and resource-based migrations.
For cross-cutting concepts like profiles, cron, and journaling, see [Concepts](concepts.md).

## Installation

```shell
dotnet add package Hyperbee.Migrations.Providers.MongoDB
```

## Configuration

Register the MongoDB client and migration services with the DI container:

```csharp
services.AddSingleton<IMongoClient>( new MongoClient( connectionString ) );

services.AddMongoDBMigrations( options =>
{
    options.DatabaseName = "migration";             // default
    options.CollectionName = "ledger";              // default
});
```

## Locking

The provider uses a MongoDB document-based distributed lock to prevent simultaneous migration runners.

```csharp
services.AddMongoDBMigrations( options =>
{
    options.LockingEnabled = true;                                  // default
    options.LockName = "ledger";                                    // lock document key
    options.LockMaxLifetime = TimeSpan.FromHours( 1 );             // max time-to-live
});
```

## Code Migration Example

Inject `IMongoClient` to interact with MongoDB directly:

```csharp
[Migration( 3000 )]
public class SeedData( IMongoClient mongoClient, ILogger<SeedData> logger ) : Migration
{
    public override async Task UpAsync( CancellationToken cancellationToken = default )
    {
        logger.LogInformation( "Seeding data via code migration" );

        var db = mongoClient.GetDatabase( "sample" );
        var collection = db.GetCollection<BsonDocument>( "users" );

        await collection.InsertOneAsync( new BsonDocument
        {
            { "name", "Bob Johnson" },
            { "email", "bob@example.com" },
            { "active", true }
        }, cancellationToken: cancellationToken );
    }
}
```

## Resource Migration Example

Use `MongoDBResourceRunner<T>` to execute embedded resource files:

```csharp
[Migration( 1000 )]
public class CreateInitialSchema( MongoDBResourceRunner<CreateInitialSchema> resourceRunner ) : Migration
{
    public override async Task UpAsync( CancellationToken cancellationToken = default )
    {
        await resourceRunner.StatementsFromAsync( [
            "statements.json"
        ], cancellationToken );

        await resourceRunner.DocumentsFromAsync( [
            "sample/users"
        ], cancellationToken );
    }
}
```

## Statement Syntax

Statements use MongoDB JavaScript command syntax inside a JSON wrapper.

```json
{
  "statements": [
    { "statement": "db.getSiblingDB('sample').users.createIndex({ email: 1 }, { name: 'idx_users_email' })" },
    { "statement": "db.getSiblingDB('sample').users.createIndex({ active: 1 }, { name: 'idx_users_active' })" }
  ]
}
```

## Document Format

Documents are JSON files stored at `database/collection/key.json`. The filename (without extension)
becomes the document identifier.

```
Resources/1000-CreateInitialSchema/
  statements.json
  sample/users/
    user1.json
    user2.json
```

Example document (`sample/users/user1.json`):

```json
{
  "name": "Alice Smith",
  "email": "alice@example.com",
  "active": true
}
```

## Provider Options Reference

| Option           | Type       | Default      |
|------------------|------------|--------------|
| DatabaseName     | string     | "migration"  |
| CollectionName   | string     | "ledger"     |
| LockName         | string     | "ledger"     |
| LockMaxLifetime  | TimeSpan   | 1 hour       |
| LockingEnabled   | bool       | true         |
