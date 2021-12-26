using System;

namespace Hyperbee.Migrations;

public class MigrationRecord : IMigrationRecord
{
    public string Id { get; init; }
    public DateTimeOffset RunOn { get; init; } = DateTimeOffset.UtcNow;
}