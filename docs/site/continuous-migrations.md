---
layout: default
title: Continuous Migrations
nav_order: 5
---

# Continuous Migrations

Not all migrations are one-time schema changes. Some need to run on a schedule,
poll for readiness, or repeat until a condition is met. Hyperbee Migrations
supports these scenarios through three approaches, each suited to different
use cases.

## Three Approaches

| Approach | Best For | Blocking? | How It Works |
|----------|----------|-----------|--------------|
| [Cron Attribute](#cron-scheduled-migrations) | Recurring tasks on a schedule | No | Runner checks if due, runs if yes, skips if not |
| [IContinuousMigration](#icontinuousmigration-interface) | Custom lifecycle with cancellation | Looping | Type-safe Start/Stop with CancellationToken |
| [String-based lifecycle](#string-based-lifecycle-legacy) | Legacy/simple cases | Looping | Reflection-based Start/Stop methods |

---

## Cron-Scheduled Migrations

The recommended approach for recurring migrations. Add a `Cron` property to
the migration attribute, and the runner will check whether the migration is
due based on its last execution time.

```csharp
[Migration(2000, Cron = "0 2 * * *")]
public class NightlyCleanup : Migration
{
    public override async Task UpAsync(CancellationToken cancellationToken = default)
    {
        // Runs once per day at 2:00 AM UTC -- only when the runner is invoked
    }
}
```

### How It Works

1. The runner calls `ReadAsync(recordId)` to get the migration's last run time
2. `MigrationCronHelper.IsDue(cronExpression, lastRunOn)` checks whether the
   next cron occurrence after the last run has passed
3. If due: execute `UpAsync`, then update the record with the current timestamp
4. If not due: skip the migration entirely (non-blocking)
5. If never run before: always due (first execution)

The runner completes in seconds -- it does not block or wait. To run
migrations on a recurring basis, invoke the runner periodically using a hosted
service timer, Kubernetes CronJob, Windows Task Scheduler, or similar.

```
External scheduler (every 15 min) --> Runner starts
  --> Migration 1000 (one-time, recorded): skip
  --> Migration 2000 (cron hourly, last ran 45 min ago): due, run it
  --> Migration 3000 (cron daily, last ran 3 hours ago): not due, skip
  --> Runner exits in <1 second
```

### Cron Format

Standard five-field format: `minute hour day month weekday`

| Expression    | Schedule               |
|---------------|------------------------|
| `* * * * *`   | Every minute           |
| `0 * * * *`   | Every hour             |
| `0 2 * * *`   | Daily at 2:00 AM UTC   |
| `0 0 * * 0`   | Weekly on Sunday       |
| `*/5 * * * *` | Every 5 minutes        |

### Using IsDue Directly

You can also use the `IsDue` helper in custom code:

```csharp
// Returns true if the cron schedule is due based on last run time
var isDue = MigrationCronHelper.IsDue("0 * * * *", lastRunTimestamp);

// null lastRunOn means never run -- always returns true
var isDue = MigrationCronHelper.IsDue("0 * * * *", null); // true
```

---

## IContinuousMigration Interface

For migrations that need custom lifecycle control -- polling for readiness,
looping a fixed number of times, or coordinating with external systems.
This approach provides compile-time safety and CancellationToken support.

```csharp
[Migration(3000)]
public class BatchProcessor : Migration, IContinuousMigration
{
    private int _batch;

    public override async Task UpAsync(CancellationToken cancellationToken = default)
    {
        // Process batch -- runs on each loop iteration
    }

    public Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        // Return true to proceed to UpAsync
        // Return false to skip this iteration (loop retries)
        return Task.FromResult(true);
    }

    public Task<bool> StopAsync(CancellationToken cancellationToken = default)
    {
        _batch++;
        // Return true to exit the loop (migration complete)
        // Return false to continue looping
        return Task.FromResult(_batch >= 10);
    }
}
```

### Execution Flow

```
while (stopProcess == false):
    |
    +-- StartAsync(ct) --> false? loop back
    |                  --> true?  continue
    |
    +-- UpAsync(ct)
    |
    +-- StopAsync(ct)  --> true?  exit loop, record migration
                       --> false? loop back to StartAsync
```

### Interface Definition

```csharp
public interface IContinuousMigration
{
    Task<bool> StartAsync(CancellationToken cancellationToken = default);
    Task<bool> StopAsync(CancellationToken cancellationToken = default);
}
```

Both methods receive the runner's CancellationToken, so they can honor
application shutdown signals.

### Example: Poll Until Ready

```csharp
[Migration(1000)]
public class WaitForDependency : Migration, IContinuousMigration
{
    private readonly IHealthChecker _health;

    public WaitForDependency(IHealthChecker health) => _health = health;

    public override async Task UpAsync(CancellationToken cancellationToken = default)
    {
        // Runs once after the dependency is healthy
    }

    public async Task<bool> StartAsync(CancellationToken cancellationToken = default)
    {
        var ready = await _health.IsReadyAsync(cancellationToken);
        if (!ready)
            await Task.Delay(TimeSpan.FromSeconds(10), cancellationToken);
        return ready;
    }

    public Task<bool> StopAsync(CancellationToken cancellationToken = default)
    {
        return Task.FromResult(true); // run once after ready
    }
}
```

---

## String-Based Lifecycle (Legacy)

The original approach, preserved for backwards compatibility. Lifecycle methods
are specified by name in the attribute and discovered via reflection.

```csharp
[Migration(4000, "StartMethod", "StopMethod")]
public class LegacyRepeating : Migration
{
    private int _count;

    public override async Task UpAsync(CancellationToken cancellationToken = default)
    {
        // migration logic
    }

    public Task<bool> StartMethod()
    {
        return Task.FromResult(true);
    }

    public Task<bool> StopMethod()
    {
        _count++;
        return Task.FromResult(_count >= 3);
    }
}
```

**Limitations compared to IContinuousMigration:**
- No CancellationToken -- methods cannot honor shutdown signals
- String-based discovery -- typos fail silently at runtime
- No compile-time validation of method signatures

If a migration implements `IContinuousMigration`, the interface methods take
precedence over string-based StartMethod/StopMethod.

### Blocking Cron with CronDelayAsync

The legacy `CronDelayAsync` method blocks the runner until the next cron
occurrence. This is still available but not recommended for long intervals.

```csharp
[Migration(5000, "OnSchedule", "CheckDone")]
public class BlockingSchedule : Migration
{
    public async Task<bool> OnSchedule()
    {
        var helper = new MigrationCronHelper();
        return await helper.CronDelayAsync("0 * * * *", cancellationToken);
    }
    // ...
}
```

For recurring tasks, prefer the `Cron` attribute approach instead -- it is
non-blocking and does not hold the runner or database lock.

---

## Journaling

The `journal` parameter controls whether the migration record is written
after completion:

- **`journal: true` (default):** Record is written when the migration completes.
  One-time and IContinuousMigration loops are recorded after StopAsync returns
  true. Cron migrations update the record timestamp each time they run.

- **`journal: false`:** Record is never written. The migration runs every time
  the runner executes, regardless of previous runs.

```csharp
// Cron migration: re-runs on schedule, record tracks last run time
[Migration(1000, Cron = "0 * * * *")]

// Non-journaled: runs every time, no cron check needed
[Migration(2000, journal: false)]
```

---

## Pattern Reference

| Pattern | Approach | Attribute | Behavior |
|---------|----------|-----------|----------|
| One-time | Default | `[Migration(1)]` | Run once, record, never again |
| Scheduled | Cron | `[Migration(1, Cron = "0 2 * * *")]` | Non-blocking, runs when due |
| Poll-then-run | Interface | `IContinuousMigration` | Loops until StartAsync returns true, runs once |
| Fixed iterations | Interface | `IContinuousMigration` | Loops N times via StopAsync counter |
| Legacy loop | String | `[Migration(1, "Start", "Stop")]` | Reflection-based loop |
| Always-run | Default | `[Migration(1, journal: false)]` | Runs every time, no record |

## Important Considerations

- **Cron migrations are non-blocking.** The runner checks due-ness and moves
  on. To run migrations on a schedule, invoke the runner periodically from an
  external scheduler or hosted service.

- **IContinuousMigration loops are blocking.** The runner does not proceed to
  the next migration until StopAsync returns true. Use CancellationToken to
  allow graceful shutdown.

- **Sequential execution.** Migrations run in version order. A looping migration
  at version 2000 blocks version 3000 until it completes.

- **Instance state is preserved** across loop iterations within a single runner
  execution. Instance fields (counters, flags) work as expected.

- **Cron migrations use upsert semantics.** Each execution deletes the old
  record and writes a new one with the current timestamp.

- **CancellationToken** is passed to `StartAsync`, `StopAsync`, and `UpAsync`
  for IContinuousMigration. The legacy string-based methods do not receive it.
  `CronDelayAsync` accepts an optional CancellationToken parameter.
