# Hyperbee Migrations Aerospike Provider

Aerospike provider for Hyperbee Migrations. Adds support for running migrations against Aerospike databases.

## Features

- Migration tracking via Aerospike sets
- Distributed locking for concurrent migration safety
- Resource migrations: statement execution and document seeding
- Parlot-based AQL statement parser with support for async index creation
- Directive support: `@RECREATE` and `@WAITREADY` for index management
