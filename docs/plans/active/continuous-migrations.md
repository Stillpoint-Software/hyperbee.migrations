# Plan: Improved Continuous/Recurring Migration Support

## Process

Each task follows **ITRV**: Implement -> Test -> Reflect -> Verify.

## Objective

Replace the current blocking, reflection-based continuous migration model with a
non-blocking, type-safe, cron-aware system. Migrations can declare a cron schedule
via the attribute or implement `IContinuousMigration` for custom lifecycle control.

### Success Criteria

- `[Migration(1000, Cron = "0 2 * * *")]` works as a declarative scheduled migration
- `IContinuousMigration` interface provides type-safe Start/Stop with CancellationToken
- Runner is non-blocking: checks due-ness, runs if ready, skips if not
- All 4 provider record stores implement `ReadAsync` returning `MigrationRecord`
- `MigrationCronHelper` supports CancellationToken and has `IsDue` method
- Old string-based StartMethod/StopMethod still works (backwards compat)
- All unit tests pass, new tests cover cron scheduling and interface-based lifecycle
- Documentation updated

### Constraints

- Breaking changes to `IMigrationRecordStore` are acceptable
- Old StartMethod/StopMethod reflection path preserved for backwards compatibility
- Cron parsing continues to use Cronos library

## Style Reference

- PascalCase types, camelCase locals, `_camelCase` private fields
- Async suffix, CancellationToken parameter
- `[TestClass]` + `[TestMethod]`, Assert-based tests

## Git Workflow

| Phase | Tag | Description |
|-------|-----|-------------|
| Phase 0 | -- | Audit only |
| Phase 1 | `continuous/phase-1` | Core framework changes |
| Phase 2 | `continuous/phase-2` | Provider implementations |
| Phase 3 | `continuous/phase-3` | Tests and docs |

---

## Phase 0: Audit

**Goal**: Verify build baseline.

### Task 0.1: Build and Test Baseline

- [ ] `dotnet build` compiles cleanly
- [ ] Unit tests pass

**Status: Not Started**

---

## Phase 1: Core Framework Changes

**Goal**: Add the new interfaces, attribute property, helper methods, and runner logic.
**Testing**: `dotnet build` compiles.

### Task 1.1: Add ReadAsync to IMigrationRecordStore

Extend the interface with a method to read the migration record including its timestamp.

**Subtasks:**

- [ ] Add `Task<MigrationRecord> ReadAsync(string recordId);` to `IMigrationRecordStore`
- [ ] Verify `MigrationRecord` already has `Id` and `RunOn` properties (it does)
- [ ] Build compiles (providers will break -- expected, fixed in Phase 2)

**Status: Not Started**

### Task 1.2: Add Cron Property to MigrationAttribute

Add an optional `Cron` property for declarative scheduling.

**Subtasks:**

- [ ] Add `public string Cron { get; set; }` property to `MigrationAttribute`
- [ ] No constructor changes needed -- use named property syntax: `[Migration(1000, Cron = "...")]`

**Status: Not Started**

### Task 1.3: Add IContinuousMigration Interface

Create the interface for type-safe lifecycle control.

**Subtasks:**

- [ ] Create `IContinuousMigration.cs` in `src/Hyperbee.Migrations/`:
  ```csharp
  public interface IContinuousMigration
  {
      Task<bool> StartAsync(CancellationToken cancellationToken = default);
      Task<bool> StopAsync(CancellationToken cancellationToken = default);
  }
  ```

**Status: Not Started**

### Task 1.4: Update MigrationCronHelper

Add CancellationToken support and a non-blocking `IsDue` check.

**Subtasks:**

- [ ] Add `CronDelayAsync(string cron, CancellationToken cancellationToken)` overload that passes token to `Task.Delay`
- [ ] Add static `IsDue(string cronExpression, DateTimeOffset? lastRunOn)` method:
  - If `lastRunOn` is null, return true (never run before)
  - Compute next occurrence after `lastRunOn`
  - Return `nextOccurrence <= DateTimeOffset.UtcNow`

**Status: Not Started**

### Task 1.5: Update MigrationRunner

Rework the migration execution loop to support three modes.

**Subtasks:**

- [ ] **Cron-based migrations** (attribute has `Cron` set):
  - Call `ReadAsync(recordId)` to get last run timestamp
  - Call `MigrationCronHelper.IsDue(cron, record?.RunOn)` to check due-ness
  - If not due, skip (continue to next migration)
  - If due, execute `UpAsync`, then call `WriteAsync` (overwrite/update record)
  - No while loop needed
- [ ] **IContinuousMigration interface** (migration implements interface):
  - Call `StartAsync(ct)` instead of reflection-based StartMethod
  - Call `StopAsync(ct)` instead of reflection-based StopMethod
  - Preserve the existing while loop semantics
- [ ] **Legacy string-based** (StartMethod/StopMethod strings in attribute):
  - Keep existing reflection path as fallback
  - Only used if migration does NOT implement IContinuousMigration
- [ ] **One-time migrations** (no cron, no lifecycle):
  - Existing behavior unchanged
- [ ] For cron migrations, `WriteAsync` should update (not just insert) the record. Check if providers handle upsert or if we need to delete+write.

**Status: Not Started**

---

## Phase 2: Provider Record Store Implementations

**Goal**: All 4 providers implement `ReadAsync` returning `MigrationRecord`.
**Testing**: `dotnet build` compiles, existing tests pass.

### Task 2.1: Implement ReadAsync in All Providers

Each provider already stores timestamps. We need to read them back.

**Subtasks:**

- [ ] **AerospikeRecordStore**: Read record by key, convert `ExecutedAt` Unix timestamp to `DateTimeOffset`, return `MigrationRecord`
- [ ] **CouchbaseRecordStore**: Read document by key (already stores `MigrationRecord`), return it
- [ ] **MongoDBRecordStore**: Find document by ID (already stores `MigrationRecord`), return it
- [ ] **PostgresRecordStore**: SELECT `record_id, run_on` from table, map to `MigrationRecord`
- [ ] For cron migrations, ensure `WriteAsync` works as upsert (re-running a cron migration should update the timestamp, not fail on duplicate key). Check each provider.
- [ ] Build compiles, existing unit tests pass

**Status: Not Started**

---

## Phase 3: Tests and Documentation

**Goal**: New tests cover all three migration modes. Documentation updated.
**Testing**: Full test suite passes.

### Task 3.1: Add Unit Tests

**Subtasks:**

- [ ] Test cron migration: verify `IsDue` returns true when past due, false when not
- [ ] Test cron migration: verify runner skips non-due migrations
- [ ] Test cron migration: verify runner executes due migrations and updates record
- [ ] Test IContinuousMigration: verify StartAsync/StopAsync are called with CancellationToken
- [ ] Test IContinuousMigration: verify loop exits when StopAsync returns true
- [ ] Test legacy: verify old string-based StartMethod/StopMethod still works
- [ ] Test MigrationCronHelper.IsDue with null lastRunOn (should be due)
- [ ] Test MigrationCronHelper.IsDue with recent lastRunOn (should not be due)
- [ ] All existing tests still pass

**Status: Not Started**

### Task 3.2: Update Documentation

**Subtasks:**

- [ ] Rewrite `docs/site/continuous-migrations.md` to reflect the new model:
  - Three modes: one-time, scheduled (cron attribute), custom lifecycle (interface)
  - Non-blocking execution model for cron
  - `IsDue` helper usage
  - `IContinuousMigration` interface examples
  - Runner invocation patterns (hosted service timer, external scheduler)
  - Migration attribute Cron property
  - Pattern reference table updated
- [ ] Update `docs/site/concepts.md` to mention cron scheduling
- [ ] Update `docs/site/advanced.md` if locking/retry sections affected

**Status: Not Started**

---

## Learnings Ledger

| Phase | Learning | Type |
|-------|----------|------|
| -- | -- | -- |

## Status Summary

| Phase | Status |
|-------|--------|
| Phase 0 (Audit) | **Done** |
| Phase 1 (Core) | **Done** |
| Phase 2 (Providers) | **Done** |
| Phase 3 (Tests) | **Done** |
| Phase 3 (Docs) | Not Started |

**Current task**: Phase 3 -- Documentation
**Next action**: Rewrite continuous-migrations.md
**Blockers**: None
