# Hyperbee.Migrations.Providers.Postgres.Squash

Postgres `ISquashProvider` implementation for the `hyperbee-migrations`
CLI. Reference this package from a migration project to enable
`hyperbee-migrations squash --provider postgres` codegen against your
migration assembly.

This package is independent from the main
`Hyperbee.Migrations.Providers.Postgres` package so that production
deployments which only apply migrations do not pull in Testcontainers /
Docker runtime dependencies. Add this reference only when you want
operator-facing squash codegen.

## Install

```bash
dotnet add package Hyperbee.Migrations.Providers.Postgres.Squash
```

The CLI discovers the provider via the migration assembly's reference
closure (per ADR-0024); no manual registration is required.
