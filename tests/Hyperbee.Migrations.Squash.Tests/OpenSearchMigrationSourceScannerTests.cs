using FluentAssertions;
using Hyperbee.Migrations.Providers.OpenSearch.Squash;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 2 Task 2.7: OpenSearchMigrationSourceScanner (ADR-0019 A5).
//
// The scanner walks user migration source files and refuses squash generation
// when a Migration-derived class contains data-op call sites without an
// explicit [DataMigration] or [StructuralOnly] annotation. OpenSearch's
// version of the scanner detects IOpenSearchClient write methods on the
// `_?client` receiver -- the same shape as the Aerospike scanner with
// provider-specific verb sets.

[TestClass]
public class OpenSearchMigrationSourceScannerTests
{
    [TestMethod]
    public void Scan_StructuralOnly_DoesNotRequireAnnotation()
    {
        // Sub-client paths (`_client.Indices.CreateAsync(...)`) are NOT data
        // ops -- the receiver-name filter excludes them automatically because
        // the receiver here is `_client.Indices`, not `_client`.
        const string source = """
            using Hyperbee.Migrations;
            namespace App;
            [Migration(1000)]
            public class CreateIndex : Migration
            {
                public override async Task UpAsync(CancellationToken ct)
                {
                    await _client.Indices.CreateAsync("users", i => i.Map(m => m.AutoMap()));
                }
            }
            """;

        var verdicts = OpenSearchMigrationSourceScanner.ScanSource( source );

        verdicts.Should().HaveCount( 1 );
        verdicts[0].ClassName.Should().Be( "CreateIndex" );
        verdicts[0].ExtendsMigration.Should().BeTrue();
        verdicts[0].HasMigrationAttribute.Should().BeTrue();
        verdicts[0].LooksLikeDataOp.Should().BeFalse();
        verdicts[0].RequiresAnnotation.Should().BeFalse();
    }

    [TestMethod]
    public void Scan_DataOpIndexAsync_RequiresAnnotation()
    {
        const string source = """
            using Hyperbee.Migrations;
            namespace App;
            [Migration(2000)]
            public class SeedUsers : Migration
            {
                public override async Task UpAsync(CancellationToken ct)
                {
                    await _client.IndexAsync(new User { Id = "u1" });
                }
            }
            """;

        var verdicts = OpenSearchMigrationSourceScanner.ScanSource( source );

        verdicts[0].LooksLikeDataOp.Should().BeTrue();
        verdicts[0].DataOpHits.Should().Contain( h => h.Contains( "IndexAsync" ) );
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
                    await _client.IndexAsync(new User { Id = "u1" });
                }
            }
            """;

        var verdicts = OpenSearchMigrationSourceScanner.ScanSource( source );

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
                    await _client.IndexAsync(new ProbeDoc { Id = "__probe__" });
                }
            }
            """;

        var verdicts = OpenSearchMigrationSourceScanner.ScanSource( source );

        verdicts[0].HasStructuralOnlyAttribute.Should().BeTrue();
        verdicts[0].RequiresAnnotation.Should().BeFalse();
    }

    [TestMethod]
    public void Scan_DetectsBulkAndUpdateByQueryAndReindex()
    {
        const string source = """
            using Hyperbee.Migrations;
            namespace App;
            [Migration(1000)]
            public class WriteMix : Migration
            {
                public override async Task UpAsync(CancellationToken ct)
                {
                    await _client.BulkAsync(b => b.IndexMany(docs));
                    await _client.UpdateByQueryAsync<User>(u => u.Query(q => q.MatchAll()));
                    await _client.ReindexAsync<User>(r => r.From("users_v1").To("users_v2"));
                }
            }
            """;

        var verdicts = OpenSearchMigrationSourceScanner.ScanSource( source );

        verdicts[0].LooksLikeDataOp.Should().BeTrue();
        verdicts[0].DataOpHits.Should().Contain( h => h.Contains( "BulkAsync" ) );
        verdicts[0].DataOpHits.Should().Contain( h => h.Contains( "UpdateByQueryAsync" ) );
        verdicts[0].DataOpHits.Should().Contain( h => h.Contains( "ReindexAsync" ) );
        verdicts[0].RequiresAnnotation.Should().BeTrue();
    }

    [TestMethod]
    public void Scan_DetectsDeleteAndDeleteByQuery()
    {
        const string source = """
            using Hyperbee.Migrations;
            namespace App;
            [Migration(1000)]
            public class CleanupMigration : Migration
            {
                public override async Task UpAsync(CancellationToken ct)
                {
                    await _client.DeleteAsync<User>("u1");
                    await _client.DeleteByQueryAsync<User>(d => d.Query(q => q.Term("status", "archived")));
                }
            }
            """;

        var verdicts = OpenSearchMigrationSourceScanner.ScanSource( source );

        verdicts[0].DataOpHits.Should().Contain( h => h.Contains( "DeleteAsync" ) );
        verdicts[0].DataOpHits.Should().Contain( h => h.Contains( "DeleteByQueryAsync" ) );
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
                    var doc = await _client.GetAsync<User>("u1");
                    var n = await _client.CountAsync<User>(c => c.Query(q => q.MatchAll()));
                    var exists = await _client.IndexExistsAsync("users");
                }
            }
            """;

        var verdicts = OpenSearchMigrationSourceScanner.ScanSource( source );

        verdicts[0].LooksLikeDataOp.Should().BeFalse();
        verdicts[0].DataOpHits.Should().BeEmpty();
        verdicts[0].RequiresAnnotation.Should().BeFalse();
    }

    [TestMethod]
    public void Scan_SubClientCalls_DoNotMatch()
    {
        // Sub-client paths must NOT be classified as data ops -- the receiver
        // is `_client.Indices` / `_client.Cluster` / `_client.Ingest`, not
        // `_client` directly. This is the load-bearing test for the
        // receiver-anchoring rule.
        const string source = """
            using Hyperbee.Migrations;
            namespace App;
            [Migration(1000)]
            public class StructuralMigration : Migration
            {
                public override async Task UpAsync(CancellationToken ct)
                {
                    await _client.Indices.CreateAsync("users", c => c.Map(m => m.AutoMap()));
                    await _client.Cluster.PutComponentTemplateAsync("ct", t => t);
                    await _client.Ingest.PutPipelineAsync("p", p => p);
                }
            }
            """;

        var verdicts = OpenSearchMigrationSourceScanner.ScanSource( source );

        verdicts[0].LooksLikeDataOp.Should().BeFalse( "sub-client paths (Indices/Cluster/Ingest) are structural, not data ops" );
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
            public class StampedTemplate : Migration
            {
                public override async Task UpAsync(CancellationToken ct)
                {
                    await _client.Indices.CreateAsync($"users_{DateTime.UtcNow:yyyyMMdd}", c => c.Map(m => m.AutoMap()));
                }
            }
            """;

        var verdicts = OpenSearchMigrationSourceScanner.ScanSource( source );

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

        var verdicts = OpenSearchMigrationSourceScanner.ScanSource( source );

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

        var verdicts = OpenSearchMigrationSourceScanner.ScanSource( source );

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

        var verdicts = OpenSearchMigrationSourceScanner.ScanSource( source );
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
                    await _client.IndexAsync(new User());
                }
            }
            """;

        var verdicts = OpenSearchMigrationSourceScanner.ScanSource( source );

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

        var verdicts = OpenSearchMigrationSourceScanner.ScanSource( source );

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

        var verdicts = OpenSearchMigrationSourceScanner.ScanSource( source );
        verdicts[0].HasMigrationAttribute.Should().BeTrue();
    }

    [TestMethod]
    public void Scan_DirectoryNotFound_Throws()
    {
        Action act = () => OpenSearchMigrationSourceScanner.Scan( @"c:\nonexistent\path\does\not\exist" );
        act.Should().Throw<DirectoryNotFoundException>();
    }

    [TestMethod]
    public void Scan_EmptySourceRoot_Throws()
    {
        Action act = () => OpenSearchMigrationSourceScanner.Scan( "" );
        act.Should().Throw<ArgumentException>().WithParameterName( "sourceRoot" );
    }

    [TestMethod]
    public void ScanSource_NullSourceText_Throws()
    {
        Action act = () => OpenSearchMigrationSourceScanner.ScanSource( null! );
        act.Should().Throw<ArgumentNullException>().WithParameterName( "sourceText" );
    }
}
