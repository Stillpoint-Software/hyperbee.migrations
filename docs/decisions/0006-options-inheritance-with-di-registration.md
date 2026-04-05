# ADR-0006: Options Inheritance Hierarchy with DI Registration

**Status:** Accepted
**Date:** 2026-04-03

## Context

Each provider needs its own configuration (connection details, lock settings, storage names) while sharing common settings (direction, profiles, assemblies, conventions).

## Decision

- Base `MigrationOptions` holds common settings: `Direction`, `Assemblies`, `Profiles`, `ToVersion`, `LockingEnabled`, `MigrationActivator`, `Conventions`
- Each provider extends with specific settings (e.g., `MongoDBMigrationOptions` adds `DatabaseName`, `CollectionName`)
- DI registration via `ServiceCollectionExtensions.Add{Provider}Migrations()`:
  - Factory creates provider options with `DefaultMigrationActivator`
  - User configuration action applied
  - Assemblies merged from code + `IConfiguration` (`FromAssemblies`, `FromPaths`)
  - Options registered as singleton; upcast to `MigrationOptions` for `MigrationRunner`

## Rationale

- Inheritance provides consistent base without duplication
- Factory pattern in DI allows access to `IServiceProvider` and `IConfiguration`
- Assembly loading from configuration enables deployment-time migration discovery
- `DefaultMigrationActivator` wraps `ActivatorUtilities.CreateInstance` for constructor DI in migrations

## Consequences

- Adding a new provider requires: options class, record store, DI extensions (established pattern)
- Options are singleton -- not reconfigurable after startup
- `Deconstruct` methods on options enable tuple unpacking for convenience
