# Plan: Resource Migrations & Aerospike Provider

## Process

Each task follows the ITRV loop: **Implement, Test, Reflect, Verify.**
Verification: `dotnet build` zero errors/warnings, `dotnet test` all pass, plan updated.

## Objective

**Goal:** Add resource migration support (StatementsFromAsync) to MongoDB, create a new Aerospike provider with full resource migration support, and expand unit test coverage across all new code.

**Success Criteria:**
- MongoDB provider supports `StatementsFromAsync` with Parlot-based parser for CREATE/DROP COLLECTION, CREATE/DROP INDEX
- New Aerospike provider with record store, resource runner, Parlot-based AQL parser, async index handling, and directive support (`@RECREATE`, `@WAITREADY`)
- Unit tests cover all parsers, resource runners, and edge cases
- All providers follow a consistent resource migration pattern
- Solution builds cleanly, all tests pass

**Constraints:**
- .NET 10/9/8 multi-targeting (existing `Directory.Build.props`)
- MSTest framework (existing)
- Central package management (`Directory.Packages.props`)
- Parlot for new parsers — see [ADR-0001](../../decisions/0001-parlot-for-statement-parsers.md)
- Standard JSON resource format — see [ADR-0002](../../decisions/0002-resource-migration-pattern.md)

**Tech Stack:** C#, .NET 10/9/8, MSTest, Parlot, Testcontainers, NuGet (Aerospike client)

## Style Reference

*Filled during Phase 0.*

- **Naming:** PascalCase types/methods, camelCase locals, `_camelCase` private fields
- **Async:** All async methods suffixed `Async`, return `Task`/`Task<T>`, accept `CancellationToken`
- **DI Pattern:** `ServiceCollectionExtensions.Add{Provider}Migrations()` registers options (singleton), record store (singleton), runner (singleton), resource runner (transient)
- **Resource Runner Pattern:** Generic `ResourceRunner<TMigration> where TMigration : Migration`, constructor-injected client + logger, overloaded methods for single/array/timeout variants
- **Parser Pattern:** Standalone class with `Parse*` method, returns record types
- **Record types:** Used for immutable data (StatementItem, KeyspaceRef, DocumentItem, Location)
- **Tests:** `[TestClass]` + `[TestMethod]`, Assert-based (not FluentAssertions in unit tests), one test class per concern
- **Project structure:** `src/Hyperbee.Migrations.Providers.{Name}/`, mirrors: `Parsers/`, `Resources/`, `Extensions/`
- **ConfigureAwait:** `.ConfigureAwait(false)` on all awaits in library code
- **Logging:** `ILogger<TMigration>` injected, structured logging with `LogInformation`/`LogError`

## Git Workflow

| Phase | Snapshot Tag | Branch |
|-------|-------------|--------|
| Phase 0 | `plan/resource-migrations/phase-0` | `resource-migrations` |
| Phase 1 | `plan/resource-migrations/phase-1` | `resource-migrations` |
| Phase 2 | `plan/resource-migrations/phase-2` | `resource-migrations` |
| Phase 3 | `plan/resource-migrations/phase-3` | `resource-migrations` |

---

## Phase 0: Infrastructure & Codebase Audit

**Goal:** Verify test infrastructure, audit existing patterns, establish branch.

**Completion Criteria:** Tests run, patterns documented in Style Reference, branch created.

- [x] Verify `dotnet build` succeeds for entire solution
- [x] Verify `dotnet test` runs and passes for `Hyperbee.Migrations.Tests`
- [x] Audit existing provider patterns (Couchbase, MongoDB, Postgres resource runners)
- [x] Audit existing parser patterns (StatementParser, KeyspaceParser)
- [x] Document Style Reference (above)
- [ ] Create plan branch
- [ ] Initial commit with plan and ADRs

**Phase 0 Completion:**
- Snapshot: `plan/resource-migrations/phase-0`
- Summary: _pending_

---

## Phase 1: MongoDB Statement Parser & Resource Runner Enhancement

**Goal:** MongoDB provider supports `StatementsFromAsync` with a Parlot-based parser that handles CREATE/DROP COLLECTION, CREATE/DROP INDEX, and INSERT operations. Fully unit-tested.

**Prerequisites:** Phase 0 complete, Parlot NuGet available.

**Testing Strategy:** Unit tests for parser (all statement types, error cases, case insensitivity), unit tests for resource runner statement loading.

**Decisions:** [ADR-0001](../../decisions/0001-parlot-for-statement-parsers.md), [ADR-0002](../../decisions/0002-resource-migration-pattern.md)

**Completion Criteria:**
- [ ] MongoDB parser handles all statement types
- [ ] `MongoDBResourceRunner.StatementsFromAsync` works end-to-end
- [ ] All unit tests pass
- [ ] Zero build warnings

### Task 1.1: Add Parlot Package & MongoDB Statement Types

**Description:** Add Parlot NuGet reference to MongoDB provider. Define `MongoStatementType` enum and `MongoStatementItem` record for parsed results.

**Implementation strategy:** Follow the Couchbase `StatementType` enum and `StatementItem` record pattern. Place in `Parsers/` directory within the MongoDB provider project.

**Prerequisites:** Parlot version selected and added to `Directory.Packages.props`.

**Completion Criteria:** Project builds with Parlot reference, types compile.

**Subtasks:**
- [ ] Add `Parlot` to `Directory.Packages.props` with version
- [ ] Add `<PackageReference Include="Parlot" />` to `Hyperbee.Migrations.Providers.MongoDB.csproj`
- [ ] Create `src/Hyperbee.Migrations.Providers.MongoDB/Parsers/MongoStatementType.cs` — enum: `CreateCollection`, `DropCollection`, `CreateIndex`, `CreateUniqueIndex`, `DropIndex`, `Insert`
- [ ] Create `src/Hyperbee.Migrations.Providers.MongoDB/Parsers/MongoStatementItem.cs` — record with `StatementType`, `Statement`, `DatabaseName`, `CollectionName`, `IndexName`, `FieldNames`, `Expression`

`Status: Not Started`

### Task 1.2: MongoDB Parlot Statement Parser

**Description:** Implement `MongoStatementParser` using Parlot fluent API to parse pseudo-MongoDB DDL statements from JSON resource files.

**Implementation strategy:** Use Parlot `Terms.Keyword()` for SQL-like keywords (CREATE, DROP, INDEX, COLLECTION, ON, UNIQUE), `Terms.Identifier()` for names, `Separated()` for field lists. Define grammar as composed parsers with `OneOf()` at the top level. Follow the XS parser pattern of modular parser composition.

**Supported statements:**
```
CREATE COLLECTION database.collection
DROP COLLECTION database.collection
CREATE INDEX index_name ON database.collection(field1, field2)
CREATE UNIQUE INDEX index_name ON database.collection(field1, field2)
DROP INDEX index_name ON database.collection
INSERT INTO database.collection
```

**Prerequisites:** Task 1.1 complete.

**Completion Criteria:** Parser correctly parses all statement types, throws on invalid input.

**Test strategy:** Parameterized tests for each statement type, case insensitivity, backtick-quoted identifiers, error cases.

**Subtasks:**
- [ ] Create `src/Hyperbee.Migrations.Providers.MongoDB/Parsers/MongoStatementParser.cs` with Parlot grammar
- [ ] Implement `ParseStatement(string statement)` returning `MongoStatementItem`
- [ ] Handle dotted `database.collection` identifier parsing
- [ ] Handle parenthesized field lists for index creation
- [ ] Handle backtick-escaped identifiers
- [ ] Create `tests/Hyperbee.Migrations.Tests/MongoStatementParserTests.cs` with tests:
  - CREATE COLLECTION (simple, backtick-quoted)
  - DROP COLLECTION
  - CREATE INDEX (single field, multi-field)
  - CREATE UNIQUE INDEX
  - DROP INDEX
  - INSERT INTO
  - Case insensitivity (mixed case keywords)
  - Error cases (unknown statement, missing collection name, malformed)

`Status: Not Started`

### Task 1.3: Enhance MongoDBResourceRunner with StatementsFromAsync

**Description:** Add `StatementsFromAsync` methods to `MongoDBResourceRunner<TMigration>` that load JSON statement resources, parse them with `MongoStatementParser`, and execute the corresponding MongoDB operations.

**Implementation strategy:** Follow the Couchbase `CouchbaseResourceRunner.StatementsFromAsync` pattern exactly: load JSON, extract `statements[]` array, parse each, switch on statement type, execute via MongoDB driver. Add `ThrowIfNoResourceLocationFor()` validation. Add overloads matching Couchbase pattern (single name, array, with timeout).

**Prerequisites:** Task 1.2 complete.

**Completion Criteria:** `StatementsFromAsync` loads and executes statements. Unit tests cover statement execution dispatch.

**Test strategy:** Unit tests with mocked `IMongoClient` verifying correct driver methods are called for each statement type.

**Subtasks:**
- [ ] Add `StatementsFromAsync` overloads to `MongoDBResourceRunner<TMigration>` (single, array, with timeout)
- [ ] Implement statement execution switch: CreateCollection → `db.CreateCollectionAsync()`, DropCollection → `db.DropCollectionAsync()`, CreateIndex → `collection.Indexes.CreateOneAsync()`, CreateUniqueIndex → `CreateOneAsync` with unique option, DropIndex → `collection.Indexes.DropOneAsync()`, Insert → `InsertOneAsync`
- [ ] Add `ThrowIfNoResourceLocationFor()` validation (same as Couchbase)
- [ ] Add timeout/cancellation token handling (same pattern as Couchbase)
- [ ] Create `tests/Hyperbee.Migrations.Tests/MongoResourceRunnerTests.cs` — test statement loading from embedded JSON

`Status: Not Started`

**Phase 1 Completion:**
- Snapshot: `plan/resource-migrations/phase-1`
- Summary: _pending_
- Learnings: _pending_

---

## Phase 2: Aerospike Provider — Core Infrastructure

**Goal:** New Aerospike provider with record store, migration options, DI registration, and basic migration support. No resource runner yet — that's Phase 3.

**Prerequisites:** Phase 1 complete (establishes Parlot parser pattern).

**Testing Strategy:** Unit tests for record store logic, options, parser. Integration tests deferred (Testcontainers for Aerospike).

**Completion Criteria:**
- [ ] Aerospike provider project builds
- [ ] Record store implements `IMigrationRecordStore`
- [ ] DI registration follows established pattern
- [ ] Unit tests pass

### Task 2.1: Aerospike Provider Project Scaffold

**Description:** Create the Aerospike provider project with correct structure, NuGet references, and multi-targeting.

**Implementation strategy:** Mirror the MongoDB provider project structure. Use `AspiranteDb.Client` (or `Aerospike.Client`) NuGet. Add to solution file.

**Prerequisites:** Identify correct Aerospike .NET client NuGet package.

**Completion Criteria:** Project compiles, is part of the solution.

**Subtasks:**
- [ ] Determine Aerospike .NET client NuGet package name and version
- [ ] Add Aerospike client to `Directory.Packages.props`
- [ ] Create `src/Hyperbee.Migrations.Providers.Aerospike/Hyperbee.Migrations.Providers.Aerospike.csproj` with references to `Hyperbee.Migrations`, Aerospike client, Parlot
- [ ] Create directory structure: `Parsers/`, `Resources/`, `Extensions/`
- [ ] Add project to solution file
- [ ] Verify `dotnet build` succeeds

`Status: Not Started`

### Task 2.2: Aerospike Migration Options & Record Store

**Description:** Implement `AerospikeMigrationOptions` and `AerospikeRecordStore` for migration tracking in Aerospike.

**Implementation strategy:** Follow MongoDB's `MongoDBRecordStore` pattern. Aerospike tracking uses a dedicated set (like the bash script's `SchemaMigrations` set) within a namespace. Records store PK=recordId, bins: Name, ExecutedAt. Locking uses a record with advisory lock pattern (similar to MongoDB's expiration-based locking).

**Prerequisites:** Task 2.1 complete.

**Completion Criteria:** Record store compiles, implements full `IMigrationRecordStore` interface.

**Test strategy:** Unit tests verifying options defaults, record store initialization logic.

**Subtasks:**
- [ ] Create `AerospikeMigrationOptions.cs` — properties: `Namespace`, `MigrationSet` (default "SchemaMigrations"), `LockName` (default "migration_lock"), `LockMaxLifetime` (default 1 hour), `ConnectionString`
- [ ] Create `AerospikeRecordStore.cs` implementing `IMigrationRecordStore`:
  - `InitializeAsync()` — verify namespace connectivity
  - `ExistsAsync(recordId)` — check if record exists in migration set
  - `WriteAsync(recordId)` — put record with Name and ExecutedAt bins
  - `DeleteAsync(recordId)` — delete record from migration set
  - `CreateLockAsync()` — advisory lock using a dedicated record with expiration (TTL)
- [ ] Create `tests/Hyperbee.Migrations.Tests/AerospikeOptionsTests.cs` — test defaults and Deconstruct

`Status: Not Started`

### Task 2.3: Aerospike DI Registration

**Description:** Implement `ServiceCollectionExtensions.AddAerospikeMigrations()` for dependency injection.

**Implementation strategy:** Follow the MongoDB `ServiceCollectionExtensions` pattern exactly. Register options, record store, runner, resource runner.

**Prerequisites:** Task 2.2 complete.

**Completion Criteria:** DI registration compiles, follows established pattern.

**Subtasks:**
- [ ] Create `ServiceCollectionExtensions.cs` with `AddAerospikeMigrations()` overloads
- [ ] Register: `AerospikeMigrationOptions` (singleton factory), `IMigrationRecordStore` → `AerospikeRecordStore` (singleton), `MigrationRunner` (singleton), `AerospikeResourceRunner<>` (transient)
- [ ] Support assembly loading from configuration (`Migrations:FromAssemblies`, `Migrations:FromPaths`)

`Status: Not Started`

**Phase 2 Completion:**
- Snapshot: `plan/resource-migrations/phase-2`
- Summary: _pending_
- Learnings: _pending_

---

## Phase 3: Aerospike AQL Parser & Resource Runner

**Goal:** Aerospike provider has a Parlot-based AQL parser with directive support, a resource runner handling async index creation, and comprehensive unit tests.

**Prerequisites:** Phase 2 complete (provider infrastructure), Phase 1 complete (Parlot parser pattern established).

**Testing Strategy:** Extensive unit tests for parser (all statement types, directives, async index corner cases, error messages). Unit tests for resource runner dispatch logic.

**Decisions:** Directive metadata (`@RECREATE`, `@WAITREADY`) stored in JSON statement format as adjacent properties.

**Completion Criteria:**
- [ ] AQL parser handles all statement types and directives
- [ ] Resource runner handles async index creation with wait-for-ready
- [ ] All unit tests pass
- [ ] Zero build warnings

### Task 3.1: Aerospike Statement Types & Directive Model

**Description:** Define the Aerospike statement types, directives, and parsed result records.

**Implementation strategy:** Statement types from the bash script: CREATE INDEX, DROP INDEX, INSERT INTO, DELETE FROM, plus CREATE SET (pseudo-AQL). Directives (`@RECREATE`, `@WAITREADY`) modeled as properties on the statement item rather than separate entities. JSON format extends the standard:
```json
{
  "statements": [
    {
      "statement": "CREATE INDEX idx_email ON test.users (email) STRING",
      "recreate": true,
      "waitReady": true
    }
  ]
}
```

**Prerequisites:** Task 2.1 complete.

**Completion Criteria:** Types compile, directives are part of the model.

**Subtasks:**
- [ ] Create `Parsers/AerospikeStatementType.cs` — enum: `CreateIndex`, `CreateStringIndex`, `CreateNumericIndex`, `CreateGeo2DSphereIndex`, `DropIndex`, `CreateSet`, `Insert`, `Delete`
- [ ] Create `Parsers/AerospikeStatementItem.cs` — record with `StatementType`, `Statement`, `Namespace`, `SetName`, `IndexName`, `BinName`, `IndexType` (STRING/NUMERIC/GEO2DSPHERE), `Recreate` (bool), `WaitReady` (bool), `Expression`
- [ ] Create `Parsers/AerospikeIndexType.cs` — enum: `String`, `Numeric`, `Geo2DSphere`

`Status: Not Started`

### Task 3.2: Aerospike Parlot AQL Parser

**Description:** Implement `AerospikeStatementParser` using Parlot to parse AQL-like statements.

**Implementation strategy:** Use Parlot fluent API. AQL syntax:
```
CREATE INDEX index_name ON namespace.set (bin_name) STRING|NUMERIC|GEO2DSPHERE
DROP INDEX namespace index_name
INSERT INTO namespace.set (PK, bin1, bin2) VALUES ('key', 'val1', 123)
DELETE FROM namespace.set WHERE PK = 'key'
CREATE SET namespace.set
```

Key Parlot patterns: `Terms.Keyword()` for AQL keywords, `Terms.Identifier()` for names, `Between()` for parenthesized lists, `OneOf()` for index type alternatives. Handle the Aerospike-specific `namespace.set` dotted notation (no scope concept, unlike Couchbase).

**Prerequisites:** Task 3.1 complete.

**Completion Criteria:** Parser correctly handles all AQL statement types with proper error messages.

**Test strategy:** Comprehensive parameterized tests:
- CREATE INDEX with each index type (STRING, NUMERIC, GEO2DSPHERE)
- CREATE INDEX without explicit type (default STRING)
- DROP INDEX (note: AQL syntax is `DROP INDEX namespace index_name`, not `ON`)
- INSERT INTO with various value types
- DELETE FROM
- CREATE SET
- Case insensitivity
- Error cases: malformed statements, missing namespace, unknown index type
- Whitespace variations

**Subtasks:**
- [ ] Create `Parsers/AerospikeStatementParser.cs` with Parlot grammar
- [ ] Implement `ParseStatement(string statement)` returning `AerospikeStatementItem`
- [ ] Handle `namespace.set` dotted identifier parsing
- [ ] Handle parenthesized bin lists for INSERT
- [ ] Handle index type keyword parsing (STRING, NUMERIC, GEO2DSPHERE)
- [ ] Handle AQL DROP INDEX syntax (`DROP INDEX namespace index_name`)
- [ ] Add clear `ElseError()` messages for parse failures
- [ ] Create `tests/Hyperbee.Migrations.Tests/AerospikeStatementParserTests.cs` with comprehensive tests:
  - CREATE INDEX (each type, default type, with/without namespace)
  - DROP INDEX
  - INSERT INTO (string values, numeric values, mixed)
  - DELETE FROM
  - CREATE SET
  - Case insensitivity
  - Error messages for malformed input
  - Whitespace edge cases

`Status: Not Started`

### Task 3.3: Aerospike Resource Runner

**Description:** Implement `AerospikeResourceRunner<TMigration>` with both `StatementsFromAsync` and `DocumentsFromAsync`, including async index handling.

**Implementation strategy:** Follow the Couchbase resource runner pattern. Key additions for Aerospike:
- **Index existence check** before CREATE INDEX (idempotent by default)
- **@RECREATE handling**: drop existing index before re-creating
- **@WAITREADY handling**: after CREATE INDEX, poll index status using Aerospike info commands until index is in ready state
- **Wait strategy**: use existing `WaitHelper.WaitUntilAsync` with `BackoffRetryStrategy` for index readiness polling
- **Documents**: Aerospike documents map to records with bins. JSON documents are parsed and each top-level property becomes a bin.

**Prerequisites:** Task 3.2 complete.

**Completion Criteria:** Resource runner handles all statement types including async index creation with directives.

**Test strategy:** Unit tests for:
- Statement dispatch (each type calls correct Aerospike operation)
- Directive handling (recreate drops first, waitReady polls)
- Document loading and bin mapping
- Timeout/cancellation behavior
- Index existence check (skip if exists, force if recreate)

**Subtasks:**
- [ ] Create `Resources/AerospikeResourceRunner.cs` with constructor (IAerospikeClient, ILogger)
- [ ] Implement `StatementsFromAsync` overloads (single, array, with timeout) — load JSON, parse statements + directive metadata, execute
- [ ] Implement statement execution switch:
  - `CreateIndex` → check exists, create via `client.CreateIndex()`, optionally wait for ready
  - `DropIndex` → `client.DropIndex()`
  - `CreateSet` → ensure set exists (Aerospike creates sets implicitly, but validate namespace)
  - `Insert` → `client.Put()`
  - `Delete` → `client.Delete()`
- [ ] Implement `IndexExistsAsync()` — query secondary index info
- [ ] Implement `WaitForIndexReadyAsync()` — poll index status using info commands, use `WaitHelper.WaitUntilAsync` with `BackoffRetryStrategy`, timeout after configurable duration
- [ ] Implement `@RECREATE` logic: if index exists and recreate=true, drop then create
- [ ] Implement `@WAITREADY` logic: after create, poll until ready
- [ ] Implement `DocumentsFromAsync` overloads — load JSON documents, map to Aerospike records (each JSON property → bin), put to namespace.set
- [ ] Add `ThrowIfNoResourceLocationFor()` validation
- [ ] Create `tests/Hyperbee.Migrations.Tests/AerospikeResourceRunnerTests.cs`:
  - Statement dispatch tests
  - Directive handling tests
  - Index existence skip test
  - Document bin mapping test
  - Timeout behavior test

`Status: Not Started`

### Task 3.4: Aerospike Sample Migration Runner

**Description:** Create a sample Aerospike migration runner with example migrations and embedded resources demonstrating the resource migration pattern.

**Implementation strategy:** Follow the existing Couchbase/MongoDB sample structure. Create a samples project with 2-3 migrations demonstrating: collection/index creation via statements, document seeding, async index with @WAITREADY.

**Prerequisites:** Task 3.3 complete.

**Completion Criteria:** Sample project builds, demonstrates all resource migration capabilities.

**Subtasks:**
- [ ] Create `samples/Hyperbee.Migrations.Aerospike.Samples/` with migration classes and embedded resources
- [ ] Create `samples/Hyperbee.MigrationRunner.Aerospike/` with Program.cs, MainService.cs, StartupExtensions.cs, appsettings.json
- [ ] Create sample migrations:
  - `1000-CreateInitialSets` — statements.json with CREATE INDEX + @WAITREADY, document seeding
  - `2000-AddSecondaryIndexes` — statements.json with CREATE INDEX + various types
- [ ] Create sample embedded resources (statements.json, document JSON files)
- [ ] Add Dockerfile for integration test container

`Status: Not Started`

**Phase 3 Completion:**
- Snapshot: `plan/resource-migrations/phase-3`
- Summary: _pending_
- Learnings: _pending_

---

## Learnings Ledger

| # | Phase | Type | Learning |
|---|-------|------|----------|
| | | | _populated during execution_ |

---

## Status Summary

| Phase | Status | Notes |
|-------|--------|-------|
| Phase 0 | In Progress | Audit complete, branch/commit pending |
| Phase 1 | Not Started | MongoDB parser + resource runner |
| Phase 2 | Not Started | Aerospike provider scaffold |
| Phase 3 | Not Started | Aerospike parser + resource runner |

**Current Task:** Phase 0 — Create branch, initial commit
**Next Action:** Complete Phase 0, begin Task 1.1
**Blockers:** None
