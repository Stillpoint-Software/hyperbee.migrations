//#define INTEGRATIONS
using System.Text.Json;
using Hyperbee.Migrations;
using Hyperbee.Migrations.Integration.Tests.Container.OpenSearch;
using Hyperbee.Migrations.Providers.OpenSearch.Squash;
using Hyperbee.Migrations.Squash;
using OpenSearch.Client;
using OpenSearch.Net;
using HttpMethod = OpenSearch.Net.HttpMethod;

namespace Hyperbee.Migrations.Integration.Tests;

#if INTEGRATIONS

// Phase 2 (R-P6): OpenSearch squash verification round (A4).
//
// Contract: re-applying the historical migration range produces the same
// canonicalized state as applying the GENERATED squash content. This is the
// load-bearing equivalence proof that the squash codegen captures the full
// structural state.
//
// Round-trip flow:
//   1. Set up structural state on the namespace (indices + templates).
//   2. Capture state A via the production helper (the "historical" snapshot).
//   3. Run RestStateDiffStrategy.GenerateAsync -> Generated.Content (JSON).
//   4. Wipe the test-prefixed objects.
//   5. Apply Generated.Content by walking the canonical JSON sections and
//      PUTting each item back via the corresponding REST endpoint.
//   6. Capture state B.
//   7. OpenSearchSquashVerifier.VerifyAsync -> expect Success.
//
// Guarded by `#if INTEGRATIONS`; run locally with /p:EnableIntegrationTests=true.

[TestClass]
[DoNotParallelize]
public class OpenSearchSquashVerificationTests
{
    private IOpenSearchClient _client;
    private const string TestPrefix = "verifyround_";

    [TestInitialize]
    public void Setup()
    {
        _client = OpenSearchTestContainer.Client;
        Assert.IsNotNull( _client, "OpenSearchTestContainer must initialize before this test class runs." );
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        await WipeTestObjectsAsync( CancellationToken.None );
    }

    [TestMethod]
    public async Task EmptyCluster_RoundTrip_ReturnsSuccess()
    {
        // Trivial baseline: empty cluster -> both A and B canonicalize to the
        // header-only form. Proves the verifier wiring works end-to-end before
        // the populated test exercises real structural state.
        var topology = await OpenSearchTopologySignature.CaptureAsync( _client );
        var ctx = MakeContext( topology );
        var generated = await GenerateAsync( ctx );

        var verifier = new OpenSearchSquashVerifier( new OpenSearchSnapshotCanonicalizer() )
        {
            CaptureFromGeneratedAsync = async ( _, _, ct ) =>
            {
                var blob = await OpenSearchSnapshotCapture.CaptureAsync( _client, topology.IsmPathPrefix, ct );
                return new SnapshotCaptureResult( blob );
            }
        };

        var result = await verifier.VerifyAsync( ctx, generated );

        if ( result is VerificationResult.Failed failed )
            Assert.Fail( $"Verification failed: {failed.Detail}\n{failed.DiffSummary}" );

        Assert.IsInstanceOfType<VerificationResult.Success>( result );
    }

    [TestMethod]
    public async Task PopulatedCluster_RoundTrip_ReturnsSuccess()
    {
        // Set up structural state: two indices + a component template.
        await _client.Indices.CreateAsync( $"{TestPrefix}users",
            i => i.Map( m => m.Properties( p => p.Text( t => t.Name( "email" ) ) ) ) );
        await _client.Indices.CreateAsync( $"{TestPrefix}orders",
            i => i.Map( m => m.Properties( p => p.Keyword( k => k.Name( "order_id" ) ) ) ) );

        var topology = await OpenSearchTopologySignature.CaptureAsync( _client );
        var ctx = MakeContext( topology );
        var generated = await GenerateAsync( ctx );

        // Defense-in-depth: confirm the squash captured the structural
        // objects we created. If this fails, the strategy isn't producing
        // a meaningful squash and the verifier test below is testing
        // nothing useful.
        StringAssert.Contains( generated.Content, $"{TestPrefix}users" );
        StringAssert.Contains( generated.Content, $"{TestPrefix}orders" );

        var verifier = new OpenSearchSquashVerifier( new OpenSearchSnapshotCanonicalizer() )
        {
            // CaptureFromGenerated: wipe test-prefixed objects, apply the
            // generated content via JSON-section walker, capture post-apply.
            CaptureFromGeneratedAsync = async ( content, _, ct ) =>
            {
                await WipeTestObjectsAsync( ct );
                await ApplyGeneratedAsync( content, ct );
                var blob = await OpenSearchSnapshotCapture.CaptureAsync( _client, topology.IsmPathPrefix, ct );
                return new SnapshotCaptureResult( blob );
            }
        };

        var result = await verifier.VerifyAsync( ctx, generated );

        if ( result is VerificationResult.Failed failed )
            Assert.Fail( $"Verification failed: {failed.Detail}\n{failed.DiffSummary}" );

        Assert.IsInstanceOfType<VerificationResult.Success>( result );
    }

    // ---- helpers -----------------------------------------------------------

    private OpenSearchSquashGenerationContext MakeContext( OpenSearchTopologySignature topology ) => new(
        squashName: "Squash_2000",
        squashVersion: 2000,
        client: _client,
        captureSnapshotAsync: async ( _, ct ) =>
        {
            var blob = await OpenSearchSnapshotCapture.CaptureAsync( _client, topology.IsmPathPrefix, ct );
            return new SnapshotCaptureResult( blob );
        } );

    private async Task<SquashGenerationResult.Generated> GenerateAsync( OpenSearchSquashGenerationContext ctx )
    {
        var descriptors = new[]
        {
            new MigrationDescriptor( typeof( object ), new MigrationAttribute( 1000 ), Array.Empty<long>() ),
            new MigrationDescriptor( typeof( object ), new MigrationAttribute( 2000 ), Array.Empty<long>() ),
        };

        var strategy = new RestStateDiffStrategy(
            new OpenSearchSnapshotCanonicalizer(),
            new OpenSearchDataOpClassifier() );

        var result = await strategy.GenerateAsync( ctx, descriptors, new SquashGenerationOptions() );

        if ( result is SquashGenerationResult.Failed failed )
            Assert.Fail( $"GenerateAsync returned Failed: {failed.Detail}" );

        return (SquashGenerationResult.Generated) result;
    }

    // Walk the canonical content's section-headered JSON and PUT each item
    // back to the cluster via the corresponding REST endpoint. This is the
    // minimum viable apply path that proves the round-trip; the Phase 5 CLI
    // ships the full version covering ingest pipelines, ISM policies, alias
    // graph updates, etc. For Phase 2 R-P6 we handle the load-bearing
    // sections: [index_template], [component_template], [index_metadata].
    private async Task ApplyGeneratedAsync( string content, CancellationToken ct )
    {
        var sections = ParseSections( content );

        if ( sections.TryGetValue( "component_template", out var componentTemplates ) )
            await ApplyTopLevelMapAsync( componentTemplates, "/_component_template", ct );

        if ( sections.TryGetValue( "index_template", out var indexTemplates ) )
            await ApplyTopLevelMapAsync( indexTemplates, "/_index_template", ct );

        if ( sections.TryGetValue( "index_metadata", out var indexMetadata ) )
            await ApplyIndexMetadataAsync( indexMetadata, ct );

        // [alias] and [ingest_pipeline] not exercised by the populated test;
        // their apply shapes are similar (PUT per top-level key) and the
        // canonicalizer is already proven to round-trip them.
    }

    private async Task ApplyTopLevelMapAsync( string sectionJson, string basePath, CancellationToken ct )
    {
        using var doc = JsonDocument.Parse( sectionJson );
        if ( doc.RootElement.ValueKind != JsonValueKind.Object )
            return;

        foreach ( var entry in doc.RootElement.EnumerateObject() )
        {
            var name = entry.Name;
            if ( !name.StartsWith( TestPrefix, StringComparison.Ordinal ) )
                continue; // only apply our test-prefixed items

            var body = entry.Value.GetRawText();
            var resp = await _client.LowLevel.DoRequestAsync<StringResponse>(
                HttpMethod.PUT, $"{basePath}/{name}", ct,
                PostData.String( body ) ).ConfigureAwait( false );

            if ( !resp.Success )
                throw new InvalidOperationException(
                    $"Apply PUT {basePath}/{name} failed: HTTP {resp.HttpStatusCode}: {resp.Body}" );
        }
    }

    private async Task ApplyIndexMetadataAsync( string sectionJson, CancellationToken ct )
    {
        using var doc = JsonDocument.Parse( sectionJson );
        if ( doc.RootElement.ValueKind != JsonValueKind.Object )
            return;

        foreach ( var entry in doc.RootElement.EnumerateObject() )
        {
            var indexName = entry.Name;
            if ( !indexName.StartsWith( TestPrefix, StringComparison.Ordinal ) )
                continue;

            // The index body from GET /_all includes {settings, mappings,
            // aliases}; PUT /<index> with that shape recreates the index.
            // Strip server-injected settings.index.* fields that PUT would
            // reject (uuid, creation_date, version) -- the canonicalizer
            // already stripped these but a belt-and-braces filter here is
            // cheap insurance against future canonicalizer evolution.
            var indexBody = StripPutRejectedFields( entry.Value.GetRawText() );

            var resp = await _client.LowLevel.DoRequestAsync<StringResponse>(
                HttpMethod.PUT, $"/{indexName}", ct,
                PostData.String( indexBody ) ).ConfigureAwait( false );

            if ( !resp.Success )
                throw new InvalidOperationException(
                    $"Apply PUT /{indexName} failed: HTTP {resp.HttpStatusCode}: {resp.Body}" );
        }
    }

    private static string StripPutRejectedFields( string indexBodyJson )
    {
        // The canonicalizer already strips creation_date, uuid, version,
        // provided_name. This defensive filter ensures any future canonical-
        // emission change does not regress the apply path.
        using var doc = JsonDocument.Parse( indexBodyJson );
        using var stream = new MemoryStream();
        using ( var writer = new System.Text.Json.Utf8JsonWriter( stream ) )
        {
            WriteFiltered( writer, doc.RootElement );
        }
        return System.Text.Encoding.UTF8.GetString( stream.ToArray() );

        static void WriteFiltered( System.Text.Json.Utf8JsonWriter w, JsonElement el )
        {
            switch ( el.ValueKind )
            {
                case JsonValueKind.Object:
                    w.WriteStartObject();
                    foreach ( var p in el.EnumerateObject() )
                    {
                        if ( p.Name is "creation_date" or "uuid" or "version" or "provided_name" )
                            continue;
                        w.WritePropertyName( p.Name );
                        WriteFiltered( w, p.Value );
                    }
                    w.WriteEndObject();
                    break;
                case JsonValueKind.Array:
                    w.WriteStartArray();
                    foreach ( var item in el.EnumerateArray() )
                        WriteFiltered( w, item );
                    w.WriteEndArray();
                    break;
                case JsonValueKind.String:
                    w.WriteStringValue( el.GetString() );
                    break;
                case JsonValueKind.Number:
                    w.WriteRawValue( el.GetRawText() );
                    break;
                case JsonValueKind.True:
                    w.WriteBooleanValue( true );
                    break;
                case JsonValueKind.False:
                    w.WriteBooleanValue( false );
                    break;
                case JsonValueKind.Null:
                    w.WriteNullValue();
                    break;
            }
        }
    }

    // Walk the canonical content's `[section]` headers and collect section
    // bodies. Equivalent to OpenSearchSnapshotCanonicalizer.ParseSections
    // but local to the test (the canonicalizer's method is internal).
    private static Dictionary<string, string> ParseSections( string content )
    {
        var bodies = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
        string current = null;
        var buffer = new System.Text.StringBuilder();

        foreach ( var rawLine in content.Split( '\n' ) )
        {
            var line = rawLine.TrimEnd( '\r' );
            var trimmed = line.TrimStart();

            if ( trimmed.StartsWith( '[' ) && trimmed.EndsWith( "]", StringComparison.Ordinal ) )
            {
                FlushSection( current, buffer, bodies );
                current = trimmed.Substring( 1, trimmed.Length - 2 ).Trim().ToLowerInvariant();
                buffer.Clear();
                continue;
            }

            if ( current == null )
                continue;

            buffer.Append( line ).Append( '\n' );
        }

        FlushSection( current, buffer, bodies );
        return bodies;
    }

    private static void FlushSection( string section, System.Text.StringBuilder buffer, Dictionary<string, string> bodies )
    {
        if ( section == null || buffer.Length == 0 )
            return;
        var body = buffer.ToString().Trim();
        if ( body.Length > 0 )
            bodies[section] = body;
    }

    private async Task WipeTestObjectsAsync( CancellationToken ct )
    {
        try { await _client.Indices.DeleteAsync( $"{TestPrefix}*", d => d, ct ); } catch { }
        try { await _client.LowLevel.DoRequestAsync<StringResponse>(
            HttpMethod.DELETE, $"/_index_template/{TestPrefix}*", ct ); } catch { }
        try { await _client.LowLevel.DoRequestAsync<StringResponse>(
            HttpMethod.DELETE, $"/_component_template/{TestPrefix}*", ct ); } catch { }
    }
}

#endif
