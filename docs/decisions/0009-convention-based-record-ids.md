# ADR-0009: Convention-Based Record ID Generation

**Status:** Accepted
**Date:** 2026-04-03

## Context

Migration records need stable, human-readable identifiers stored in the database. IDs must survive class renames (within reason) and be consistent across environments.

## Decision

- `IMigrationConventions` interface with `GetRecordId(Migration)` method
- `DefaultMigrationConventions`: generates `{version}.{normalized-name}` (lowercase, underscores to hyphens)
- Example: class `M1000_CreateInitialBuckets` with `[Migration(1000)]` produces `1000.m1000-createinitialbuckets`
- `Migration.VersionedName<T>()` static helper returns `{version}-{ClassName}` for resource path resolution
- Conventions are swappable via `MigrationOptions.Conventions`

## Rationale

- Human-readable IDs are valuable in database inspection and debugging
- Normalization (lowercase, hyphens) prevents platform-specific casing issues
- Swappable conventions allow projects with existing naming schemes to migrate
- Version prefix ensures sortability in database views

## Consequences

- Renaming a migration class changes its record ID -- existing records won't match
- Convention must be consistent across all environments (dev, staging, prod)
- Resource paths depend on `VersionedName`, not record IDs -- they're separate concerns
