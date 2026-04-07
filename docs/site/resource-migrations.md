---
layout: default
title: Resource Migrations
nav_order: 6
---

# Resource Migrations

Resource-based migrations load and execute embedded resource files instead of inline C# code.

## Overview

- Resource migrations use embedded files (SQL, JSON, N1QL, AQL) instead of inline C# code
- Changes are declared in resource files, making them reviewable and diffable
- Each provider has a `ResourceRunner<TMigration>` that loads and executes resources
- Resource files are embedded in the assembly at build time

## How It Works

1. Create resource files in a `Resources/{version}-{ClassName}/` folder
2. Add them as `EmbeddedResource` in the .csproj
3. Inject the provider's resource runner into your migration
4. Call `StatementsFromAsync` or `DocumentsFromAsync` (or `SqlFromAsync`/`AllSqlFromAsync` for Postgres)

## Folder Convention

The resource runner resolves files relative to `Resources/{version}-{ClassName}/`:

```
MyProject/
  Migrations/
    1000-CreateInitialSchema.cs
    2000-AddSecondaryIndexes.cs
  Resources/
    1000-CreateInitialSchema/
      statements.json         <-- statements to execute
      sample/users/           <-- documents to seed
        user1.json
        user2.json
    2000-AddSecondaryIndexes/
      statements.json
```

## Embedding Resources in .csproj

```xml
<ItemGroup>
  <None Remove="Resources\1000-CreateInitialSchema\statements.json" />
  <None Remove="Resources\1000-CreateInitialSchema\sample\users\user1.json" />
</ItemGroup>
<ItemGroup>
  <EmbeddedResource Include="Resources\1000-CreateInitialSchema\statements.json" />
  <EmbeddedResource Include="Resources\1000-CreateInitialSchema\sample\users\user1.json" />
</ItemGroup>
```

## Statements (StatementsFromAsync)

Execute database-specific statements from a JSON file.

**JSON Format:**

```json
{
  "statements": [
    { "statement": "CREATE INDEX IF NOT EXISTS idx_users_email ON sample.users (email)" },
    { "statement": "CREATE INDEX IF NOT EXISTS idx_users_active ON sample.users (active)" }
  ]
}
```

The statement syntax is provider-specific:

- **Aerospike**: AQL syntax -- `CREATE INDEX WAIT idx_users_email ON test.users (email) STRING`
- **Couchbase**: N1QL syntax -- `CREATE INDEX idx_users_email ON sample(email) USING GSI`
- **MongoDB**: JavaScript syntax -- `db.getSiblingDB('sample').users.createIndex({ email: 1 }, { name: 'idx_users_email' })`
- **PostgreSQL**: Uses `SqlFromAsync`/`AllSqlFromAsync` with plain `.sql` files instead of JSON

## Documents (DocumentsFromAsync)

Seed data by loading JSON documents into the database. The folder path beneath
the migration resource folder maps to the target location in the database:

```
Resources/<migration>/<database>/<container>/<document>.json
```

Each provider interprets the path segments according to its own terminology:

| Segment | Aerospike | Couchbase | MongoDB |
|---------|-----------|-----------|---------|
| `<database>` | namespace | bucket | database |
| `<container>` | set | scope | collection |
| `<document>` | record key | document key | document |

**Aerospike:**

```
Resources/1000-CreateInitialSchema/test/users/user1.json
                                   |    |     |
                                   |    |     +-- record key
                                   |    +-- set
                                   +-- namespace
```

**Couchbase:**

```
Resources/1000-CreateInitialSchema/sample/_default/ccuser001.json
                                   |      |        |
                                   |      |        +-- document key
                                   |      +-- scope
                                   +-- bucket
```

**MongoDB:**

```
Resources/1000-CreateInitialSchema/sample/users/user1.json
                                   |      |     |
                                   |      |     +-- document
                                   |      +-- collection
                                   +-- database
```

## PostgreSQL Resource Migrations

PostgreSQL uses plain SQL files instead of JSON statements:

```csharp
[Migration(1000)]
public class CreateInitialSchema(PostgresResourceRunner<CreateInitialSchema> resourceRunner) : Migration
{
    public override async Task UpAsync(CancellationToken cancellationToken = default)
    {
        await resourceRunner.AllSqlFromAsync(cancellationToken);
    }
}
```

Methods:

- `AllSqlFromAsync()` -- executes ALL .sql files in the migration's resource folder
- `SqlFromAsync(["CreateSchema.sql"])` -- executes specific named SQL files

## Example: Complete Resource Migration

```csharp
[Migration(1000)]
public class CreateInitialSchema(AerospikeResourceRunner<CreateInitialSchema> resourceRunner) : Migration
{
    public override async Task UpAsync(CancellationToken cancellationToken = default)
    {
        // execute AQL statements (create indexes)
        await resourceRunner.StatementsFromAsync(["statements.json"], cancellationToken);

        // seed documents into test.users set
        await resourceRunner.DocumentsFromAsync(["test/users"], cancellationToken);
    }
}
```

## When to Use Resource vs Code Migrations

| Consideration | Resource | Code |
|---|---|---|
| Schema/DDL changes | Preferred -- declarative, reviewable | Works but verbose |
| Data seeding | Good for static seed data | Better for computed/dynamic data |
| Complex logic | Not suitable | Required |
| Database client access | Not available | Full DI access |
| Reviewability | SQL/JSON files are easy to diff | C# code requires more context |
