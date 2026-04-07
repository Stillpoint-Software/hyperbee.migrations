# Hyperbee Migrations

## Introduction

Hyperbee Migrations is a migration framework for .NET. Migrations are a structured way to alter your database
schema and are an alternative to creating lots of database scripts that have to be run manually by every
developer involved. Migrations solve the problem of evolving a database schema (and data) for multiple databases
(for example, the developer's local database, the test database and the production database). Database changes
are described in classes written in C# that can be checked into a version control system.

The framework API is heavily influenced by [Fluent Migrator](https://github.com/schambers/fluentmigrator), [Raven Migrations](https://github.com/migrating-ravens/RavenMigrations) and [DbUp](https://github.com/DbUp/DbUp)

The Cron Helper uses HangFire Cronos.

### Features include:

* Easy integration
* Supports **Aerospike**, **Couchbase**, **MongoDB** and **PostgreSQL**
* Resource Migrations
    * Migrations can be defined as embedded resource files (SQL, N1QL, AQL, MongoDB commands, JSON documents) alongside code-based migrations, enabling database changes without recompilation.
* Preventing simultaneous migrations
    * By default, Hyperbee Migrations prevents parallel migration runner execution.
* Profiles 
    * There are times when you may want to scope migrations to specific environments.
* A Record Store
    * Keeps list of migrations that have completed
* Local Solutions
    * Run a migration locally
* Run from Command Line
    * Run a migration at the command line
* Cron Helper
    * Run a migration based on a start and stop criteria using a cron setting
* Journaling
    * You can determine whether or not to journal the migration

### A Migration Example

A migration looks like the following:

```c#
// #1 - specify the migration number
[Migration(1)]
public class PeopleHaveFullNames : Migration // #2 inherit from Migration
{
    // #3 do the migration
    public async override Task UpAsync( CancellationToken cancellationToken = default )
    {
    }

    // #4 optional: undo the migration
    public async override Task DownAsync( CancellationToken cancellationToken = default )
    {
    }
}

```

### Project Structure

| Path | Description |
|------|-------------|
| `src/` | Core migration libraries and provider implementations |
| `runners/` | Provider-specific migration runners |
| `runners/samples/` | Sample migrations for each provider |
| `tests/` | Unit and integration tests |

# Build Requirements

* To build and run this project, **.NET 8, 9 or 10 SDK** is required.
* Ensure your development tools are compatible with .NET 8, 9 or 10.

## Building the Solution

* With .NET 8, 9 or 10 SDK installed, you can build the solution using the standard `dotnet build` command.


# Status

| Branch     | Action                                                                                                                                                                                                                      |
|------------|-----------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------------|
| `develop`  | [![Build status](https://github.com/Stillpoint-Software/hyperbee.migrations/actions/workflows/pack_publish.yml/badge.svg?branch=develop)](https://github.com/Stillpoint-Software/hyperbee.migration/actions/workflows/pack_publish.yml)  |
| `main`     | [![Build status](https://github.com/Stillpoint-Software/hyperbee.migrations/actions/workflows/pack_publish.yml/badge.svg)](https://github.com/Stillpoint-Software/hyperbee.migration/actions/workflows/pack_publish.yml)                 |
