---
layout: default
title: Continuous Migrations
nav_order: 5
---

# Continuous Migrations

Hyperbee Migrations supports long-running and repeating migrations through a
loop-based execution model built into the migration runner. Using lifecycle
methods (`StartMethod` and `StopMethod`) and optional cron scheduling, a single
migration can execute repeatedly on a schedule, poll for conditions, or run
until a stopping criterion is met.

## How It Works

The runner wraps each migration in a `while` loop. On every iteration:

1. **StartMethod** is called. If it returns `false`, the loop waits and retries.
   If it returns `true` (or is not defined), the migration proceeds.
2. **UpAsync** (or `DownAsync`) executes the migration logic.
3. **StopMethod** is called. If it returns `true` (or is not defined), the loop
   exits and the migration is marked complete. If it returns `false`, the loop
   continues back to step 1.

```
while (stopProcess == false):
    |
    +-- StartMethod() --> false? loop back (wait/retry)
    |                 --> true?  continue
    |
    +-- UpAsync()
    |
    +-- StopMethod()  --> true?  exit loop, record migration
                      --> false? loop back to StartMethod
```

When neither StartMethod nor StopMethod is defined, the migration runs exactly
once -- the default behavior.

## Defining Lifecycle Methods

Lifecycle methods are specified by name in the `[Migration]` attribute and
discovered via reflection at runtime.

```csharp
[Migration(2000, "StartMethod", "StopMethod")]
public class MyRepeatingMigration : Migration
{
    public override async Task UpAsync(CancellationToken cancellationToken = default)
    {
        // This executes on every loop iteration
    }

    public async Task<bool> StartMethod()
    {
        // Return true to proceed to UpAsync
        // Return false to skip this iteration (loop retries)
        return await Task.FromResult(true);
    }

    public Task<bool> StopMethod()
    {
        // Return true to exit the loop (migration complete)
        // Return false to continue looping
        return Task.FromResult(true);
    }
}
```

**Method signature requirements:**
- Must return `Task<bool>`
- Must take zero parameters
- Must match the name specified in the attribute exactly

## Cron-Based Scheduling

The `MigrationCronHelper` integrates with the lifecycle model to create
time-gated migrations. It blocks the runner until the next cron occurrence,
then returns `true` to proceed.

```csharp
[Migration(3000, "StartMethod", "StopMethod")]
public class HourlyCleanup : Migration
{
    private int _executionCount = 0;

    public override async Task UpAsync(CancellationToken cancellationToken = default)
    {
        // Cleanup logic that runs every hour
    }

    public async Task<bool> StartMethod()
    {
        _executionCount++;
        var helper = new MigrationCronHelper();
        return await helper.CronDelayAsync("0 * * * *"); // block until next hour
    }

    public Task<bool> StopMethod()
    {
        // Run 24 times (once per hour for a day), then stop
        return Task.FromResult(_executionCount >= 24);
    }
}
```

**How `CronDelayAsync` works:**
1. Parses the cron expression using the Cronos library
2. Calculates the next occurrence from the current UTC time
3. Calls `Task.Delay()` to block until that time
4. Returns `true` (always proceeds to UpAsync)

**Cron format:** Standard five-field format -- `minute hour day month weekday`

| Expression    | Schedule               |
|---------------|------------------------|
| `* * * * *`   | Every minute           |
| `0 * * * *`   | Every hour             |
| `0 2 * * *`   | Daily at 2:00 AM UTC   |
| `0 0 * * 0`   | Weekly on Sunday       |
| `*/5 * * * *` | Every 5 minutes        |

## Journaling and Continuous Migrations

The `journal` parameter controls whether the migration is recorded after
completion. This is critical for continuous migration behavior:

**Journaled (default):** The migration record is written only after StopMethod
returns `true`. On subsequent runner executions, the migration is skipped
because it is already recorded.

```csharp
// Runs its loop once, then never again
[Migration(1000, "StartMethod", "StopMethod")]
```

**Non-journaled:** The migration record is never written. The migration runs
its full loop every time the runner executes.

```csharp
// Runs its loop on every runner execution
[Migration(1000, "StartMethod", "StopMethod", false)]
```

## Common Patterns

### Poll Until Ready

Wait for an external condition before proceeding with a one-time migration.

```csharp
[Migration(1000, "WaitForService", null)]
public class MigrateWhenReady : Migration
{
    private readonly IHealthChecker _health;

    public MigrateWhenReady(IHealthChecker health) => _health = health;

    public override async Task UpAsync(CancellationToken cancellationToken = default)
    {
        // Run once after the service is healthy
    }

    public async Task<bool> WaitForService()
    {
        var ready = await _health.IsReadyAsync();
        if (!ready)
            await Task.Delay(TimeSpan.FromSeconds(10));
        return ready;
    }
}
```

### Repeating Task with Counter

Execute a migration a fixed number of times.

```csharp
[Migration(2000, "Start", "Stop")]
public class BatchProcessor : Migration
{
    private int _batch = 0;

    public override async Task UpAsync(CancellationToken cancellationToken = default)
    {
        // Process batch _batch
    }

    public Task<bool> Start() => Task.FromResult(true);

    public Task<bool> Stop()
    {
        _batch++;
        return Task.FromResult(_batch >= 10); // 10 batches
    }
}
```

### Scheduled Recurring Task

Run on a cron schedule until a condition is met.

```csharp
[Migration(3000, "OnSchedule", "CheckComplete", false)]
public class ScheduledSync : Migration
{
    private readonly ISyncService _sync;

    public ScheduledSync(ISyncService sync) => _sync = sync;

    public override async Task UpAsync(CancellationToken cancellationToken = default)
    {
        await _sync.SyncAsync(cancellationToken);
    }

    public async Task<bool> OnSchedule()
    {
        var helper = new MigrationCronHelper();
        return await helper.CronDelayAsync("*/30 * * * *"); // every 30 minutes
    }

    public async Task<bool> CheckComplete()
    {
        return await _sync.IsFullySyncedAsync();
    }
}
```

## Pattern Reference

| Pattern | StartMethod | StopMethod | Journal | Behavior |
|---------|-------------|------------|---------|----------|
| One-time migration | None | None | true | Runs once, recorded, never repeats |
| Conditional gate | Custom poll | None | true | Waits for condition, runs once, recorded |
| Fixed loop count | `return true` | Counter check | true | Loops N times, recorded, never repeats |
| Scheduled loop | Cron delay | Counter/condition | true | Runs on schedule until done, recorded |
| Recurring task | Cron delay | Condition check | false | Runs every runner execution on schedule |
| Always-run setup | None | None | false | Runs every time, no looping |

## Important Considerations

- **Blocking behavior:** `CronDelayAsync` and `Task.Delay` block the entire
  migration runner. No other migrations execute while a migration is waiting
  in its StartMethod. Plan accordingly.

- **Sequential execution:** Migrations with loops run sequentially. A migration
  at version 2000 will not start until the migration at version 1000 has
  completed its entire loop (StopMethod returned `true`).

- **Instance state:** StartMethod and StopMethod run on the same migration
  instance as UpAsync, so instance fields (like counters) are preserved across
  loop iterations within a single runner execution.

- **No parameters:** Lifecycle methods must have the signature `Task<bool>()`
  with no parameters. They cannot receive CancellationToken or other arguments.

- **String-based discovery:** Method names are matched by string at runtime via
  reflection. There is no compile-time validation -- a typo in the attribute
  will cause the method to not be found (logged as an error, returns false).
