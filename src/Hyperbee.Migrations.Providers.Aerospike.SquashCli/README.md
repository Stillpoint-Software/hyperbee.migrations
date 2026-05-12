# Hyperbee.Migrations.Providers.Aerospike.SquashCli

Aerospike `ISquashCliProvider` implementation for the `hyperbee-migrations`
CLI. Reference this package from a migration project to enable
`hyperbee-migrations squash --provider aerospike` codegen against your
migration assembly.

This package is independent from the main
`Hyperbee.Migrations.Providers.Aerospike` package so that production
deployments which only apply migrations do not pull in Testcontainers /
Docker runtime dependencies. Add this reference only when you want
operator-facing squash codegen.

## Install

```bash
dotnet add package Hyperbee.Migrations.Providers.Aerospike.SquashCli
```

The CLI discovers the provider via the migration assembly's reference
closure (per ADR-0024); no manual registration is required.
