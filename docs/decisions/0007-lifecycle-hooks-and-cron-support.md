# ADR-0007: Lifecycle Hooks and Cron Support

**Status:** Accepted
**Date:** 2026-04-03

## Context

Some migrations need conditional execution (wait for external dependency), scheduled repetition (periodic cleanup), or pre/post-execution logic that doesn't belong in `UpAsync` itself.

## Decision

- `MigrationAttribute` accepts optional `StartMethod` and `StopMethod` names
- Methods discovered via reflection on the migration instance
- Must return `Task<bool>`: true = proceed/continue, false = wait and retry
- `MigrationCronHelper` provides `CronDelayAsync(cronExpression)` for scheduled execution using the Cronos library
- `Journal` property on attribute controls whether the migration is recorded (non-journaled migrations re-run every time)

## Rationale

- Reflection keeps the `Migration` base class minimal (only `UpAsync`/`DownAsync`)
- Lifecycle methods are optional -- most migrations don't need them
- Cron support enables scheduled maintenance migrations without external schedulers
- Non-journaled + cron = repeating migration (e.g., "clean up temp data every hour")

## Consequences

- Lifecycle methods must match exact signature (`Task<bool>`) or they won't be found
- Cron delay blocks the migration runner until the next cron occurrence
- StartMethod/StopMethod names are strings -- no compile-time checking
