using FluentAssertions;
using Hyperbee.Migrations.Providers.Couchbase.Squash;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 4 Task 4.7: CouchbaseMigrationSourceScanner (ADR-0019 A5).
//
// Receiver-anchoring trade-off: matches Couchbase KV write-method names
// without receiver-name filtering (Couchbase code routes through local
// `collection`/`bucket`/`scope`/`cluster` vars). False positives possible;
// operators annotate with [StructuralOnly] to suppress.
//
// R-P3 OQ acknowledgement: `QueryAsync` is NOT in the data-op method
// catalog -- source-only inspection can't resolve the SQL value. Operator
// annotation is the contract; scanner agrees with the data-op classifier.

[TestClass]
public class CouchbaseMigrationSourceScannerTests
{
    [TestMethod]
    public void Scan_StructuralOnly_DoesNotRequireAnnotation()
    {
        const string source = """
            using Hyperbee.Migrations;
            namespace App;
            [Migration(1000)]
            public class CreateBuckets : Migration
            {
                public override async Task UpAsync(CancellationToken ct)
                {
                    await cluster.Buckets.CreateBucketAsync(settings);
                    await bucket.Collections.CreateCollectionAsync(spec);
                    await cluster.QueryIndexes.CreateIndexAsync("myapp", "idx", new[]{"email"});
                }
            }
            """;

        var verdicts = CouchbaseMigrationSourceScanner.ScanSource( source );

        verdicts.Should().HaveCount( 1 );
        verdicts[0].ClassName.Should().Be( "CreateBuckets" );
        verdicts[0].ExtendsMigration.Should().BeTrue();
        verdicts[0].HasMigrationAttribute.Should().BeTrue();
        verdicts[0].LooksLikeDataOp.Should().BeFalse();
        verdicts[0].RequiresAnnotation.Should().BeFalse();
    }

    [TestMethod]
    public void Scan_DataOpUpsertAsync_RequiresAnnotation()
    {
        const string source = """
            using Hyperbee.Migrations;
            namespace App;
            [Migration(2000)]
            public class SeedUsers : Migration
            {
                public override async Task UpAsync(CancellationToken ct)
                {
                    await collection.UpsertAsync("u1", new { name = "alpha" });
                }
            }
            """;

        var verdicts = CouchbaseMigrationSourceScanner.ScanSource( source );

        verdicts[0].LooksLikeDataOp.Should().BeTrue();
        verdicts[0].DataOpHits.Should().Contain( "UpsertAsync" );
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
                    await collection.UpsertAsync("u1", new { name = "alpha" });
                }
            }
            """;

        var verdicts = CouchbaseMigrationSourceScanner.ScanSource( source );

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
                    await collection.UpsertAsync("__probe__", new { x = 1 });
                }
            }
            """;

        var verdicts = CouchbaseMigrationSourceScanner.ScanSource( source );

        verdicts[0].HasStructuralOnlyAttribute.Should().BeTrue();
        verdicts[0].RequiresAnnotation.Should().BeFalse();
    }

    [TestMethod]
    public void Scan_DetectsKvWriteFamily()
    {
        const string source = """
            using Hyperbee.Migrations;
            namespace App;
            [Migration(1000)]
            public class WriteMix : Migration
            {
                public override async Task UpAsync(CancellationToken ct)
                {
                    await collection.InsertAsync("k1", doc);
                    await collection.ReplaceAsync("k1", doc);
                    await collection.RemoveAsync("k1");
                    await collection.MutateInAsync("k1", specs);
                    await collection.Binary.AppendAsync("k1", bytes);
                    await collection.Binary.IncrementAsync("counter");
                }
            }
            """;

        var verdicts = CouchbaseMigrationSourceScanner.ScanSource( source );

        verdicts[0].LooksLikeDataOp.Should().BeTrue();
        verdicts[0].DataOpHits.Should().Contain( "InsertAsync" );
        verdicts[0].DataOpHits.Should().Contain( "ReplaceAsync" );
        verdicts[0].DataOpHits.Should().Contain( "RemoveAsync" );
        verdicts[0].DataOpHits.Should().Contain( "MutateInAsync" );
        verdicts[0].DataOpHits.Should().Contain( "AppendAsync" );
        verdicts[0].DataOpHits.Should().Contain( "IncrementAsync" );
        verdicts[0].RequiresAnnotation.Should().BeTrue();
    }

    [TestMethod]
    public void Scan_DetectsGenericUpsert()
    {
        // Generic method invocations (`collection.UpsertAsync<T>(id, doc)`)
        // must also match -- Couchbase SDK methods are typically generic.
        const string source = """
            using Hyperbee.Migrations;
            namespace App;
            [Migration(1000)]
            public class TypedSeed : Migration
            {
                public override async Task UpAsync(CancellationToken ct)
                {
                    await collection.UpsertAsync<UserDoc>("u1", doc);
                }
            }
            """;

        var verdicts = CouchbaseMigrationSourceScanner.ScanSource( source );

        verdicts[0].DataOpHits.Should().Contain( "UpsertAsync" );
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
                    var r = await collection.GetAsync("k1");
                    var e = await collection.ExistsAsync("k1");
                    var l = await collection.LookupInAsync("k1", specs);
                }
            }
            """;

        var verdicts = CouchbaseMigrationSourceScanner.ScanSource( source );

        verdicts[0].LooksLikeDataOp.Should().BeFalse();
        verdicts[0].DataOpHits.Should().BeEmpty();
        verdicts[0].RequiresAnnotation.Should().BeFalse();
    }

    [TestMethod]
    public void Scan_QueryAsync_NotFlaggedAsDataOp_R_P3_OQ()
    {
        // R-P3 OQ resolution: scanner does NOT flag QueryAsync as a data op.
        // The data-op classifier surfaces default-deny at classification
        // time. Operator annotation is the contract for parameterized N1QL.
        const string source = """
            using Hyperbee.Migrations;
            namespace App;
            [Migration(1000)]
            public class N1qlOnly : Migration
            {
                public override async Task UpAsync(CancellationToken ct)
                {
                    await cluster.QueryAsync<MyDoc>("INSERT INTO myapp (KEY, VALUE) VALUES ('k', {})");
                }
            }
            """;

        var verdicts = CouchbaseMigrationSourceScanner.ScanSource( source );

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
            public class StampedScope : Migration
            {
                public override async Task UpAsync(CancellationToken ct)
                {
                    await bucket.Collections.CreateScopeAsync($"tenant_{DateTime.UtcNow:yyyyMMdd}");
                }
            }
            """;

        var verdicts = CouchbaseMigrationSourceScanner.ScanSource( source );

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

        var verdicts = CouchbaseMigrationSourceScanner.ScanSource( source );

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

        var verdicts = CouchbaseMigrationSourceScanner.ScanSource( source );

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

        var verdicts = CouchbaseMigrationSourceScanner.ScanSource( source );
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
                    await collection.UpsertAsync("k1", doc);
                }
            }
            """;

        var verdicts = CouchbaseMigrationSourceScanner.ScanSource( source );

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

        var verdicts = CouchbaseMigrationSourceScanner.ScanSource( source );

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

        var verdicts = CouchbaseMigrationSourceScanner.ScanSource( source );
        verdicts[0].HasMigrationAttribute.Should().BeTrue();
    }

    [TestMethod]
    public void Scan_DirectoryNotFound_Throws()
    {
        Action act = () => CouchbaseMigrationSourceScanner.Scan( @"c:\nonexistent\path\does\not\exist" );
        act.Should().Throw<DirectoryNotFoundException>();
    }

    [TestMethod]
    public void Scan_EmptySourceRoot_Throws()
    {
        Action act = () => CouchbaseMigrationSourceScanner.Scan( "" );
        act.Should().Throw<ArgumentException>().WithParameterName( "sourceRoot" );
    }

    [TestMethod]
    public void ScanSource_NullSourceText_Throws()
    {
        Action act = () => CouchbaseMigrationSourceScanner.ScanSource( null );
        act.Should().Throw<ArgumentNullException>().WithParameterName( "sourceText" );
    }
}
