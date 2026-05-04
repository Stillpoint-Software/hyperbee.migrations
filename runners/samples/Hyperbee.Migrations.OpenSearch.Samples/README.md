# Hyperbee.Migrations.OpenSearch.Samples

Reference migration set demonstrating every v1 verb of the OpenSearch
provider (R-27). Each migration is self-contained and idempotent against
a fresh cluster — the `Hyperbee.MigrationRunner.OpenSearch` runner loads
this assembly via `Migrations:FromPaths` and runs them in version order.

| # | Migration | Verbs / behavior demonstrated | Body-source form (ADR-0017) |
|---|-----------|-------------------------------|------------------------------|
| 1000 | `CreateInitialIndex` | `CREATE INDEX` with body, auto `dynamic:strict`, `WAIT FOR` | Form 2 — inline `bodies` section |
| 2000 | `AliasSwapReindexHandComposed` | Long-form zero-downtime reindex (CREATE + REINDEX + ALIAS SWAP) | Form 2 — inline `bodies` for each |
| 3000 | `ComponentAndIndexTemplate` | `CREATE COMPONENT` + `CREATE TEMPLATE` with `composed_of` | **Mixed: form 3 (`bodies.x: "@path"`) + form 2** |
| 4000 | `IsmPolicyAndApply` | ISM `CREATE POLICY` + `APPLY POLICY` to existing indices | **Form 1 — direct `WITH BODY @path`** |
| 5000 | `ConditionalVersion` | `WHEN VERSION` semver-correct conditional execution (R-15a) | Form 2 |
| 6000 | **`MigrateIndexComposite`** | **Featured: `MIGRATE INDEX` composite — the canonical template-propagation pattern (R-30)** | Form 2 |
| 7000 | `ReversibleAlias` | Opt-in `rollback` per statement; partial-rollback ledger semantics (R-19) | (no bodies — DDL-only rollback) |
| 8000 | `UnsafeReindex` | `REINDEX UNSAFE("<justification>")` — opt-out of `op_type:create` | Form 2 |

**Sample 6 is the headline.** Adopters asking "how do I apply a template/mapping
change to existing data?" should be pointed at `MigrateIndexComposite` first;
the long-form sample 2 exists to show what the composite expands to.

**Body-source forms.** ADR-0017 defines three resolution forms for `WITH BODY`
references. The samples deliberately demonstrate all of them so authors can
compare the trade-offs side by side:

- **Form 1** — `WITH BODY @bodies/file.json` directly in the statement string.
  Best for any body large enough to dominate `statements.json` if inlined
  (sample 4: ISM policies routinely run 100+ lines in production).
- **Form 2** — `WITH BODY $name` resolved against a `bodies.<name>` inline
  JSON object. Best for tiny bodies tightly coupled to one statement
  (samples 1, 2, 5, 6, 8).
- **Form 3** — `WITH BODY $name` where `bodies.<name>` is a `"@path"` string.
  Best when you want to address bodies by name AND keep them in their own
  files (sample 3, mixed with form 2 to show coexistence).

For the full grammar and resolution rules, see the provider README's
"Body references" section.

## Running

```bash
cd ../../Hyperbee.MigrationRunner.OpenSearch
dotnet run
```

The runner's default `appsettings.json` already points
`Migrations:FromPaths` at this samples assembly's compiled DLL.
