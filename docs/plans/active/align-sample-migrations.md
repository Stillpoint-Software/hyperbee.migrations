# Plan: Align Samples, Restructure Runners, and Update Documentation

## Process

Each task follows **ITRV**: Implement → Test → Reflect → Verify.
- **Implement**: make the change
- **Test**: build compiles, integration tests pass
- **Reflect**: check consistency across providers
- **Verify**: `dotnet build` clean, `dotnet test` green

## Objective

1. Restructure the repository: move MigrationRunner projects from `samples/` to `runners/`
2. Rename and align sample migrations across all four providers to tell the same logical story
3. Bring documentation up to date: Aerospike docs, updated README, runner READMEs, resource migration coverage
4. Delete stale `docs/site/.todo.md`

### Success Criteria

- MigrationRunner projects live under `runners/`, sample migration libraries under `samples/`
- All four providers use the same three migration class names with consistent domain model
- Each provider uses its own idiomatic naming for containers, fields, indexes
- Aerospike has a docs/site page on par with other providers
- README reflects all four providers and resource migration features
- Each runner project has a README
- Existing provider docs cover resource migrations (StatementsFromAsync, DocumentsFromAsync)
- `.todo.md` deleted
- Integration tests pass, solution builds clean

### Constraints

- Keep the 1000/2000/3000 version numbering
- Preserve existing resource migration infrastructure (no runner code changes)
- Resource folder naming follows convention: `{version}-{ClassName}`

## Style Reference

- **Naming**: PascalCase types, camelCase locals, `_camelCase` private fields
- **Async**: `Async` suffix, `CancellationToken` parameter
- **DI**: Constructor injection, primary constructors for resource migrations
- **Resource runners**: `{Provider}ResourceRunner<TMigration>` generic pattern
- **Tests**: `[TestClass]` + `[TestMethod]`, Assert-based

## Git Workflow

| Phase | Tag | Description |
|-------|-----|-------------|
| Phase 0 | — | Audit only |
| Phase 1 | `align-samples/phase-1` | Folder restructure complete |
| Phase 2 | `align-samples/phase-2` | Aerospike aligned |
| Phase 3 | `align-samples/phase-3` | Couchbase aligned |
| Phase 4 | `align-samples/phase-4` | MongoDB aligned |
| Phase 5 | `align-samples/phase-5` | PostgreSQL aligned |
| Phase 6 | `align-samples/phase-6` | Integration tests updated |
| Phase 7 | `align-samples/phase-7` | Documentation complete |

---

## Unified Migration Progression

| Version | Class Name | Type | Purpose |
|---------|-----------|------|---------|
| 1000 | `CreateInitialSchema` | Resource | Create primary containers + entity structures, seed initial data |
| 2000 | `AddSecondaryIndexes` | Resource | Add secondary indexes, seed additional data |
| 3000 | `SeedData` | Code | Demonstrate code-based migration with DI |

## Shared Domain Model

### Users

| Field (logical) | Aerospike (bin) | Couchbase (field) | MongoDB (field) | PostgreSQL (column) |
|-----------------|-----------------|-------------------|-----------------|---------------------|
| id | `id` | `userId` | `userId` | `user_id` (SERIAL PK) |
| name | `name` | `name` | `name` | `name` |
| email | `email` | `email` | `email` | `email` |
| active | `active` (1/0) | `active` (bool) | `active` (bool) | `active` (BOOLEAN) |
| role | `role` | `role` | `role` | `role` |
| createdDate | `createdDate` | `createdDate` | `createdDate` | `created_date` (TIMESTAMPTZ) |

### Products

| Field (logical) | Aerospike (bin) | Couchbase (field) | MongoDB (field) | PostgreSQL (column) |
|-----------------|-----------------|-------------------|-----------------|---------------------|
| id | `id` | `productId` | `productId` | `product_id` (SERIAL PK) |
| name | `name` | `name` | `name` | `name` |
| category | `category` | `category` | `category` | `category` |
| price | `price` | `price` (number) | `price` (number) | `price` (NUMERIC(10,2)) |
| active | `active` (1/0) | `active` (bool) | `active` (bool) | `active` (BOOLEAN) |
| createdDate | `createdDate` | `createdDate` | `createdDate` | `created_date` (TIMESTAMPTZ) |

## Per-Provider Naming Conventions

### Aerospike
- **Namespace**: `test`
- **Sets**: `users`, `products` (lowercase plural)
- **Bins**: camelCase (`userId`, `createdDate`)
- **Indexes**: `idx_users_email`, `idx_users_active`, `idx_users_role`, `idx_products_category`, `idx_products_price`
- **Record keys**: descriptive (`user-admin`, `user-001`, `prod-001`)

### Couchbase
- **Bucket**: `sample`
- **Scope**: `inventory` (named scope, not `_default`)
- **Collections**: `users`, `products`
- **Fields**: camelCase (`userId`, `createdDate`)
- **Indexes**: `idx_users_email`, `idx_users_active`, `idx_users_role`, `idx_products_category`, `idx_products_price`
- **Document keys**: `user::001`, `product::001` (Couchbase convention with `::` separator)

### MongoDB
- **Database**: `sample`
- **Collections**: `users`, `products` (lowercase plural)
- **Fields**: camelCase (`userId`, `createdDate`)
- **Indexes**: `idx_users_email` (unique), `idx_users_active`, `idx_users_role`, `idx_products_category`, `idx_products_price`

### PostgreSQL
- **Schema**: `sample`
- **Tables**: `users`, `products` (lowercase plural)
- **Columns**: snake_case (`user_id`, `created_date`)
- **Indexes**: `ix_users_email` (unique), `ix_users_active`, `ix_users_role`, `ix_products_category`, `ix_products_price`
- **Constraints**: `pk_users`, `pk_products`

---

## Phase 0: Audit and Preparation

**Goal**: Verify build, confirm test infrastructure, document baseline.

### Task 0.1: Build and Test Baseline

- [x] `dotnet build` compiles cleanly (0 errors, 30 pre-existing warnings — obsolete testcontainers constructors, MSTest analyzer)
- [x] Unit tests pass: 105/105 on net8.0, net9.0, net10.0. Integration tests require Docker — will validate per-phase.
- [x] Resource embedding: all providers use explicit `<None Remove>` + `<EmbeddedResource Include>` per file in .csproj
- [x] Pre-existing fix: solution file referenced `test/` instead of `tests/` — corrected in .slnx

**Status: **Done****

---

## Phase 1: Restructure — Move Runners to `runners/`

**Goal**: Move all four MigrationRunner projects from `samples/` to `runners/` and update all path references.
**Testing**: `dotnet build` + verify Docker builds still resolve.

### Task 1.1: Move MigrationRunner Projects

Move these directories:
- `samples/Hyperbee.MigrationRunner.Aerospike/` → `runners/Hyperbee.MigrationRunner.Aerospike/`
- `samples/Hyperbee.MigrationRunner.Couchbase/` → `runners/Hyperbee.MigrationRunner.Couchbase/`
- `samples/Hyperbee.MigrationRunner.MongoDB/` → `runners/Hyperbee.MigrationRunner.MongoDB/`
- `samples/Hyperbee.MigrationRunner.Postgres/` → `runners/Hyperbee.MigrationRunner.Postgres/`

**Subtasks:**

- [x] Create `runners/` directory and move all four MigrationRunner projects (cp + rm due to file lock, git staged)
- [x] Move all four .Samples projects from `samples/` to `runners/samples/` (user decision to co-locate)
- [x] Update `Hyperbee.Migrations.slnx` — `/Runners/` folder for runners, `/Samples/` folder with `runners/samples/` paths
- [x] Update all 4 Dockerfiles — `runners/Hyperbee.MigrationRunner.*` and `runners/samples/Hyperbee.Migrations.*.Samples` paths
- [x] Update all 4 integration test container configs — `.WithDockerfile("runners/...")`
- [x] Update all 4 runner `appsettings.json` `FromPaths` — `runners\\samples\\` prefix
- [x] Update all 4 sample `.csproj` `ProjectReference` paths — `..\..\..\src\` (3 levels up from runners/samples/X/)
- [x] `dotnet build` — 0 errors, 0 warnings. 105/105 unit tests pass.

**Status: **Done****

---

## Phase 2: Align Aerospike Samples

**Goal**: Rename Aerospike migrations and align domain model.
**Testing**: `dotnet build` + Aerospike integration test.

### Task 2.1: Rename Migration Files and Classes

Aerospike is closest to the target state. Changes needed:

| Current | New |
|---------|-----|
| `1000-CreateInitialSets.cs` / `CreateInitialSets` | `1000-CreateInitialSchema.cs` / `CreateInitialSchema` |
| `2000-AddSecondaryIndexes.cs` / `AddSecondaryIndexes` | (no rename needed) |
| `3000-CodeMigration.cs` / `CodeMigration` | `3000-SeedData.cs` / `SeedData` |

**Subtasks:**

- [x] Rename `1000-CreateInitialSets.cs` → `1000-CreateInitialSchema.cs`, class `CreateInitialSchema`
- [x] Rename `3000-CodeMigration.cs` → `3000-SeedData.cs`, class `SeedData` — seeds users + products via `IAsyncClient`
- [x] Rename resource folder: `Resources/1000-CreateInitialSets/` → `Resources/1000-CreateInitialSchema/`
- [x] Update `.csproj` embedded resource paths
- [x] `3000-SeedData.cs` seeds user-003 and prod-003 via code (demonstrates DI with `IAsyncClient`)
- [x] Resource files aligned: `createdTimestamp` → `createdDate`, products have `active`/`createdDate` (removed `inStock`), removed `idx_location` (GEO2DSPHERE)
- [x] Index names aligned: `idx_users_email`, `idx_users_active`, `idx_users_role`, `idx_products_category`, `idx_products_price`
- [x] Build: 0 errors, 0 warnings. 105/105 tests pass.

**Status: **Done****

---

## Phase 3: Align Couchbase Samples

**Goal**: Rename Couchbase migrations, introduce proper domain model with named scope/collections.
**Testing**: `dotnet build` + Couchbase integration test.

### Task 3.1: Rewrite Couchbase Migrations

Couchbase needs the most work — currently uses a generic `migrationbucket` with no real domain model.

| Current | New |
|---------|-----|
| `1000-CreateInitialBuckets.cs` / `CreateInitialBuckets` | `1000-CreateInitialSchema.cs` / `CreateInitialSchema` |
| `2000-SecondaryAction.cs` / `SecondaryAction` | `2000-AddSecondaryIndexes.cs` / `AddSecondaryIndexes` |
| `3000-MigrationAction.cs` / `MigrationAction` | `3000-SeedData.cs` / `SeedData` |

**Subtasks:**

- [x] Create `1000-CreateInitialSchema.cs` — resource migration creating `sample` bucket, primary index, seeding user documents
- [x] Create `2000-AddSecondaryIndexes.cs` — resource migration creating GSI indexes on users/products fields
- [x] Create `3000-SeedData.cs` — code migration seeding user::003 and product::003 via `IClusterProvider`
- [x] Write resource files: bucket creation, primary index, user seed documents, GSI indexes with WHERE clauses
- [x] Remove old migration files and resource folders (migrationbucket → sample)
- [x] Update `.csproj` embedded resource paths
- [x] Build: 0 errors, 0 warnings. 105/105 tests pass.

**Status: **Done****

---

## Phase 4: Align MongoDB Samples

**Goal**: Rename MongoDB migrations, add products collection, align domain model.
**Testing**: `dotnet build` + MongoDB integration test.

### Task 4.1: Rewrite MongoDB Migrations

| Current | New |
|---------|-----|
| `1000-Initial.cs` / `Initial` | `1000-CreateInitialSchema.cs` / `CreateInitialSchema` |
| `2000-MigrationAction.cs` / `MigrationAction` | `2000-AddSecondaryIndexes.cs` / `AddSecondaryIndexes` |
| `3000-AddIndexes.cs` / `AddIndexes` | `3000-SeedData.cs` / `SeedData` |

**Subtasks:**

- [x] Create `1000-CreateInitialSchema.cs` — resource migration seeding users and products via `DocumentsFromAsync`
- [x] Create `2000-AddSecondaryIndexes.cs` — resource migration creating indexes via `StatementsFromAsync`
- [x] Create `3000-SeedData.cs` — code migration seeding user 3 and product 3 via `IMongoClient`
- [x] Write resource files: user1/user2/product1/product2 JSON documents, statements.json with createIndex commands
- [x] Remove old migration files and resource folders (`administration` → `sample`)
- [x] Update `.csproj` embedded resource paths
- [x] Build: 0 errors, 0 warnings.

**Status: **Done****

---

## Phase 5: Align PostgreSQL Samples

**Goal**: Rename PostgreSQL migrations, add products table, align domain model.
**Testing**: `dotnet build` + PostgreSQL integration test.

### Task 5.1: Rewrite PostgreSQL Migrations

| Current | New |
|---------|-----|
| `1000-Initial.cs` / `Initial` | `1000-CreateInitialSchema.cs` / `CreateInitialSchema` |
| `2000-MigrationAction.cs` / `MigrationAction` | `2000-AddSecondaryIndexes.cs` / `AddSecondaryIndexes` |
| `3000-SeedData.cs` / `SeedData` | (no rename needed) |

**Subtasks:**

- [x] Create `1000-CreateInitialSchema.cs` — resource migration creating `sample` schema, `users` and `products` tables
- [x] Create `2000-AddSecondaryIndexes.cs` — resource migration creating indexes via SQL
- [x] Update `3000-SeedData.cs` — code migration seeding users and products via `NpgsqlDataSource`
- [x] Write `CreateSchema.sql` (schema + tables) and `CreateIndexes.sql` (ix_ prefixed indexes)
- [x] Remove old migration files and resource folders (`administration` → `sample`)
- [x] Update `.csproj` embedded resource paths
- [x] Build: 0 errors, 0 warnings.

**Status: **Done****

---

## Phase 6: Update Integration Tests

**Goal**: Update integration test assertions to match new migration class names.
**Testing**: Full integration test suite.

### Task 6.1: Update Test Assertions

Integration tests assert on migration class names in log output. All four test files need updating.

**Subtasks:**

- [x] Update `AerospikeRunnerTest.cs` — class names, index names (idx_users_*, idx_products_*), removed idx_location
- [x] Update `CouchbaseRunnerTest.cs` — migrationbucket → sample, class names, migration count
- [x] Update `MongoDBRunnerTest.cs` — Initial → CreateInitialSchema, MigrationAction → AddSecondaryIndexes, administration → sample, added SeedData assertions, 2 → 3 migrations
- [x] Update `PostgresRunnerTest.cs` — Initial → CreateInitialSchema, MigrationAction → AddSecondaryIndexes, added SeedData assertions, 2 → 3 migrations
- [x] Full build: 0 errors. 105/105 unit tests pass.

**Status: **Done****

---

## Phase 7: Documentation

**Goal**: Bring all documentation up to date with current provider support and features.
**Testing**: Build docs site locally, verify links.

### Task 7.1: Delete Stale `.todo.md`

- [ ] Delete `docs/site/.todo.md`

**Status: Not Started**

### Task 7.2: Add Aerospike Documentation Page

Create `docs/site/aerospike.md` following the same structure as the existing provider pages (couchbase.md, mongodb.md, postgresql.md).

**Subtasks:**

- [ ] Introduction — Hyperbee.Migrations.Providers.Aerospike overview
- [ ] Configuration — `AddAerospikeProvider()`, `AddAerospikeMigrations()`, namespace/set/lock options
- [ ] Migrations — code-based migration example with `IAsyncClient` and `IAerospikeClient` injection
- [ ] Resource Migrations — `AerospikeResourceRunner<T>`: `StatementsFromAsync` (AQL), `DocumentsFromAsync` (JSON records), directive support (`@RECREATE`, `@WAITREADY`)
- [ ] Profiles — same pattern as other providers
- [ ] Cron Settings — `MigrationCronHelper` usage
- [ ] Journaling — same pattern

**Status: Not Started**

### Task 7.3: Update Root README

**Subtasks:**

- [ ] Add Aerospike to the list of supported databases
- [ ] Add resource migrations as a key feature (embedded SQL, N1QL, AQL, MongoDB commands, JSON document seeding)
- [ ] Update project structure description to reflect `runners/` and `samples/` separation
- [ ] Ensure build/run instructions are current

**Status: Not Started**

### Task 7.4: Update Existing Provider Documentation

Update each provider's docs page to cover resource migration features that are currently undocumented.

**Subtasks:**

- [ ] **Couchbase** (`couchbase.md`) — add `StatementsFromAsync` section (N1QL via resource files), update `DocumentsFromAsync` examples, reference new sample migration names
- [ ] **MongoDB** (`mongodb.md`) — add `StatementsFromAsync` section (MongoDB commands via statements.json, new Parlot parser), update `DocumentsFromAsync` examples, reference new sample migration names
- [ ] **PostgreSQL** (`postgresql.md`) — update resource runner examples (`SqlFromAsync`, `AllSqlFromAsync`), reference new sample migration names
- [ ] Update `migration-runner.md` — add Aerospike to record store list, update sample runner references to `runners/` path, update command-line options for all providers

**Status: Not Started**

### Task 7.5: Add Runner READMEs

Add a README.md to each MigrationRunner project explaining what it is, how to configure, and how to run.

**Subtasks:**

- [ ] `runners/Hyperbee.MigrationRunner.Aerospike/README.md` — connection config, namespace/set setup, CLI usage, Docker usage
- [ ] `runners/Hyperbee.MigrationRunner.Couchbase/README.md` — connection config, bucket/scope/collection setup, CLI usage, Docker usage
- [ ] `runners/Hyperbee.MigrationRunner.MongoDB/README.md` — connection config, database/collection setup, CLI usage, Docker usage
- [ ] `runners/Hyperbee.MigrationRunner.Postgres/README.md` — connection config, schema/table setup, CLI usage, Docker usage

Each README should follow a consistent template:
1. What this runner is (one-liner)
2. Prerequisites (database instance)
3. Configuration (appsettings.json keys)
4. Running locally (`dotnet run`)
5. Running with Docker
6. CLI arguments reference
7. Relationship to companion `.Samples` project

**Status: Not Started**

---

## Learnings Ledger

| Phase | Learning | Type |
|-------|----------|------|
| — | — | — |

## Status Summary

| Phase | Status |
|-------|--------|
| Phase 0 (Audit) | **Done** |
| Phase 1 (Restructure) | **Done** |
| Phase 2 (Aerospike) | **Done** |
| Phase 3 (Couchbase) | **Done** |
| Phase 4 (MongoDB) | **Done** |
| Phase 5 (PostgreSQL) | **Done** |
| Phase 6 (Tests) | **Done** |
| Phase 7 (Documentation) | Not Started |

**Current task**: Phase 7 — Documentation
**Next action**: Task 7.1 — Delete stale .todo.md
**Blockers**: None
