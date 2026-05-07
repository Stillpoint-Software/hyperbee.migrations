using FluentAssertions;
using Hyperbee.Migrations.Providers.Postgres.Squash;
using Hyperbee.Migrations.Squash;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Npgsql;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 6 — synthetic end-to-end exercise of the Postgres squash pipeline.
//
// Real Testcontainers + pg_dump verification lives in the Phase 7 integration
// suite. These tests inject a fake snapshot capture so the strategy /
// canonicalizer / classifier wire path runs cleanly without Docker.
//
// Topology capture also requires a live Postgres connection; we exercise it
// only in the integration suite. For these unit-level tests we hand-build a
// PostgresTopologySignature.

[TestClass]
public class PostgresSquashEndToEndTests
{
    [TestMethod]
    public async Task DataOpClassifier_FlagsNonDeterminism()
    {
        var classifier = new PostgresDataOpClassifier();

        var c1 = classifier.Classify( "INSERT INTO app.audit (created_at) VALUES (now())" );
        c1.IsDataOp.Should().BeTrue();
        c1.RequiresPreservation.Should().BeTrue();
        c1.EmissionHint.Should().Contain( "now" );
        c1.RequiresAnnotation.Should().BeTrue();

        var c2 = classifier.Classify( "CREATE TABLE app.t (id uuid DEFAULT gen_random_uuid())" );
        c2.IsDataOp.Should().BeFalse();
        c2.EmissionHint.Should().Contain( "gen_random_uuid" );

        await Task.CompletedTask;
    }

    [TestMethod]
    public async Task DataOpClassifier_DoBlock_RequiresPreservation()
    {
        var c = new PostgresDataOpClassifier()
            .Classify( "DO $$ BEGIN INSERT INTO app.t VALUES (1); END $$;" );

        c.IsDataOp.Should().BeTrue();
        c.RequiresPreservation.Should().BeTrue();
        c.IsUnclassified.Should().BeFalse();
    }

    [TestMethod]
    public async Task DataOpClassifier_UnknownVerb_DefaultDeny()
    {
        var c = new PostgresDataOpClassifier().Classify( "VACUUM ANALYZE app.t" );

        c.IsUnclassified.Should().BeTrue( "VACUUM is not in the recognized verb set" );
        c.RequiresAnnotation.Should().BeTrue();
        await Task.CompletedTask;
    }

    [TestMethod]
    public void TopologySignature_IsCompatibleWith_Self_ReturnsTrue()
    {
        var sig = new PostgresTopologySignature
        {
            ServerMajor = 16,
            ServerMinor = 13,
            Extensions = new[] { "pgcrypto", "uuid-ossp" },
            CollationProvider = "C",
            LocaleProvider = "libc",
            ServerEncoding = "UTF8"
        };

        sig.IsCompatibleWith( sig, out var reason ).Should().BeTrue();
        reason.Should().BeNullOrEmpty();
    }

    [TestMethod]
    public void TopologySignature_DifferentMajor_Incompatible()
    {
        var sig16 = new PostgresTopologySignature { ServerMajor = 16, ServerMinor = 13, ServerEncoding = "UTF8", CollationProvider = "C", LocaleProvider = "libc" };
        var sig17 = sig16 with { ServerMajor = 17 };

        sig16.IsCompatibleWith( sig17, out var reason ).Should().BeFalse();
        reason.Should().Contain( "server_major" );
    }

    [TestMethod]
    public void TopologySignature_ExtensionAdded_Incompatible()
    {
        var a = new PostgresTopologySignature
        {
            ServerMajor = 16,
            Extensions = new[] { "pgcrypto" },
            CollationProvider = "C",
            LocaleProvider = "libc",
            ServerEncoding = "UTF8"
        };
        var b = a with { Extensions = new[] { "pgcrypto", "uuid-ossp" } };

        a.IsCompatibleWith( b, out var reason ).Should().BeFalse();
        reason.Should().Contain( "extension set" );
        reason.Should().Contain( "uuid-ossp" );
    }

    [TestMethod]
    public void Canonicalizer_StripsPgDumpPreambleAndDirectives()
    {
        var raw = """
            --
            -- PostgreSQL database dump
            --

            \restrict 4Pt21ZeOK

            -- Dumped from database version 16.13
            -- Dumped by pg_dump version 16.13

            SET statement_timeout = 0;
            SET lock_timeout = 0;
            SELECT pg_catalog.set_config('search_path', '', false);

            --
            -- Name: app; Type: SCHEMA; Schema: -; Owner: -
            --

            CREATE SCHEMA app;

            \unrestrict 4Pt21ZeOK
            """;

        var canon = new PostgresSnapshotCanonicalizer().Canonicalize( raw );

        canon.Should().NotContain( "PostgreSQL database dump" );
        canon.Should().NotContain( "\\restrict" );
        canon.Should().NotContain( "SET statement_timeout" );
        canon.Should().NotContain( "set_config" );
        canon.Should().Contain( "CREATE SCHEMA app" );
    }

    [TestMethod]
    public void Canonicalizer_Refuses_CreateIndexConcurrently()
    {
        var raw = "CREATE INDEX CONCURRENTLY idx_x ON app.t (col);";
        var canon = new PostgresSnapshotCanonicalizer();

        var act = () => canon.Canonicalize( raw );
        act.Should().Throw<MigrationException>().WithMessage( "*CONCURRENTLY*ADR-0019*A2*" );
    }

    [TestMethod]
    public async Task Strategy_HappyPath_EmitsCanonicalSquash()
    {
        var topology = new PostgresTopologySignature
        {
            ServerMajor = 16,
            ServerMinor = 13,
            Extensions = new[] { "pgcrypto" },
            CollationProvider = "C",
            LocaleProvider = "libc",
            ServerEncoding = "UTF8"
        };

        // Synthetic capture: returns canned dump bytes regardless of input.
        var dumpText = """
            --
            -- PostgreSQL database dump
            --

            \restrict tok

            SET statement_timeout = 0;

            CREATE SCHEMA app;
            CREATE TABLE app.t (id int PRIMARY KEY, name text);
            CREATE INDEX idx_t_name ON app.t (name);
            """;

        var capture = new Func<SnapshotCaptureRequest, CancellationToken, Task<SnapshotCaptureResult>>(
            ( req, ct ) => Task.FromResult( new SnapshotCaptureResult(
                DumpText: dumpText,
                SequenceLastValues: new Dictionary<string, long>
                {
                    ["app.s_id_seq"] = 1042
                } ) ) );

        // The strategy needs a NpgsqlDataSource for topology capture; override
        // by using a derived context that pre-loads the topology. For this
        // unit test we build an NpgsqlDataSource pointed at a non-existent
        // host but never call CaptureAsync — so we use a derived strategy
        // that bypasses topology capture.
        var dataSource = NpgsqlDataSource.Create( "Host=localhost;Port=1;Database=none;Username=none;Password=none" );

        var ctx = new PostgresSquashGenerationContext(
            squashName: "Squash_5000",
            squashVersion: 5000L,
            dataSource: dataSource,
            captureSnapshotAsync: capture );

        var descriptors = new[]
        {
            DescriptorFor<MigrationA1>(),
            DescriptorFor<MigrationA2>(),
            DescriptorFor<MigrationA3>()
        };

        var strategy = new PgDumpSnapshotStrategyTestable(
            new PostgresSnapshotCanonicalizer(),
            new PostgresDataOpClassifier(),
            preCapturedTopology: topology );

        var result = await strategy.GenerateAsync( ctx, descriptors, new SquashGenerationOptions() );

        var generated = result.Should().BeOfType<SquashGenerationResult.Generated>().Subject;
        generated.Kind.Should().Be( ContentKind.SqlText );
        generated.Encoding.Should().Be( ContentEncoding.Utf8 );
        generated.Replaces.Should().BeEquivalentTo( new[] { 1L, 2L, 3L } );

        // Canonicalized + setval block emitted
        generated.Content.Should().Contain( "CREATE SCHEMA app" );
        generated.Content.Should().Contain( "CREATE TABLE app.t" );
        generated.Content.Should().NotContain( "\\restrict" );
        generated.Content.Should().Contain( "setval('app.s_id_seq', 1042, true)" );
        generated.Content.Should().Contain( "Sequence post-emission" );
    }

    [TestMethod]
    public async Task Verifier_RoundTrip_BytePequal_Succeeds()
    {
        var topology = new PostgresTopologySignature
        {
            ServerMajor = 16,
            Extensions = new[] { "pgcrypto" },
            CollationProvider = "C",
            LocaleProvider = "libc",
            ServerEncoding = "UTF8"
        };

        var dumpText = "CREATE SCHEMA app;\nCREATE TABLE app.t (id int);\n";

        var generated = new SquashGenerationResult.Generated(
            Content: dumpText,
            Kind: ContentKind.SqlText,
            Encoding: ContentEncoding.Utf8,
            Replaces: new[] { 1L, 2L, 3L },
            Diagnostics: Array.Empty<string>(),
            Topology: topology );

        var ctx = new PostgresSquashGenerationContext(
            squashName: "Squash_5000",
            squashVersion: 5000L,
            dataSource: NpgsqlDataSource.Create( "Host=localhost;Port=1;Database=none;Username=none;Password=none" ),
            captureSnapshotAsync: ( _, _ ) => Task.FromResult( new SnapshotCaptureResult(
                DumpText: dumpText,
                SequenceLastValues: new Dictionary<string, long>() ) ) );

        var verifier = new PostgresSquashVerifier( new PostgresSnapshotCanonicalizer() )
        {
            CaptureFromGeneratedAsync = ( content, topo, ct ) => Task.FromResult( new SnapshotCaptureResult(
                DumpText: dumpText,
                SequenceLastValues: new Dictionary<string, long>() ) )
        };

        var result = await verifier.VerifyAsync( ctx, generated );

        result.Should().BeOfType<VerificationResult.Success>();
    }

    [TestMethod]
    public async Task Verifier_RoundTrip_Differs_ProducesFailedWithDiff()
    {
        var topology = new PostgresTopologySignature
        {
            ServerMajor = 16,
            Extensions = new[] { "pgcrypto" },
            CollationProvider = "C",
            LocaleProvider = "libc",
            ServerEncoding = "UTF8"
        };

        var historicalDump = "CREATE SCHEMA app;\nCREATE TABLE app.t (id int);\n";
        var generatedDump = "CREATE SCHEMA app;\nCREATE TABLE app.t (id int, extra text);\n";

        var generated = new SquashGenerationResult.Generated(
            Content: generatedDump,
            Kind: ContentKind.SqlText,
            Encoding: ContentEncoding.Utf8,
            Replaces: new[] { 1L, 2L, 3L },
            Diagnostics: Array.Empty<string>(),
            Topology: topology );

        var ctx = new PostgresSquashGenerationContext(
            squashName: "Squash_5000",
            squashVersion: 5000L,
            dataSource: NpgsqlDataSource.Create( "Host=localhost;Port=1;Database=none;Username=none;Password=none" ),
            captureSnapshotAsync: ( _, _ ) => Task.FromResult( new SnapshotCaptureResult(
                DumpText: historicalDump,
                SequenceLastValues: new Dictionary<string, long>() ) ) );

        var verifier = new PostgresSquashVerifier( new PostgresSnapshotCanonicalizer() )
        {
            CaptureFromGeneratedAsync = ( content, topo, ct ) => Task.FromResult( new SnapshotCaptureResult(
                DumpText: generatedDump,
                SequenceLastValues: new Dictionary<string, long>() ) )
        };

        var result = await verifier.VerifyAsync( ctx, generated );

        var failed = result.Should().BeOfType<VerificationResult.Failed>().Subject;
        failed.Detail.Should().Contain( "byte-equal" );
        failed.DiffSummary.Should().Contain( "extra text" );
    }

    private static MigrationDescriptor DescriptorFor<T>() where T : Migration =>
        new( typeof( T ), typeof( T ).GetCustomAttributes( typeof( MigrationAttribute ), false ).Cast<MigrationAttribute>().Single(), Array.Empty<long>() );

    private sealed class PgDumpSnapshotStrategyTestable : ISquashStrategy
    {
        private readonly PostgresSnapshotCanonicalizer _canonicalizer;
        private readonly PostgresDataOpClassifier _dataOpClassifier;
        private readonly PostgresTopologySignature _topology;

        public string ProviderId => PostgresTopologySignature.ProviderIdValue;

        public PgDumpSnapshotStrategyTestable(
            PostgresSnapshotCanonicalizer canonicalizer,
            PostgresDataOpClassifier dataOpClassifier,
            PostgresTopologySignature preCapturedTopology )
        {
            _canonicalizer = canonicalizer;
            _dataOpClassifier = dataOpClassifier;
            _topology = preCapturedTopology;
        }

        public async Task<SquashGenerationResult> GenerateAsync(
            ISquashGenerationContext context,
            IReadOnlyList<MigrationDescriptor> descriptors,
            SquashGenerationOptions options,
            CancellationToken cancellationToken = default )
        {
            // Mirrors PgDumpSnapshotStrategy but uses a pre-captured topology so we
            // don't need a live Postgres connection in unit tests.
            var pgContext = (PostgresSquashGenerationContext) context;

            var lowerBound = options?.LowerBound ?? descriptors.Min( d => d.Attribute.Version );
            var upperBound = options?.UpperBound ?? descriptors.Max( d => d.Attribute.Version );

            var replaces = descriptors
                .Where( d => d.Attribute.Version >= lowerBound && d.Attribute.Version <= upperBound )
                .Select( d => d.Attribute.Version )
                .OrderBy( v => v )
                .ToArray();

            var capture = await pgContext.CaptureSnapshotAsync(
                new SnapshotCaptureRequest( "snapshot-B", upperBound, _topology ),
                cancellationToken );

            var canon = _canonicalizer.Canonicalize( capture.DumpText );

            var content = canon;
            if ( capture.SequenceLastValues is { Count: > 0 } seqs )
            {
                var setvalBlock = string.Join(
                    Environment.NewLine,
                    seqs.OrderBy( kv => kv.Key, StringComparer.Ordinal )
                        .Select( kv => $"SELECT setval('{kv.Key}', {kv.Value}, true);" ) );
                content = content.TrimEnd( '\n' ) + "\n\n-- Sequence post-emission\n" + setvalBlock + "\n";
            }

            return new SquashGenerationResult.Generated(
                Content: _canonicalizer.EmitScript( content ),
                Kind: ContentKind.SqlText,
                Encoding: ContentEncoding.Utf8,
                Replaces: replaces,
                Diagnostics: Array.Empty<string>(),
                Topology: _topology );
        }
    }
}

[Migration( 1L )]
public class MigrationA1 : Migration
{
    public override Task UpAsync( CancellationToken ct = default ) => Task.CompletedTask;
}

[Migration( 2L )]
public class MigrationA2 : Migration
{
    public override Task UpAsync( CancellationToken ct = default ) => Task.CompletedTask;
}

[Migration( 3L )]
public class MigrationA3 : Migration
{
    public override Task UpAsync( CancellationToken ct = default ) => Task.CompletedTask;
}
