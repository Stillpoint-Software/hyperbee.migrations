# ADR-0004: Reflection-Based Migration Discovery with Attribute Metadata

**Status:** Accepted
**Date:** 2026-04-03

## Context

The framework needs to discover, order, and execute migrations across assemblies. Migrations must be versioned, optionally scoped to profiles, and support bidirectional execution (Up/Down).

## Decision

- Discover migrations via reflection: scan configured assemblies for types inheriting `Migration`
- Metadata via `MigrationAttribute`: `Version` (long), `Profiles`, `StartMethod`, `StopMethod`, `Journal`
- Order by `Version` ascending (Up) or descending (Down)
- Duplicate version numbers throw `DuplicateMigrationException`
- Assemblies loaded from DI configuration (`Migrations:FromAssemblies`, `Migrations:FromPaths`)

## Rationale

- Automatic discovery eliminates manual registration boilerplate
- Version numbers provide deterministic ordering and conflict detection
- Attribute metadata is idiomatic C# and discoverable via IDE tooling
- Profile filtering enables environment-specific migrations (e.g., "development" seeds)
- Assembly loading from configuration supports modular migration packages

## Consequences

- Migrations must have unique version numbers across all configured assemblies
- Version numbers are long integers, not semantic versions -- convention (1000, 2000) provides grouping
- Reflection cost is paid once at startup, not per-migration
