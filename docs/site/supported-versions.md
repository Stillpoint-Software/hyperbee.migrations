---
layout: default
title: Supported Versions
nav_order: 16
---

# Supported Versions

This page documents the .NET runtimes, provider servers, and client SDK
package versions that Hyperbee.Migrations is built and tested against. It
is intended for operators evaluating whether their environment is
supported and for developers deciding which target framework or provider
version to validate their migrations against. Numbers below are taken
directly from the source repository — the integration test containers
under `tests/Hyperbee.Migrations.Integration.Tests/Container/` and the
central package list in `Directory.Packages.props`.

## .NET runtimes

| Runtime          | Status    |
|------------------|-----------|
| .NET 8.0 (LTS)   | Supported |
| .NET 9.0 (STS)   | Supported |
| .NET 10.0 (LTS)  | Supported |

The core library and every provider package multi-target `net8.0`,
`net9.0`, and `net10.0`. The unit test matrix runs against all three
target frameworks on every push and pull request, so a passing build
implies coverage on the full set rather than just the most recent
runtime.

## Provider servers

The "Tested against" column lists the Docker image or version that the
integration test containers spin up on each CI run. The "Minimum
supported" column is a conservative floor — typically one major version
below the tested version — that the provider implementations are
expected to function against. Operators on older releases should treat
the floor as a soft guarantee and validate against their own fixtures.

| Provider   | Tested against                          | Minimum supported | Notes                                                                                                                                                              |
|------------|-----------------------------------------|-------------------|--------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| Aerospike  | `aerospike/aerospike-server:latest` (CE 7+) | 6.x               | Lock and ledger sets are created on first run. The namespace must allow records with TTL — set `DEFAULT_TTL > 0` if using the official Docker image (see test container).         |
| Couchbase  | `couchbase/server:enterprise-7.6.4` (Testcontainers default) | 7.0               | Provider creates bucket / scope / collection on first run. The configured user needs roles with bucket-create and collection-create.                               |
| MongoDB    | `mongo:7.0` (Testcontainers default)    | 6.0               | Replica set or standalone are both fine. The provider does not require multi-document transactions.                                                                |
| OpenSearch | `opensearchproject/opensearch:2.18.0`   | 2.0               | AWS Managed OpenSearch is supported via the AWS extension package using SigV4 auth. The bootstrap probes `_plugins/_ism` vs `_opendistro/_ism` for AWS domain compatibility. |
| PostgreSQL | `postgres:15.1` (Testcontainers default) | 14                | The configured role needs `CREATE` on the target schema during `InitializeAsync` so the runner can create its ledger table.                                        |

## Client SDK package versions

These are the central-managed versions in `Directory.Packages.props`.
Override at the project level only when a specific consumer constraint
requires it; mismatches between the runner and the provider library are
not supported.

| Package                                       | Version |
|-----------------------------------------------|---------|
| Aerospike.Client                              | 8.2.0   |
| CouchbaseNetClient                            | 3.8.1   |
| Couchbase.Extensions.DependencyInjection      | 3.8.1   |
| Couchbase.Extensions.Locks                    | 2.1.0   |
| MongoDB.Driver                                | 3.6.0   |
| MongoDB.Bson                                  | 3.6.0   |
| OpenSearch.Client                             | 1.8.0   |
| OpenSearch.Net                                | 1.8.0   |
| OpenSearch.Net.Auth.AwsSigV4                  | 1.8.0   |
| Npgsql                                        | 10.0.1  |
| Npgsql.DependencyInjection                    | 10.0.1  |

## NuGet versioning policy

- The library uses semantic versioning. Major versions ship breaking API
  changes.
- Provider packages move in lockstep with the core `Hyperbee.Migrations`
  package on major version bumps, so a single major across the whole
  solution is the unit of upgrade.
- Patch and minor versions add behavior without breaking source
  compatibility.
- See [CHANGELOG.md](https://github.com/Stillpoint-Software/hyperbee.migrations/blob/main/CHANGELOG.md)
  for release-by-release detail.

## Operating systems

The library is pure .NET and runs anywhere .NET runs. Integration tests
run on Linux (Ubuntu 24.04 in CI, Windows + WSL2 or native Linux
locally). Docker engine is required for integration tests; Docker
Desktop on macOS and Windows or native Docker on Linux all work.

## End-of-support policy

- We follow Microsoft's .NET support lifecycle: LTS releases are
  supported for 3 years, STS for 18 months. After a runtime falls out
  of Microsoft's support window it drops from the test matrix and may
  be dropped from the multi-target list at the next major version.
- Provider versions: the minimum supported version moves up at most
  once per major version, with at least 6 months of advance notice in
  the changelog before the floor changes.
