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

The resource runner resolves files relative to `Resources/{version}-{ClassName}/`. Statement files may use either the script form (`.statements`, recommended) or the JSON-array form (`.statements.json`, legacy). Document seed files always end in `.json`.

```
MyProject/
  Migrations/
    1000-CreateInitialSchema.cs
    2000-AddSecondaryIndexes.cs
  Resources/
    1000-CreateInitialSchema/
      statements.pql          <-- script form (recommended)
      sample/users/           <-- documents to seed
        user1.json
        user2.json
    2000-AddSecondaryIndexes/
      statements.pql          <-- script form (recommended)
    7000-ReversibleAlias/
      statements.pql          <-- Up script
      statements.down.pql     <-- Down script (reversible; OpenSearch)
    9000-LegacyExample/
      statements.json         <-- legacy JSON form (still supported)
```

## Embedding Resources in .csproj

Each resource file must be marked `<EmbeddedResource>`; reference it by exact name (including the extension).

```xml
<ItemGroup>
  <None Remove="Resources\1000-CreateInitialSchema\statements.pql" />
  <None Remove="Resources\1000-CreateInitialSchema\sample\users\user1.json" />
  <None Remove="Resources\7000-ReversibleAlias\statements.pql" />
  <None Remove="Resources\7000-ReversibleAlias\statements.down.pql" />
</ItemGroup>
<ItemGroup>
  <EmbeddedResource Include="Resources\1000-CreateInitialSchema\statements.pql" />
  <EmbeddedResource Include="Resources\1000-CreateInitialSchema\sample\users\user1.json" />
  <EmbeddedResource Include="Resources\7000-ReversibleAlias\statements.pql" />
  <EmbeddedResource Include="Resources\7000-ReversibleAlias\statements.down.pql" />
</ItemGroup>
```

## Statements (StatementsFromAsync)

Execute database-specific statements from a resource file. The four NoSQL
providers (Aerospike, Couchbase, MongoDB, OpenSearch) accept two file
formats; PostgreSQL uses plain `.sql` files via `AllSqlFromAsync` /
`SqlFromAsync` instead.

### Script form (`.pql`) -- recommended

The script form is the recommended shape for new migrations. Statements are
written one-per-line (or multi-line) inside a plain text file with the
`.pql` extension (*Provider Query Language* -- the extension is
grammar-neutral; the statement grammar itself is provider-specific).
Postgres also accepts its native `.sql`:

```
-- Secondary indexes for the application's queries.
CREATE UNIQUE INDEX idx_users_email ON sample.users (email);

-- Common filters used by the application.
CREATE INDEX idx_users_active ON sample.users (active);
CREATE INDEX idx_users_role   ON sample.users (role);
```

Lexical rules (universal across the four NoSQL providers):

- **Statement terminator: `;`** (required).
- **Comments:** `--` line, `//` line, `/* ... */` block. Block comments may nest.
- **Whitespace** is free in source; pick whichever indentation pattern is readable.
- **String literals** are provider-specific (single quotes, double quotes,
  backticks per the provider's native grammar).
- **Embedded JSON bodies** (OpenSearch only): `WITH BODY { ... }` is consumed
  as a brace-balanced block; the body block is not split at `;`. A `BODIES`
  header block at the top of the file declares named bodies that can be
  referenced by `$name`.

### JSON-array form (`.statements.json`) -- legacy, still supported

The original JSON-array wrapper format remains supported indefinitely:

```json
{
  "statements": [
    { "statement": "CREATE UNIQUE INDEX idx_users_email ON sample.users (email)" },
    { "statement": "CREATE INDEX idx_users_active ON sample.users (active)" }
  ]
}
```

Both forms parse to the same internal statement list. The resource runner
picks the form based on the file extension; the same migration can mix forms
across files (a `.pql` and a `.statements.json` in the same migration
folder both apply).

### Reversible migrations (`.down.pql`)

A reversible migration (one with a meaningful `DownAsync`) pairs its
`<name>.pql` Up script with a sibling `<name>.down.pql` Down script
(OpenSearch). `UpAsync` loads `<name>.pql`; `DownAsync` calls
`RollbackStatementsFromAsync(this, "<name>.down.pql")`. The down script is
a normal `.pql` script and is dispatched **in written order** -- you
author the explicit teardown sequence (typically the reverse of Up).

```
// 7000-ReversibleAlias/statements.pql
CREATE INDEX sample_audit_v1 IF NOT EXISTS;
ALIAS ADD sample_audit ON sample_audit_v1;

// 7000-ReversibleAlias/statements.down.pql  (explicit teardown order)
ALIAS REMOVE sample_audit ON sample_audit_v1;
DROP INDEX sample_audit_v1 IF EXISTS;
```

This differs from the legacy `.statements.json` form, where each entry
carries an inline `rollback` field and Down auto-reverses per entry; that
form remains supported for backward compatibility. If `<name>.down.pql`
is absent the runner refuses Down loudly
(`RollbackNotSupportedException`) before mutating anything. Squashes are
up-only, so generated squashes never carry a down script.

### Enable `.pql` syntax highlighting in VS Code

`.pql` is not a registered language, so VS Code shows it as plain text by
default. Map it to SQL highlighting (the closest generic fit for the
CREATE/DROP/index DDL these scripts contain) by adding to your
`.vscode/settings.json`:

```json
{
  "files.associations": { "*.pql": "sql" }
}
```

(`*.pql` also matches `*.down.pql`.) This is editor-only; it has no effect
on parsing or runtime behavior.

### Statement syntax by provider

The wrapping format (script vs JSON) is universal; the statement language
inside each statement is provider-specific:

- **Aerospike**: AQL-like grammar -- `CREATE INDEX WAIT idx_users_email ON test.users (email) STRING`
- **Couchbase**: N1QL grammar -- `CREATE INDEX idx_users_email ON sample(email) USING GSI`
- **MongoDB**: SQL-flavored DSL -- `CREATE UNIQUE INDEX idx_users_email ON sample.users (email)`. The grammar is intentionally narrow; for arbitrary MongoDB commands (aggregation pipelines, schema validation rules, time-series options) inject `IMongoClient` and use a code migration instead.
- **OpenSearch**: OpenSearch DSL -- `CREATE INDEX logs_v1 WITH BODY @logs_v1.json`, `ALIAS SWAP app FROM logs_v1 TO logs_v2`, `WAIT FOR YELLOW ON logs_v2`
- **PostgreSQL**: plain SQL -- `.sql` files invoked via `AllSqlFromAsync` (see below)

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
        // execute AQL statements (create indexes) -- script form
        await resourceRunner.StatementsFromAsync(["statements"], cancellationToken);

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
