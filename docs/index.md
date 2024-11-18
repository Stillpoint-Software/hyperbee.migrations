---
layout: default
title: Hyperbee Migrations
nav_order: 1
---

# Welcome to Hyperbee Migrations

Hyperbee Migrations is a migration framework for .NET. Migrations are a structured way to alter your database
schema and are an alternative to creating lots of database scripts that have to be run manually by every
developer involved. Migrations solve the problem of evolving a database schema (and data) for multiple databases
(for example, the developer's local database, the test database and the production database). Database changes
are described in classes written in C# that can be checked into a version control system.


The Cron Helper uses HangFire Cronos.

## Features include:

* Easy integration
* Supports **Couchbase**, **MongoDB** and **Postgresql**
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

## A Migration Example

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
## Getting Started

To get started with Hyperbee.Migrations, refer to the documentation for detailed instructions and examples. Install the library via NuGet:

Install via NuGet:

```bash
dotnet add package Hyperbee.Migrations
```

## Credits

Hyperbee.Migrations framework API is heavily influenced by [Fluent Migrator](https://github.com/schambers/fluentmigrator), [Raven Migrations](https://github.com/migrating-ravens/RavenMigrations) and [DbUp](https://github.com/DbUp/DbUp).  Special thanks to:

- HangFire Cronos [HangFire Cronos](https://github.com/HangfireIO/Cronos)
- Couchbase .Net Client & Couchbase Extentions DI [Couchbase .Net Client](https://github.com/couchbase/couchbase-net-client)
- Raven Migrations [Raven Migrations](https://github.com/migrating-ravens/RavenMigrations)



## Contributing

We welcome contributions! Please see our [Contributing Guide](https://github.com/Stillpoint-Software/.github/blob/main/.github/CONTRIBUTING.md) 
for more details.
