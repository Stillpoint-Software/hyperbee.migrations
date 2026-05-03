#nullable enable
using System;
using System.Threading;
using System.Threading.Tasks;
using FluentAssertions;
using Hyperbee.Migrations;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hyperbee.Migrations.Tests;

// ADR-0009 — convention-based record IDs.
//
// Locks the documented format so a future formatting change is a deliberate
// supersession (new ADR), not a silent drift caught only by the integration
// suite. Format: "record.<version>.<kebab-cased-type-name>".

[TestClass]
public class DefaultMigrationConventionsTests
{
    private readonly DefaultMigrationConventions _conventions = new();

    [TestMethod]
    public void GetRecordId_LowerCasesAndKebabsTypeName()
    {
        var id = _conventions.GetRecordId( new SimpleMigration() );
        id.Should().Be( "record.1.simplemigration" );
    }

    [TestMethod]
    public void GetRecordId_CollapsesUnderscoresToDashes()
    {
        var id = _conventions.GetRecordId( new __ONE_Two___Three_() );
        id.Should().Be( "record.42.one-two-three" );
    }

    [TestMethod]
    public void GetRecordId_UsesFullSemanticVersion()
    {
        var id = _conventions.GetRecordId( new VersionedMigration() );
        id.Should().Be( "record.20260503120000.versionedmigration" );
    }

    [TestMethod]
    public void GetRecordId_ThrowsWhenAttributeMissing()
    {
        var act = () => _conventions.GetRecordId( new NoAttributeMigration() );
        act.Should().Throw<MigrationException>().WithMessage( "*missing*" );
    }

    // Profile-tagged so reflection-based discovery in RunnerTests does not
    // pick these up. Runner tests don't include this profile, so these types
    // remain invisible to the runner while still being usable here directly.
    private const string TestProfile = "convention-tests-only";

    [Migration( 1, null, null, true, TestProfile )]
    private sealed class SimpleMigration : Migration
    {
        public override Task UpAsync( CancellationToken cancellationToken = default ) => Task.CompletedTask;
    }

    [Migration( 42, null, null, true, TestProfile )]
    private sealed class __ONE_Two___Three_ : Migration
    {
        public override Task UpAsync( CancellationToken cancellationToken = default ) => Task.CompletedTask;
    }

    [Migration( 20260503120000L, null, null, true, TestProfile )]
    private sealed class VersionedMigration : Migration
    {
        public override Task UpAsync( CancellationToken cancellationToken = default ) => Task.CompletedTask;
    }

    private sealed class NoAttributeMigration : Migration
    {
        public override Task UpAsync( CancellationToken cancellationToken = default ) => Task.CompletedTask;
    }
}
