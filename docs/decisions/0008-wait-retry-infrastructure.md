# ADR-0008: Composable Wait/Retry Infrastructure

**Status:** Accepted
**Date:** 2026-04-03

## Context

Database operations during migrations often require waiting for asynchronous processes: index builds completing, cluster nodes becoming healthy, N1QL visibility propagation, and bucket provisioning. A consistent retry/wait mechanism is needed across providers.

## Decision

Strategy pattern with three components:

- **`RetryStrategy`** base class: `Delay` (current interval), `Backoff` (func computing next delay), `WaitAction` (logging hook)
- **`BackoffRetryStrategy`**: Exponential backoff (doubles each wait, capped at max). Default: 100ms first, 120s max
- **`PauseRetryStrategy`**: Fixed interval. Default: 1000ms
- **`WaitHelper.WaitUntilAsync()`**: Polls a condition function with retry strategy and timeout
- **`TimeoutTokenSource`**: Helper to create `CancellationTokenSource` from nullable `TimeSpan`

## Rationale

- Polly was considered but adds an external dependency for a narrow use case
- Strategy pattern allows different backoff curves per situation (aggressive for index checks, gentle for cluster health)
- Linked cancellation tokens compose timeout + caller cancellation cleanly
- `RetryTimeoutException` and `RetryStrategyException` distinguish timeout from callback failure

## Consequences

- Used extensively in Couchbase (bootstrapper, bucket health, N1QL visibility) and Aerospike (index readiness)
- Timeout defaults must be generous enough for slow environments (CI, cold starts)
- `WaitAction` hook enables structured logging of retry behavior without coupling to a logger
