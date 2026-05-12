using FluentAssertions;
using Hyperbee.Migrations.Providers.MongoDB.Squash;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 3 Task 3.7: MongoDBMigrationSourceScanner (ADR-0019 A5).
//
// Receiver-anchoring trade-off: matches write-method names without
// receiver-name filtering (MongoDB code routes through local
// `collection`/`db` vars, not `_client.*`). False positives possible;
// operators annotate with [StructuralOnly] to suppress.

[TestClass]
public class MongoDBMigrationSourceScannerTests
{
    [TestMethod]
    public void Scan_StructuralOnly_DoesNotRequireAnnotation()
    {
        // `db.CreateCollectionAsync(...)` is structural; `Indexes.CreateOneAsync(...)`
        // is structural. Neither hits the write-method name catalog.
        const string source = """
            using Hyperbee.Migrations;
            namespace App;
            [Migration(1000)]
            public class CreateUsers : Migration
            {
                public override async Task UpAsync(CancellationToken ct)
                {
                    await db.CreateCollectionAsync("users");
                    await collection.Indexes.CreateOneAsync(indexModel);
                }
            }
            """;

        var verdicts = MongoDBMigrationSourceScanner.ScanSource( source );

        verdicts.Should().HaveCount( 1 );
        verdicts[0].ClassName.Should().Be( "CreateUsers" );
        verdicts[0].ExtendsMigration.Should().BeTrue();
        verdicts[0].HasMigrationAttribute.Should().BeTrue();
        verdicts[0].LooksLikeDataOp.Should().BeFalse();
        verdicts[0].RequiresAnnotation.Should().BeFalse();
    }

    [TestMethod]
    public void Scan_DataOpInsertOneAsync_RequiresAnnotation()
    {
        const string source = """
            using Hyperbee.Migrations;
            namespace App;
            [Migration(2000)]
            public class SeedUsers : Migration
            {
                public override async Task UpAsync(CancellationToken ct)
                {
                    await collection.InsertOneAsync(new User { Id = "u1" });
                }
            }
            """;

        var verdicts = MongoDBMigrationSourceScanner.ScanSource( source );

        verdicts[0].LooksLikeDataOp.Should().BeTrue();
        verdicts[0].DataOpHits.Should().Contain( "InsertOneAsync" );
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
                    await collection.InsertOneAsync(new User { Id = "u1" });
                }
            }
            """;

        var verdicts = MongoDBMigrationSourceScanner.ScanSource( source );

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
                    await collection.InsertOneAsync(new ProbeDoc { Id = "__probe__" });
                }
            }
            """;

        var verdicts = MongoDBMigrationSourceScanner.ScanSource( source );

        verdicts[0].HasStructuralOnlyAttribute.Should().BeTrue();
        verdicts[0].RequiresAnnotation.Should().BeFalse();
    }

    [TestMethod]
    public void Scan_DetectsBulkWriteAndUpdateManyAndDeleteMany()
    {
        const string source = """
            using Hyperbee.Migrations;
            namespace App;
            [Migration(1000)]
            public class WriteMix : Migration
            {
                public override async Task UpAsync(CancellationToken ct)
                {
                    await collection.BulkWriteAsync(operations);
                    await collection.UpdateManyAsync(filter, update);
                    await collection.DeleteManyAsync(filter);
                    await collection.ReplaceOneAsync(filter, doc);
                    await collection.FindOneAndUpdateAsync(filter, update);
                }
            }
            """;

        var verdicts = MongoDBMigrationSourceScanner.ScanSource( source );

        verdicts[0].LooksLikeDataOp.Should().BeTrue();
        verdicts[0].DataOpHits.Should().Contain( "BulkWriteAsync" );
        verdicts[0].DataOpHits.Should().Contain( "UpdateManyAsync" );
        verdicts[0].DataOpHits.Should().Contain( "DeleteManyAsync" );
        verdicts[0].DataOpHits.Should().Contain( "ReplaceOneAsync" );
        verdicts[0].DataOpHits.Should().Contain( "FindOneAndUpdateAsync" );
        verdicts[0].RequiresAnnotation.Should().BeTrue();
    }

    [TestMethod]
    public void Scan_DetectsGenericInsertOne()
    {
        // Generic method invocations (`collection.InsertOne<T>(doc)`) must
        // also match.
        const string source = """
            using Hyperbee.Migrations;
            namespace App;
            [Migration(1000)]
            public class TypedSeed : Migration
            {
                public override async Task UpAsync(CancellationToken ct)
                {
                    await collection.InsertOne<UserDoc>(doc);
                }
            }
            """;

        var verdicts = MongoDBMigrationSourceScanner.ScanSource( source );

        verdicts[0].DataOpHits.Should().Contain( "InsertOne" );
        verdicts[0].RequiresAnnotation.Should().BeTrue();
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
                    var cursor = await collection.FindAsync(filter);
                    var count = await collection.CountDocumentsAsync(filter);
                    var values = await collection.DistinctAsync<string>(field, filter);
                }
            }
            """;

        var verdicts = MongoDBMigrationSourceScanner.ScanSource( source );

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
            public class StampedCollection : Migration
            {
                public override async Task UpAsync(CancellationToken ct)
                {
                    await db.CreateCollectionAsync($"users_{DateTime.UtcNow:yyyyMMdd}");
                }
            }
            """;

        var verdicts = MongoDBMigrationSourceScanner.ScanSource( source );

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
                    var id = Guid.NewGuid().ToString();
                }
            }
            """;

        var verdicts = MongoDBMigrationSourceScanner.ScanSource( source );

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

        var verdicts = MongoDBMigrationSourceScanner.ScanSource( source );

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

        var verdicts = MongoDBMigrationSourceScanner.ScanSource( source );
        verdicts[0].NonDeterminismHits.Should().BeEmpty();
    }

    [TestMethod]
    public void Scan_ClassNotExtendingMigration_NeverRequiresAnnotation()
    {
        const string source = """
            namespace App;
            public class WriterHelper
            {
                public async Task DoIt()
                {
                    await collection.InsertOneAsync(new User());
                }
            }
            """;

        var verdicts = MongoDBMigrationSourceScanner.ScanSource( source );

        verdicts[0].ExtendsMigration.Should().BeFalse();
        verdicts[0].RequiresAnnotation.Should().BeFalse();
    }

    [TestMethod]
    public void Scan_NoMigrationAttribute_NeverRequiresAnnotation()
    {
        const string source = """
            using Hyperbee.Migrations;
            namespace App;
            public abstract class BaseSeed : Migration
            {
                public abstract Task UpAsync(CancellationToken ct);
            }
            """;

        var verdicts = MongoDBMigrationSourceScanner.ScanSource( source );

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

        var verdicts = MongoDBMigrationSourceScanner.ScanSource( source );
        verdicts[0].HasMigrationAttribute.Should().BeTrue();
    }

    [TestMethod]
    public void Scan_DirectoryNotFound_Throws()
    {
        Action act = () => MongoDBMigrationSourceScanner.Scan( @"c:\nonexistent\path\does\not\exist" );
        act.Should().Throw<DirectoryNotFoundException>();
    }

    [TestMethod]
    public void Scan_EmptySourceRoot_Throws()
    {
        Action act = () => MongoDBMigrationSourceScanner.Scan( "" );
        act.Should().Throw<ArgumentException>().WithParameterName( "sourceRoot" );
    }

    [TestMethod]
    public void ScanSource_NullSourceText_Throws()
    {
        Action act = () => MongoDBMigrationSourceScanner.ScanSource( null );
        act.Should().Throw<ArgumentNullException>().WithParameterName( "sourceText" );
    }
}
