# Hyperbee.Migrations.OpenSearch.Samples

Reference migration set demonstrating every v1 verb of the OpenSearch
provider (R-27). Each migration is self-contained and idempotent against
a fresh cluster — the `Hyperbee.MigrationRunner.OpenSearch` runner loads
this assembly via `Migrations:FromPaths` and runs them in version order.

| # | Migration | Demonstrates |
|---|-----------|--------------|
| 1000 | `CreateInitialIndex` | `CREATE INDEX` with body, auto `dynamic:strict`, `WAIT FOR` |
| 2000 | `AliasSwapReindexHandComposed` | Long-form zero-downtime reindex (CREATE + REINDEX + ALIAS SWAP) |
| 3000 | `ComponentAndIndexTemplate` | `CREATE COMPONENT` + `CREATE TEMPLATE` with `composed_of` |
| 4000 | `IsmPolicyAndApply` | ISM `CREATE POLICY` + `APPLY POLICY` to existing indices |
| 5000 | `ConditionalVersion` | `WHEN VERSION` semver-correct conditional execution (R-15a) |
| 6000 | **`MigrateIndexComposite`** | **Featured: `MIGRATE INDEX` composite — the canonical template-propagation pattern (R-30)** |
| 7000 | `ReversibleAlias` | Opt-in `rollback` per statement; partial-rollback ledger semantics (R-19) |
| 8000 | `UnsafeReindex` | `REINDEX UNSAFE("<justification>")` — opt-out of `op_type:create` |

**Sample 6 is the headline.** Adopters asking "how do I apply a template/mapping
change to existing data?" should be pointed at `MigrateIndexComposite` first;
the long-form sample 2 exists to show what the composite expands to.

## Running

```bash
cd ../../Hyperbee.MigrationRunner.OpenSearch
dotnet run
```

The runner's default `appsettings.json` already points
`Migrations:FromPaths` at this samples assembly's compiled DLL.
