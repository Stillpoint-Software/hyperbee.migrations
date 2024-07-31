# Hyperbee Migrations

## Introduction

Hyperbee Migrations is a migration framework for .NET. Migrations are a structured way to alter your database
schema and are an alternative to creating lots of database scripts that have to be run manually by every
developer involved. Migrations solve the problem of evolving a database schema (and data) for multiple databases
(for example, the developer's local database, the test database and the production database). Database changes
are described in classes written in C# that can be checked into a version control system.

The framework API is heavily influenced by [Fluent Migrator](https://github.com/schambers/fluentmigrator)
and [Raven Migrations](https://github.com/migrating-ravens/RavenMigrations).

### The Runner

At the heart of migrations is the **MigrationRunner**. The migration runner scans all provided assemblies for
classes deriving from the **Migration** base class and then orders them according to their migration attribute
value.

After each migration is executed, a **MigrationRecord** is inserted into your database, unless no journaling is set. This ensures that the
next time the runner is executed, previously completed migrations are not executed again. When a migration is
rolled back the **MigrationRecord** is removed.

You can modify the runner options by passing an action to the **.AddCouchbaseMigrations** call:

```c#
// In Startup.cs
public void ConfigureServices( IServiceCollection services )
{
    services.AddCouchbaseMigrations( options =>
    {
        // Configure migration options
        options.Direction = Direction.Down;
    });
}
```

### Profiles

There are times when you may want to scope migrations to specific environments. To allow this Hyperbee Migrations
supports profiles. For instance, some migrations might only run during development. By decorating your migration
with the profile of _"development"_ and setting **options** to include only that profile, you can control which
migrations run in which environments.

```c#
[Migration(3, null,null, true,"development")]
public class DevelopmentOnlyMigration : Migration
{
    public async override Task UpAsync( CancellationToken cancellationToken = default )
    {
        // do something nice for local developers
    }
}

...

// In Startup.cs
public void ConfigureServices( IServiceCollection services )
{
    services.AddCouchbaseMigrations( options =>
    {
        // Configure to only run development migrations
         options.Profiles = new[] { "development" } };
    });
}
```

A migration may belong to multiple profiles.

```c#
[Migration(3, null,null, true,"development", "staging")]
public class TargetedMigration : Migration
{
    // ...
}
```

This migration will run if either the **development** or the **stating** profile is specified in
**MigrationOptions**.

### The Record Store

Hyperbee Migrations currently supports **Couchbase**, **MongoDB** & **Postgres** databases but it can easily be extended.
The steps are:

1. Derive from IMigrationRecordStore
2. Derive from MigrationOptions to add any store specific configuration
3. Implement ServiceCollectionExtensions to register your implementation

See the one of the current implementations for reference.

## Configure Local Solution

To run the migration solution you will need to add some local configuration.

`appsettings.developer.json`

```json
{
  "Couchbase": {
    "ConnectionString": "couchbase://localhost"
  }
}
```

`Manage User Secrets`

```json
{
  "Couchbase:UserName": "Administrator",
  "Couchbase:Password": "_YOUR_PASSWORD_"
}
```

## Using Sample Runners

Currently there is are [sample runners](../../samples) for each of the database providers.  These provider a simple console app that can be run using a command line or built into a docker image.

### Running From The Command Line

Once installed as a dotnet tool, the runner can be run from the command line. The runner expects, and will use settings
from the `appSettings.json` in the execution folder. Arguments can also be provided from the command line.

#### Command Line Options

| Switch | Alias        | Description           |
| ------ | ------------ | --------------------- |
| -f     | --file       | From Paths Array      |
| -a     | --assembly   | From Assemblies Array |
| -p     | --profile    | Profiles Array        |
| -b     | --bucket     | Bucket Name           |
| -s     | --scope      | Scope Name            |
| -c     | --collection | Collection Name       |
| -usr   | --user       | Database User         |
| -pwd   | --password   | Database Password     |
| -cs    | --connection | Database Connection String |

#### Runtime Configuration

```json
{
  "Couchbase": {
    "ConnectionString": "__CONNECTION_STRING_HERE__",
    "UserName": "__SECRET_HERE__",
    "Password": "__SECRET_HERE__",
    "MaxConnectionLimit": 20
  },
  "Migrations": {
    "BucketName": "hyperbee",
    "ScopeName": "migrations",
    "CollectionName": "ledger",
    "Lock": {
      "Enabled": false,
      "Name": "migration-runner-mutex",
      "MaxLifetime": 3600,
      "ExpireInterval": 300,
      "RenewInterval": 120
    },
    "FromPaths": [
      "c:\\my-migration-assembly.dll"
    ],
    "FromAssemblies": [
    ]
  },
  "Serilog": {
    "MinimumLevel": {
      "Default": "Debug",
      "Override": {
        "Couchbase": "Warning",
        "Microsoft": "Warning",
        "Microsoft.Hosting.Lifetime": "Information",
        "System": "Warning"
      }
    }
  }
}
```
