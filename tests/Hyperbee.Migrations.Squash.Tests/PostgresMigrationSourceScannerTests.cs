using FluentAssertions;
using Hyperbee.Migrations.Providers.Postgres.Squash;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 6 follow-up + ADR-0019 A5: Roslyn-based migration source scanner.

[TestClass]
public class PostgresMigrationSourceScannerTests
{
    private string _tempDir = null!;

    [TestInitialize]
    public void Setup()
    {
        _tempDir = Path.Combine( Path.GetTempPath(), "hb-roslyn-scan-" + Guid.NewGuid().ToString( "n" ) );
        Directory.CreateDirectory( _tempDir );
    }

    [TestCleanup]
    public void Cleanup()
    {
        if ( Directory.Exists( _tempDir ) )
            Directory.Delete( _tempDir, recursive: true );
    }

    [TestMethod]
    public void Scanner_ClassWithDmlAndNoAnnotation_RequiresAnnotation()
    {
        WriteSource( "Migration1000.cs", """
            using Hyperbee.Migrations;

            namespace MyApp;

            [Migration(1000)]
            public class SeedInitialUsers : Migration
            {
                public override async Task UpAsync(CancellationToken ct = default)
                {
                    var sql = "INSERT INTO users (email) VALUES ('admin@example.com')";
                    // ... execute
                }
            }
            """ );

        var verdicts = PostgresMigrationSourceScanner.Scan( _tempDir );
        verdicts.Should().HaveCount( 1 );
        var v = verdicts[0];

        v.ClassName.Should().Be( "SeedInitialUsers" );
        v.ExtendsMigration.Should().BeTrue();
        v.HasMigrationAttribute.Should().BeTrue();
        v.HasDataMigrationAttribute.Should().BeFalse();
        v.HasStructuralOnlyAttribute.Should().BeFalse();
        v.LooksLikeDataOp.Should().BeTrue();
        v.DataOpHits.Should().Contain( s => s.StartsWith( "INSERT INTO" ) );
        v.RequiresAnnotation.Should().BeTrue();
    }

    [TestMethod]
    public void Scanner_ClassWithDmlAndDataMigrationAttr_DoesNotRequireAnnotation()
    {
        WriteSource( "Migration1010.cs", """
            using Hyperbee.Migrations;

            namespace MyApp;

            [Migration(1010)]
            [DataMigration]
            public class SeedInitialUsers : Migration
            {
                public override async Task UpAsync(CancellationToken ct = default)
                {
                    var sql = "INSERT INTO users VALUES ('admin')";
                }
            }
            """ );

        var verdicts = PostgresMigrationSourceScanner.Scan( _tempDir );
        verdicts.Should().HaveCount( 1 );
        verdicts[0].HasDataMigrationAttribute.Should().BeTrue();
        verdicts[0].RequiresAnnotation.Should().BeFalse();
    }

    [TestMethod]
    public void Scanner_ClassWithStructuralDdl_DoesNotRequireAnnotation()
    {
        WriteSource( "Migration1020.cs", """
            using Hyperbee.Migrations;

            namespace MyApp;

            [Migration(1020)]
            public class CreateOrdersTable : Migration
            {
                public override async Task UpAsync(CancellationToken ct = default)
                {
                    var sql = "CREATE TABLE orders (id bigint PRIMARY KEY)";
                }
            }
            """ );

        var verdicts = PostgresMigrationSourceScanner.Scan( _tempDir );
        verdicts[0].LooksLikeDataOp.Should().BeFalse();
        verdicts[0].RequiresAnnotation.Should().BeFalse();
    }

    [TestMethod]
    public void Scanner_ClassWithNonDeterminism_RequiresAnnotation()
    {
        WriteSource( "Migration1030.cs", """
            using System;
            using Hyperbee.Migrations;

            namespace MyApp;

            [Migration(1030)]
            public class TimestampedSeed : Migration
            {
                public override async Task UpAsync(CancellationToken ct = default)
                {
                    var now = DateTime.UtcNow;
                    var id = Guid.NewGuid();
                }
            }
            """ );

        var verdicts = PostgresMigrationSourceScanner.Scan( _tempDir );
        verdicts[0].NonDeterminismHits.Should().Contain( "DateTime.UtcNow" );
        verdicts[0].NonDeterminismHits.Should().Contain( "Guid.NewGuid" );
        verdicts[0].RequiresAnnotation.Should().BeTrue();
    }

    [TestMethod]
    public void Scanner_ClassWithStructuralOnlyAttr_DoesNotRequireAnnotation()
    {
        WriteSource( "Migration1040.cs", """
            using Hyperbee.Migrations;

            namespace MyApp;

            [Migration(1040)]
            [StructuralOnly]
            public class IdempotentDdl : Migration
            {
                public override async Task UpAsync(CancellationToken ct = default)
                {
                    // No DML; classifier might or might not see structural keywords
                }
            }
            """ );

        var verdicts = PostgresMigrationSourceScanner.Scan( _tempDir );
        verdicts[0].HasStructuralOnlyAttribute.Should().BeTrue();
        verdicts[0].RequiresAnnotation.Should().BeFalse();
    }

    [TestMethod]
    public void Scanner_NonMigrationClass_NotFlagged()
    {
        WriteSource( "Helper.cs", """
            namespace MyApp;
            public class Helper
            {
                public void DoStuff()
                {
                    var sql = "INSERT INTO foo VALUES (1)";
                }
            }
            """ );

        var verdicts = PostgresMigrationSourceScanner.Scan( _tempDir );
        verdicts[0].ClassName.Should().Be( "Helper" );
        verdicts[0].ExtendsMigration.Should().BeFalse();
        verdicts[0].RequiresAnnotation.Should().BeFalse();
    }

    [TestMethod]
    public void Scanner_InterpolatedString_DetectsDml()
    {
        WriteSource( "Migration1050.cs", """
            using Hyperbee.Migrations;

            namespace MyApp;

            [Migration(1050)]
            public class InterpolatedDml : Migration
            {
                public override async Task UpAsync(CancellationToken ct = default)
                {
                    var table = "users";
                    var sql = $"DELETE FROM {table} WHERE created_at < now()";
                }
            }
            """ );

        var verdicts = PostgresMigrationSourceScanner.Scan( _tempDir );
        verdicts[0].LooksLikeDataOp.Should().BeTrue();
        verdicts[0].DataOpHits.Should().Contain( s => s.StartsWith( "DELETE FROM" ) );
    }

    [TestMethod]
    public void Scanner_RandomConstructorWithoutSeed_FlaggedAsNonDeterministic()
    {
        WriteSource( "Migration1060.cs", """
            using System;
            using Hyperbee.Migrations;

            namespace MyApp;

            [Migration(1060)]
            public class RandomSeed : Migration
            {
                public override async Task UpAsync(CancellationToken ct = default)
                {
                    var r1 = new Random();
                    var seeded = new Random(42); // not flagged
                }
            }
            """ );

        var verdicts = PostgresMigrationSourceScanner.Scan( _tempDir );
        verdicts[0].NonDeterminismHits.Should().Contain( "new Random()" );
        verdicts[0].RequiresAnnotation.Should().BeTrue();
    }

    [TestMethod]
    public void Scanner_SkipsBinAndObjFolders()
    {
        Directory.CreateDirectory( Path.Combine( _tempDir, "obj" ) );
        Directory.CreateDirectory( Path.Combine( _tempDir, "bin" ) );

        File.WriteAllText( Path.Combine( _tempDir, "obj", "Generated.cs" ),
            "[Migration(9999)]\npublic class Generated : Migration { }" );
        File.WriteAllText( Path.Combine( _tempDir, "bin", "Compiled.cs" ),
            "[Migration(9998)]\npublic class Compiled : Migration { }" );

        WriteSource( "Real.cs", "[Migration(1000)] public class Real : Migration { }" );

        var verdicts = PostgresMigrationSourceScanner.Scan( _tempDir );
        verdicts.Should().ContainSingle();
        verdicts[0].ClassName.Should().Be( "Real" );
    }

    private void WriteSource( string fileName, string source ) =>
        File.WriteAllText( Path.Combine( _tempDir, fileName ), source );
}
