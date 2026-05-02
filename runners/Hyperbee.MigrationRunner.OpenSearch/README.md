# Hyperbee.MigrationRunner.OpenSearch

Command-line migration runner for OpenSearch. Loads migration assemblies at runtime and executes pending migrations against an OpenSearch cluster.

## Prerequisites

- .NET 10 SDK
- A running OpenSearch cluster (single-node or multi-node; OpenSearch 2.0+)

## Configuration

Configure via `appsettings.json`, `appsettings.<ENV>.json`, environment variables, or command-line flags. Configuration sources are layered: command line > env > env-specific JSON > base JSON.

| Key | Description | Default |
|-----|-------------|---------|
| `OpenSearch:ConnectionString` | Cluster URL | `http://localhost:9200` |
| `OpenSearch:Authentication:Mode` | Auth mode: `Anonymous` \| `Basic` \| `ApiKey` \| `ClientCertificate` | `Anonymous` |
| `OpenSearch:Authentication:UserName` | Basic-auth username | |
| `OpenSearch:Authentication:Password` | Basic-auth password (use user-secrets in dev) | |
| `OpenSearch:Authentication:ApiKeyId` | OpenSearch security-plugin API key id | |
| `OpenSearch:Authentication:ApiKey` | OpenSearch security-plugin API key secret | |
| `OpenSearch:Authentication:ClientCertificatePath` | Path to a PFX/PKCS12 client cert (mTLS) | |
| `OpenSearch:Authentication:ClientCertificatePassword` | PFX password, if any | |
| `Migrations:LedgerIndex` | Ledger index name | `.migrations` |
| `Migrations:LockIndex` | Lock index name | `.migrations-lock` |
| `Migrations:LockName` | Lock document id | `migration_lock` |
| `Migrations:Lock:Enabled` | Enable distributed locking | `false` |
| `Migrations:ForceResume` | Bypass partially_rolled_back lockout (R-19) | `false` |
| `Migrations:FromPaths` | Migration assembly file paths | |
| `Migrations:FromAssemblies` | Migration assembly names | |
| `Migrations:Profiles` | Active migration profiles | |

The runner accepts the legacy flat `OpenSearch:UserName` / `OpenSearch:Password` keys without an explicit `Authentication:Mode` and treats them as Basic. New deployments should use the `Authentication:*` section so the mode is explicit.

## Running Locally

```bash
dotnet run
```

## Running with Docker

```bash
docker build -t opensearch-migrations -f Dockerfile ../..
docker run opensearch-migrations
```

## CLI Arguments

| Flag | Description |
|------|-------------|
| `-cs`, `--connection` | OpenSearch connection string |
| `--auth-mode` | Auth mode: `Anonymous` \| `Basic` \| `ApiKey` \| `ClientCertificate` (case-insensitive) |
| `-u`, `--user` | Basic-auth username |
| `--password` | Basic-auth password |
| `--api-key-id` | API key id (mode `ApiKey`) |
| `--api-key` | API key secret (mode `ApiKey`) |
| `--client-cert` | Path to PFX client cert (mode `ClientCertificate`) |
| `--client-cert-password` | PFX password, if any |
| `--ledger` | Ledger index name |
| `--lock` | Lock index name |
| `--lock-name` | Lock document id |
| `--force-resume` | R-19 recovery: bypass `partially_rolled_back` lockout |
| `-f`, `--file` | Migration assembly paths (repeat for multiple) |
| `-a`, `--assembly` | Migration assembly names (repeat for multiple) |
| `-p`, `--profile` | Migration profiles (repeat for multiple) |

## Recovering from a partial rollback (R-19)

When a `Down` direction halts partway through a rollback sequence, the
ledger entry for that migration is overwritten to `status: partially_rolled_back`
with `failedStatementIndex` pointing at the failing statement. Subsequent
runs in EITHER direction are refused with `OpenSearchPartialRollbackException`
and a remediation message — silent retry could leave the cluster in an
indeterminate intermediate state.

To recover:

1. **Inspect** the ledger entry to identify the failing statement:
   ```bash
   curl -s http://localhost:9200/.migrations/_doc/<migration-id>?pretty
   ```
2. **Reconcile** cluster state manually so the rollback can complete cleanly
   from the failing index onward.
3. **Re-run** with `--force-resume`:
   ```bash
   dotnet run -- --force-resume true
   ```
   The lockout is bypassed for this run only; the ledger entry is rewritten
   to its final state by the next successful Up or full Down.

For a fresh `Up` re-execution rather than a rollback retry, delete the
ledger entry by id before re-running. That is more disruptive, so the
runner does not provide a flag for it.

## Sample Migrations

This runner loads migrations from the companion `Hyperbee.Migrations.OpenSearch.Samples` project via the `FromPaths` configuration. See `../samples/Hyperbee.Migrations.OpenSearch.Samples/` for example migrations covering every v1 verb.
