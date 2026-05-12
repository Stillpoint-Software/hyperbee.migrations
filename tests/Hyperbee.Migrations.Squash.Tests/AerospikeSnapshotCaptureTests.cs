using FluentAssertions;
using Hyperbee.Migrations.Providers.Aerospike.Squash;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using NSubstitute;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 1 (R-P1, R-P8): AerospikeSnapshotCapture pure-logic coverage.
//
// CaptureAsync against a live IAerospikeClient is exercised in the Phase 1
// integration suite (AerospikeSquashDeterminismTests + AerospikeSquashVerificationTests).
// These tests cover the orchestration helpers + early guard rails:
//   - ComposeBlob: format the canonicalizer consumes
//   - CaptureAsync guards: null client, empty namespace, no-connected-nodes

[TestClass]
public class AerospikeSnapshotCaptureTests
{
    [TestMethod]
    public void ComposeBlob_ProducesSectionHeaderedFormat()
    {
        var blob = AerospikeSnapshotCapture.ComposeBlob(
            @namespace: "test",
            setsResponse: "ns=test:set=users:objects=42",
            sindexResponse: "ns=test:indexname=idx:set=users:bin=name:type=STRING" );

        blob.Should().Contain( "# aerospike-snapshot v1" );
        blob.Should().Contain( "# namespace: test" );
        blob.Should().Contain( "[sets]\nns=test:set=users:objects=42" );
        blob.Should().Contain( "[sindex]\nns=test:indexname=idx" );
    }

    [TestMethod]
    public void ComposeBlob_EmptyResponses_StillEmitsBothSections()
    {
        var blob = AerospikeSnapshotCapture.ComposeBlob(
            @namespace: "test",
            setsResponse: "",
            sindexResponse: "" );

        // Canonicalizer must accept an empty-namespace snapshot and emit the
        // header-only canonical form. Both section headers must appear so the
        // section parser sees them.
        blob.Should().Contain( "[sets]" );
        blob.Should().Contain( "[sindex]" );
    }

    [TestMethod]
    public void ComposeBlob_NullResponses_TreatedAsEmpty()
    {
        var blob = AerospikeSnapshotCapture.ComposeBlob( "test", null, null );

        blob.Should().Contain( "[sets]" );
        blob.Should().Contain( "[sindex]" );
    }

    [TestMethod]
    public void ComposeBlob_RoundTripsThroughCanonicalizer()
    {
        // The capture format must canonicalize cleanly. Confirm the
        // canonicalizer emits the expected statements for a typical fixture.
        var blob = AerospikeSnapshotCapture.ComposeBlob(
            @namespace: "test",
            setsResponse: "ns=test:set=users:objects=42",
            sindexResponse: "ns=test:indexname=idx_email:set=users:bin=email:type=STRING:keys=10" );

        var canon = new AerospikeSnapshotCanonicalizer().Canonicalize( blob );

        canon.Should().Contain( "CREATE SET test.users;" );
        canon.Should().Contain( "CREATE INDEX WAIT idx_email ON test.users(email) STRING;" );
        canon.Should().NotContain( "objects=" );
        canon.Should().NotContain( "keys=" );
    }

    [TestMethod]
    public async Task CaptureAsync_NullClient_Throws()
    {
        Func<Task> act = () => AerospikeSnapshotCapture.CaptureAsync( null!, "test" );
        await act.Should().ThrowAsync<ArgumentNullException>().WithParameterName( "client" );
    }

    [TestMethod]
    public async Task CaptureAsync_EmptyNamespace_Throws()
    {
        var client = Substitute.For<Aerospike.Client.IAerospikeClient>();

        Func<Task> act = () => AerospikeSnapshotCapture.CaptureAsync( client, "" );
        await act.Should().ThrowAsync<ArgumentException>().WithParameterName( "namespace" );
    }

    [TestMethod]
    public async Task CaptureAsync_NoConnectedNodes_ThrowsMigrationException()
    {
        var client = Substitute.For<Aerospike.Client.IAerospikeClient>();
        client.Nodes.Returns( Array.Empty<Aerospike.Client.Node>() );

        Func<Task> act = () => AerospikeSnapshotCapture.CaptureAsync( client, "test" );
        await act.Should().ThrowAsync<MigrationException>()
            .WithMessage( "*no connected nodes*" );
    }

    [TestMethod]
    public async Task CaptureAsync_CancelledToken_Throws()
    {
        var client = Substitute.For<Aerospike.Client.IAerospikeClient>();
        using var cts = new CancellationTokenSource();
        cts.Cancel();

        Func<Task> act = () => AerospikeSnapshotCapture.CaptureAsync( client, "test", cts.Token );
        await act.Should().ThrowAsync<OperationCanceledException>();
    }

    // ---- UDF probe ---------------------------------------------------------

    [TestMethod]
    public void ParseUdfList_RecognizesCommaSeparatedKeyValueEntries()
    {
        const string response = "filename=foo.lua,hash=abc123,type=LUA;filename=bar.lua,hash=def456,type=LUA";

        var udfs = AerospikeSnapshotCapture.ParseUdfList( response );

        udfs.Should().HaveCount( 2 );
        udfs.Should().Contain( "foo.lua" );
        udfs.Should().Contain( "bar.lua" );
    }

    [TestMethod]
    public void ParseUdfList_EmptyResponse_ReturnsEmpty()
    {
        AerospikeSnapshotCapture.ParseUdfList( "" ).Should().BeEmpty();
        AerospikeSnapshotCapture.ParseUdfList( null ).Should().BeEmpty();
    }

    [TestMethod]
    public void ParseUdfList_SortsEntriesOrdinal()
    {
        // Sort order matters: the strategy emits a diagnostic containing the
        // UDF list and it should be deterministic for log/CI comparison.
        const string response = "filename=zzz.lua,type=LUA;filename=aaa.lua,type=LUA;filename=mmm.lua,type=LUA";

        var udfs = AerospikeSnapshotCapture.ParseUdfList( response );

        udfs.Should().Equal( "aaa.lua", "mmm.lua", "zzz.lua" );
    }

    [TestMethod]
    public void ParseUdfList_SkipsMalformedEntries()
    {
        const string response = "filename=ok.lua,type=LUA;;orphan;type=LUA;filename=,type=LUA";

        var udfs = AerospikeSnapshotCapture.ParseUdfList( response );

        // Only the well-formed `ok.lua` entry survives.
        udfs.Should().Equal( "ok.lua" );
    }

    [TestMethod]
    public void ListUdfs_NullClient_Throws()
    {
        Action act = () => AerospikeSnapshotCapture.ListUdfs( null! );
        act.Should().Throw<ArgumentNullException>().WithParameterName( "client" );
    }

    [TestMethod]
    public void ListUdfs_NoConnectedNodes_ReturnsEmpty()
    {
        // No-nodes case must NOT throw; treats absence-of-cluster as
        // absence-of-UDFs so the strategy's refusal logic flows cleanly
        // into the normal "no nodes" topology error instead.
        var client = Substitute.For<Aerospike.Client.IAerospikeClient>();
        client.Nodes.Returns( Array.Empty<Aerospike.Client.Node>() );

        AerospikeSnapshotCapture.ListUdfs( client ).Should().BeEmpty();
    }
}
