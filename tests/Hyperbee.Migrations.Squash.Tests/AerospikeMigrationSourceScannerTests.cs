using FluentAssertions;
using Hyperbee.Migrations.Providers.Aerospike.Squash;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 1 Sev 1 D: AerospikeMigrationSourceScanner (ADR-0019 A5).
//
// The scanner walks user migration source files and refuses squash generation
// when a Migration-derived class contains data-op call sites without an
// explicit [DataMigration] or [StructuralOnly] annotation.

[TestClass]
public class AerospikeMigrationSourceScannerTests
{
    [TestMethod]
    public void Scan_StructuralOnly_DoesNotRequireAnnotation()
    {
        const string source = """
            using Hyperbee.Migrations;
            namespace App;
            [Migration(1000)]
            public class CreateIndex : Migration
            {
                public override async Task UpAsync(CancellationToken ct)
                {
                    await _client.CreateIndexAsync("test", "users", "idx", "email", IndexType.STRING);
                }
            }
            """;

        var verdicts = AerospikeMigrationSourceScanner.ScanSource( source );

        verdicts.Should().HaveCount( 1 );
        verdicts[0].ClassName.Should().Be( "CreateIndex" );
        verdicts[0].ExtendsMigration.Should().BeTrue();
        verdicts[0].HasMigrationAttribute.Should().BeTrue();
        verdicts[0].LooksLikeDataOp.Should().BeFalse();
        verdicts[0].RequiresAnnotation.Should().BeFalse();
    }

    [TestMethod]
    public void Scan_DataOpWithoutAnnotation_RequiresAnnotation()
    {
        const string source = """
            using Hyperbee.Migrations;
            namespace App;
            [Migration(2000)]
            public class SeedUsers : Migration
            {
                public override async Task UpAsync(CancellationToken ct)
                {
                    await _client.Put(null, ct, new Key("test", "users", "u1"), new Bin("name", "alice"));
                }
            }
            """;

        var verdicts = AerospikeMigrationSourceScanner.ScanSource( source );

        verdicts[0].ClassName.Should().Be( "SeedUsers" );
        verdicts[0].LooksLikeDataOp.Should().BeTrue();
        verdicts[0].DataOpHits.Should().Contain( s => s.Contains( "Put" ) );
        verdicts[0].HasDataMigrationAttribute.Should().BeFalse();
        verdicts[0].RequiresAnnotation.Should().BeTrue();
    }

    [TestMethod]
    public void Scan_DataOpWithDataMigrationAttribute_DoesNotRequireAnnotation()
    {
        const string source = """
            using Hyperbee.Migrations;
            namespace App;
            [Migration(2000)]
            [DataMigration]
            public class SeedUsers : Migration
            {
                public override async Task UpAsync(CancellationToken ct)
                {
                    await _client.Put(null, ct, new Key("test", "users", "u1"), new Bin("name", "alice"));
                }
            }
            """;

        var verdicts = AerospikeMigrationSourceScanner.ScanSource( source );

        verdicts[0].LooksLikeDataOp.Should().BeTrue();
        verdicts[0].HasDataMigrationAttribute.Should().BeTrue();
        verdicts[0].RequiresAnnotation.Should().BeFalse();
    }

    [TestMethod]
    public void Scan_DataOpWithStructuralOnlyAttribute_DoesNotRequireAnnotation()
    {
        const string source = """
            using Hyperbee.Migrations;
            namespace App;
            [Migration(3000)]
            [StructuralOnly]
            public class CreateAndProbe : Migration
            {
                public override async Task UpAsync(CancellationToken ct)
                {
                    await _client.Put(null, ct, new Key("test", "users", "__probe__"), new Bin("init", 1));
                }
            }
            """;

        var verdicts = AerospikeMigrationSourceScanner.ScanSource( source );

        verdicts[0].HasStructuralOnlyAttribute.Should().BeTrue();
        verdicts[0].RequiresAnnotation.Should().BeFalse();
    }

    [TestMethod]
    public void Scan_DetectsDeleteAndTouchAndOperateCallSites()
    {
        const string source = """
            using Hyperbee.Migrations;
            namespace App;
            [Migration(1000)]
            public class WriteMix : Migration
            {
                public override async Task UpAsync(CancellationToken ct)
                {
                    await _client.Delete(null, ct, new Key("test", "users", "u1"));
                    await _client.Touch(null, ct, new Key("test", "users", "u2"));
                    await _client.Operate(null, ct, new Key("test", "users", "u3"), Operation.Put(new Bin("v", 1)));
                }
            }
            """;

        var verdicts = AerospikeMigrationSourceScanner.ScanSource( source );

        verdicts[0].LooksLikeDataOp.Should().BeTrue();
        verdicts[0].DataOpHits.Should().Contain( h => h.Contains( "Delete" ) );
        verdicts[0].DataOpHits.Should().Contain( h => h.Contains( "Touch" ) );
        verdicts[0].DataOpHits.Should().Contain( h => h.Contains( "Operate" ) );
        verdicts[0].RequiresAnnotation.Should().BeTrue();
    }

    [TestMethod]
    public void Scan_OperationPutInsideOperateArgs_DoesNotMatchAsWrite()
    {
        // Operation.Put inside _client.Operate(...) must NOT be classified
        // as a write call site -- the receiver `Operation` is not a client
        // identifier. Only `_client.Operate` itself triggers the heuristic.
        const string source = """
            using Hyperbee.Migrations;
            namespace App;
            [Migration(1000)]
            public class OperateOnly : Migration
            {
                public override async Task UpAsync(CancellationToken ct)
                {
                    await _client.Operate(null, ct, new Key("test", "users", "u1"),
                        Operation.Put(new Bin("v", 1)),
                        Operation.Get());
                }
            }
            """;

        var verdicts = AerospikeMigrationSourceScanner.ScanSource( source );

        verdicts[0].DataOpHits.Should().HaveCount( 1, "only _client.Operate should match; Operation.Put inside arguments must NOT" );
        verdicts[0].DataOpHits[0].Should().Contain( "Operate" );
        verdicts[0].DataOpHits[0].Should().NotMatch( "*_client.Put*" );
    }

    [TestMethod]
    public void Scan_ReadOnlyCalls_DoNotMatch()
    {
        const string source = """
            using Hyperbee.Migrations;
            namespace App;
            [Migration(1000)]
            public class ReadProbe : Migration
            {
                public override async Task UpAsync(CancellationToken ct)
                {
                    var r1 = await _client.Get(null, ct, new Key("test", "users", "u1"));
                    var r2 = await _client.Exists(null, ct, new Key("test", "users", "u2"));
                }
            }
            """;

        var verdicts = AerospikeMigrationSourceScanner.ScanSource( source );

        verdicts[0].LooksLikeDataOp.Should().BeFalse();
        verdicts[0].DataOpHits.Should().BeEmpty();
        verdicts[0].RequiresAnnotation.Should().BeFalse();
    }

    [TestMethod]
    public void Scan_NonDeterminism_DateTimeUtcNow_RequiresAnnotation()
    {
        const string source = """
            using Hyperbee.Migrations;
            namespace App;
            [Migration(1000)]
            public class StampedSeed : Migration
            {
                public override async Task UpAsync(CancellationToken ct)
                {
                    await _client.CreateIndexAsync("test", "users", $"idx_{DateTime.UtcNow.Ticks}", "name", IndexType.STRING);
                }
            }
            """;

        var verdicts = AerospikeMigrationSourceScanner.ScanSource( source );

        verdicts[0].NonDeterminismHits.Should().Contain( "DateTime.UtcNow" );
        verdicts[0].RequiresAnnotation.Should().BeTrue();
    }

    [TestMethod]
    public void Scan_NonDeterminism_GuidNewGuid_RequiresAnnotation()
    {
        const string source = """
            using Hyperbee.Migrations;
            namespace App;
            [Migration(1000)]
            public class IdSeed : Migration
            {
                public override async Task UpAsync(CancellationToken ct)
                {
                    var name = Guid.NewGuid().ToString();
                }
            }
            """;

        var verdicts = AerospikeMigrationSourceScanner.ScanSource( source );

        verdicts[0].NonDeterminismHits.Should().Contain( "Guid.NewGuid" );
        verdicts[0].RequiresAnnotation.Should().BeTrue();
    }

    [TestMethod]
    public void Scan_NonDeterminism_NewRandomWithoutSeed_RequiresAnnotation()
    {
        const string source = """
            using Hyperbee.Migrations;
            namespace App;
            [Migration(1000)]
            public class RandSeed : Migration
            {
                public override async Task UpAsync(CancellationToken ct)
                {
                    var r = new Random();
                }
            }
            """;

        var verdicts = AerospikeMigrationSourceScanner.ScanSource( source );

        verdicts[0].NonDeterminismHits.Should().Contain( "new Random()" );
    }

    [TestMethod]
    public void Scan_NonDeterminism_SeededRandom_DoesNotMatch()
    {
        const string source = """
            using Hyperbee.Migrations;
            namespace App;
            [Migration(1000)]
            public class SeededRandSeed : Migration
            {
                public override async Task UpAsync(CancellationToken ct)
                {
                    var r = new Random(42);
                }
            }
            """;

        var verdicts = AerospikeMigrationSourceScanner.ScanSource( source );

        verdicts[0].NonDeterminismHits.Should().BeEmpty();
    }

    [TestMethod]
    public void Scan_ClassNotExtendingMigration_NeverRequiresAnnotation()
    {
        // A helper class with the same call site shape but NOT extending Migration
        // should not be flagged -- the squash refusal applies only to migrations.
        const string source = """
            namespace App;
            public class WriterHelper
            {
                public async Task DoIt()
                {
                    await _client.Put(null, ct, new Key("test", "users", "u1"), new Bin("v", 1));
                }
            }
            """;

        var verdicts = AerospikeMigrationSourceScanner.ScanSource( source );

        verdicts[0].ExtendsMigration.Should().BeFalse();
        verdicts[0].RequiresAnnotation.Should().BeFalse();
    }

    [TestMethod]
    public void Scan_NoMigrationAttribute_NeverRequiresAnnotation()
    {
        // Extending Migration but lacking [Migration] -- usually an abstract
        // base or scaffolding -- never triggers the refusal.
        const string source = """
            using Hyperbee.Migrations;
            namespace App;
            public abstract class BaseSeed : Migration
            {
                public abstract Task UpAsync(CancellationToken ct);
            }
            """;

        var verdicts = AerospikeMigrationSourceScanner.ScanSource( source );

        verdicts[0].ExtendsMigration.Should().BeTrue();
        verdicts[0].HasMigrationAttribute.Should().BeFalse();
        verdicts[0].RequiresAnnotation.Should().BeFalse();
    }

    [TestMethod]
    public void Scan_AcceptsMigrationAttributeSuffixForm()
    {
        const string source = """
            using Hyperbee.Migrations;
            namespace App;
            [MigrationAttribute(1000)]
            public class WithSuffix : Migration { }
            """;

        var verdicts = AerospikeMigrationSourceScanner.ScanSource( source );
        verdicts[0].HasMigrationAttribute.Should().BeTrue();
    }

    [TestMethod]
    public void Scan_DirectoryNotFound_Throws()
    {
        Action act = () => AerospikeMigrationSourceScanner.Scan( @"c:\nonexistent\path\does\not\exist" );
        act.Should().Throw<DirectoryNotFoundException>();
    }

    [TestMethod]
    public void Scan_EmptySourceRoot_Throws()
    {
        Action act = () => AerospikeMigrationSourceScanner.Scan( "" );
        act.Should().Throw<ArgumentException>().WithParameterName( "sourceRoot" );
    }

    [TestMethod]
    public void ScanSource_NullSourceText_Throws()
    {
        Action act = () => AerospikeMigrationSourceScanner.ScanSource( null! );
        act.Should().Throw<ArgumentNullException>().WithParameterName( "sourceText" );
    }
}
