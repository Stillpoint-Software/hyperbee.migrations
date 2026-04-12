# ADR-0010: Dual-Tier Testing Strategy (Unit + Integration with Testcontainers)

**Status:** Accepted
**Date:** 2026-04-03

## Context

Migration tooling operates against real databases with provider-specific behavior. Unit tests alone can't validate database interactions, but integration tests are slow and require Docker.

## Decision

Two test tiers:

**Unit tests** (`Hyperbee.Migrations.Tests`):
- MSTest framework
- Focus on parsers, options, core logic (no database dependency)
- Run on every build, fast feedback
- 105 tests across 3 target frameworks

**Integration tests** (`Hyperbee.Migrations.Integration.Tests`):
- Testcontainers (Docker-based throwaway databases)
- Per-provider container infrastructure: test container + migration container
- Test containers start real databases; migration containers build Docker images from sample runners
- Verify log output AND direct database reads for correctness
- Gated by `#if INTEGRATIONS` preprocessor directive -- disabled by default
- Parallelized at method level (`[Parallelize(Workers = 0)]`)

**Integration test pattern:**
1. `BuildMigrationImageAsync()` -- build Docker image from sample Dockerfile
2. `BuildMigrationsAsync()` -- create container with environment config
3. `StartAsync()` + `GetLogsAsync()` -- run and capture output
4. Assert log messages AND query database directly for verification

## Rationale

- Parsers are the most logic-dense code -- unit tests provide fast, precise coverage
- Docker-based integration tests are reproducible across CI/CD environments
- Sample runners serve dual purpose: documentation AND test fixtures
- `#if INTEGRATIONS` gate prevents accidental slow CI runs
- Direct database reads (not just log assertions) catch silent failures

## Consequences

- Integration tests require Docker -- won't run in restricted environments
- Sample migrations must be kept in sync with integration test assertions
- Assembly initialization starts all containers sequentially (startup cost ~30-60s)
