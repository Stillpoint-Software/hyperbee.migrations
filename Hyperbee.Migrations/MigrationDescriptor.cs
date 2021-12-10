using System;

namespace Hyperbee.Migrations;

internal class MigrationDescriptor
{
    public Func<Migration> Migration { get; set; }
    public MigrationAttribute Attribute { get; set; }
}