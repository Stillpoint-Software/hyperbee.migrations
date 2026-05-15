# Hyperbee.Migrations.Providers.MongoDB.Squash

MongoDB `ISquashProvider` implementation for the `hyperbee-migrations`
CLI. Reference this package from a migration project to enable
`hyperbee-migrations squash --provider mongodb` codegen against your
migration assembly.

This package is independent from the main
`Hyperbee.Migrations.Providers.MongoDB` package so that production
deployments which only apply migrations do not pull in Testcontainers /
Docker runtime dependencies. Add this reference only when you want
operator-facing squash codegen.

## Install

```bash
dotnet add package Hyperbee.Migrations.Providers.MongoDB.Squash
```

The CLI discovers the provider via the migration assembly's reference
closure (per ADR-0024); no manual registration is required.
