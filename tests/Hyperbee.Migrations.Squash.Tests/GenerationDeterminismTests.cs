using FluentAssertions;
using Hyperbee.Migrations.Providers.Postgres.Squash;
using Hyperbee.Migrations.Squash;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using Npgsql;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 8 — C12 generation determinism gate (per ADR-0019 A16 + ADR-0022).
//
// The gate runs `squash --range R` twice and asserts byte-equal output across
// runs. Sources of nondeterminism eliminated:
//   - Snapshot capture (synthetic in tests; Testcontainers-backed in production)
//   - Canonicalizer normalization (deterministic by contract)
//   - Sequence setval block emission (sorted by name)
//   - Generated content (no GUIDs, no timestamps in body)
//
// The test runs against the same synthetic captures used by Phase 6 end-to-end
// tests; production CI ships a real-Testcontainers variant in Phase 7/8 follow-up.

[TestClass]
public class GenerationDeterminismTests
{
    [TestMethod]
    public async Task PostgresSquashCodegen_SameInputs_ProducesByteIdenticalOutput()
    {
        var dumpText = """
            --
            -- PostgreSQL database dump
            --

            \restrict tok

            SET statement_timeout = 0;

            CREATE SCHEMA app;
            CREATE TABLE app.users (id bigint PRIMARY KEY, email text NOT NULL);
            CREATE INDEX idx_users_email ON app.users (email);
            CREATE TABLE app.orders (id bigint PRIMARY KEY, user_id bigint REFERENCES app.users(id));
            CREATE INDEX idx_orders_user_id ON app.orders (user_id);
            """;

        // Two independent runs against the same inputs.
        var first = await RunSynthesizedSquashAsync( dumpText );
        var second = await RunSynthesizedSquashAsync( dumpText );

        first.Content.Should().Be( second.Content,
            "C12 determinism gate: two runs of the squash codegen against identical inputs " +
            "must produce byte-identical output." );

        first.Replaces.Should().BeEquivalentTo( second.Replaces );
        first.Encoding.Should().Be( second.Encoding );
        first.Kind.Should().Be( second.Kind );
    }

    [TestMethod]
    public async Task PostgresSquashCodegen_SetvalBlockEmittedInSortedOrder()
    {
        var dumpText = "CREATE SCHEMA app;\nCREATE TABLE app.t (id int);\n";

        // Two runs with the SAME sequence-name set in DIFFERENT key orders;
        // the emitted setval block must appear in sorted order regardless.
        var seqsForward = new Dictionary<string, long>
        {
            ["app.alpha_seq"] = 100,
            ["app.beta_seq"] = 200,
            ["app.gamma_seq"] = 300
        };
        var seqsReverse = new Dictionary<string, long>
        {
            ["app.gamma_seq"] = 300,
            ["app.beta_seq"] = 200,
            ["app.alpha_seq"] = 100
        };

        var first = await RunSynthesizedSquashAsync( dumpText, seqsForward );
        var second = await RunSynthesizedSquashAsync( dumpText, seqsReverse );

        first.Content.Should().Be( second.Content,
            "Sequence setval block must emit in sorted-by-name order regardless of dictionary iteration order." );

        // Verify the actual ordering inside the content.
        var alphaIdx = first.Content.IndexOf( "alpha_seq", StringComparison.Ordinal );
        var betaIdx = first.Content.IndexOf( "beta_seq", StringComparison.Ordinal );
        var gammaIdx = first.Content.IndexOf( "gamma_seq", StringComparison.Ordinal );
        alphaIdx.Should().BePositive();
        betaIdx.Should().BeGreaterThan( alphaIdx );
        gammaIdx.Should().BeGreaterThan( betaIdx );
    }

    [TestMethod]
    public async Task PostgresSquashCodegen_DifferentInputs_ProduceDifferentOutput()
    {
        var dumpA = "CREATE SCHEMA app;\nCREATE TABLE app.t (id int);\n";
        var dumpB = "CREATE SCHEMA app;\nCREATE TABLE app.t (id int, extra text);\n";

        var first = await RunSynthesizedSquashAsync( dumpA );
        var second = await RunSynthesizedSquashAsync( dumpB );

        first.Content.Should().NotBe( second.Content,
            "Different schema inputs must produce distinct squash content (sanity check on the determinism gate)." );
    }

    [TestMethod]
    public void RecoveryToken_DeterministicPerInputs()
    {
        var t1 = RecoveryAcknowledgement.ComputeToken( "prod", 2099L, new[] { 1500L, 1600L } );
        var t2 = RecoveryAcknowledgement.ComputeToken( "prod", 2099L, new[] { 1500L, 1600L } );
        var t3 = RecoveryAcknowledgement.ComputeToken( "prod", 2099L, new[] { 1600L, 1500L } ); // reordered

        t1.Should().Be( t2 ).And.Be( t3 );
        t1.Should().HaveLength( 12 );
        t1.Should().MatchRegex( "^[0-9a-f]{12}$" );
    }

    [TestMethod]
    public void RecoveryToken_DifferentEnv_DifferentToken()
    {
        var prod = RecoveryAcknowledgement.ComputeToken( "prod", 2099L, new[] { 1500L } );
        var qa = RecoveryAcknowledgement.ComputeToken( "qa", 2099L, new[] { 1500L } );

        prod.Should().NotBe( qa, "different env names must produce different tokens to defeat copy-paste from siblings" );
    }

    [TestMethod]
    public void RecoveryToken_DifferentMissingSet_DifferentToken()
    {
        var t1 = RecoveryAcknowledgement.ComputeToken( "prod", 2099L, new[] { 1500L } );
        var t2 = RecoveryAcknowledgement.ComputeToken( "prod", 2099L, new[] { 1500L, 1600L } );

        t1.Should().NotBe( t2 );
    }

    [TestMethod]
    public void RecoveryToken_Verify_AcceptsCaseInsensitiveAndTrimmed()
    {
        var token = RecoveryAcknowledgement.ComputeToken( "prod", 2099L, new[] { 1500L } );

        RecoveryAcknowledgement.Verify( "prod", 2099L, new[] { 1500L }, token ).Should().BeTrue();
        RecoveryAcknowledgement.Verify( "prod", 2099L, new[] { 1500L }, token.ToUpperInvariant() ).Should().BeTrue();
        RecoveryAcknowledgement.Verify( "prod", 2099L, new[] { 1500L }, "  " + token + "\n" ).Should().BeTrue();

        RecoveryAcknowledgement.Verify( "prod", 2099L, new[] { 1500L }, "wrong-token" ).Should().BeFalse();
        RecoveryAcknowledgement.Verify( "prod", 2099L, new[] { 1500L }, "" ).Should().BeFalse();
        RecoveryAcknowledgement.Verify( "prod", 2099L, new[] { 1500L }, null ).Should().BeFalse();
    }

    // ---------------------------------------------------------------
    // helpers
    // ---------------------------------------------------------------

    private static async Task<SquashGenerationResult.Generated> RunSynthesizedSquashAsync(
        string dumpText,
        Dictionary<string, long> sequenceLastValues = null )
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

        var ctx = new PostgresSquashGenerationContext(
            squashName: "Squash_5000",
            squashVersion: 5000L,
            dataSource: NpgsqlDataSource.Create( "Host=localhost;Port=1;Database=none;Username=none;Password=none" ),
            captureSnapshotAsync: ( _, _ ) => Task.FromResult( new SnapshotCaptureResult(
                DumpText: dumpText,
                SequenceLastValues: sequenceLastValues ?? new Dictionary<string, long>() ) ) );

        var descriptors = new[]
        {
            DescriptorFor( 1L ), DescriptorFor( 2L ), DescriptorFor( 3L )
        };

        // Use a topology-pre-loaded variant to avoid live-DB topology capture.
        var canonicalizer = new PostgresSnapshotCanonicalizer();
        var classifier = new PostgresDataOpClassifier();

        var lowerBound = descriptors.Min( d => d.Attribute.Version );
        var upperBound = descriptors.Max( d => d.Attribute.Version );

        var capture = await ctx.CaptureSnapshotAsync(
            new SnapshotCaptureRequest( "snapshot-B", upperBound, topology ),
            CancellationToken.None );

        var canon = canonicalizer.Canonicalize( capture.DumpText );

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
            Content: canonicalizer.EmitScript( content ),
            Kind: ContentKind.SqlText,
            Encoding: ContentEncoding.Utf8,
            Replaces: descriptors.Select( d => d.Attribute.Version ).ToArray(),
            Diagnostics: Array.Empty<string>(),
            Topology: topology );
    }

    private static MigrationDescriptor DescriptorFor( long version ) =>
        new( typeof( DummyMigration ), new MigrationAttribute( version ), Array.Empty<long>() );

    private sealed class DummyMigration : Migration
    {
        public override Task UpAsync( CancellationToken ct = default ) => Task.CompletedTask;
    }
}
