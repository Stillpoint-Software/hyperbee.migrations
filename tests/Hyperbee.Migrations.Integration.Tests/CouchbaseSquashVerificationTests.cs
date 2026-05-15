//#define INTEGRATIONS
using System.Text;
using System.Text.Json;
using Couchbase;
using Couchbase.Management.Query;
using Hyperbee.Migrations;
using Hyperbee.Migrations.Integration.Tests.Container.Couchbase;
using Hyperbee.Migrations.Providers.Couchbase.Services;
using Hyperbee.Migrations.Providers.Couchbase.Squash;
using Hyperbee.Migrations.Squash;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hyperbee.Migrations.Integration.Tests;

#if INTEGRATIONS

// Phase 4 (R-P6): Couchbase squash verification round (A4).
//
// Contract: re-applying the historical migration range produces the same
// canonicalized state as applying the GENERATED squash content.
//
// Round-trip:
//   1. Set up GSI indexes in the test bucket.
//   2. Capture A via CouchbaseSnapshotCapture (the "historical" snapshot).
//   3. Run HybridStrategy.GenerateAsync -> Generated.Content.
//   4. Drop the indexes we created.
//   5. Apply Generated.Content by walking the canonical JSON sections and
//      recreating each index via the SDK.
//   6. Capture B.
//   7. CouchbaseSquashVerifier.VerifyAsync -> expect Success.
//
// F-1 closed: see CouchbaseSquashDeterminismTests for the
// setupAlternateAddresses fix that lets these run in CI.

[TestClass]
[DoNotParallelize]
public class CouchbaseSquashVerificationTests
{
    private IsolatedCouchbaseContainer _container;
    private ICluster _cluster;
    private ICouchbaseRestApiService _restApi;
    private HttpClient _http;
    private static readonly string[] CreatedIndexes = { "idx_ver_primary", "idx_ver_email", "idx_ver_phone" };

    [TestInitialize]
    public async Task Setup()
    {
        _container = await IsolatedCouchbaseContainer.StartAsync();
        _cluster = _container.ClusterHandle;

        var options = new ClusterOptions
        {
            ConnectionString = _container.ConnectionString,
            BootstrapHttpPort = _container.MgmtPort
        };

        _http = new HttpClient
        {
            BaseAddress = new Uri( $"http://localhost:{_container.MgmtPort}" )
        };
        _http.DefaultRequestHeaders.Authorization =
            new System.Net.Http.Headers.AuthenticationHeaderValue(
                "Basic",
                Convert.ToBase64String( Encoding.ASCII.GetBytes( "Administrator:password" ) ) );

        _restApi = new CouchbaseRestApiService(
            _http,
            new OptionsWrapper<ClusterOptions>( options ),
            NullLogger<CouchbaseRestApiService>.Instance );

        await DropTestArtifactsAsync();
    }

    [TestCleanup]
    public async Task Cleanup()
    {
        try { await DropTestArtifactsAsync(); } catch { }
        _http?.Dispose();
        if ( _container != null )
            await _container.DisposeAsync();
    }

    [TestMethod]
    public async Task EmptyBucket_RoundTrip_ReturnsSuccess()
    {
        var ctx = MakeContext();
        var generated = await GenerateAsync( ctx );

        var verifier = new CouchbaseSquashVerifier( new CouchbaseSnapshotCanonicalizer() )
        {
            CaptureFromGeneratedAsync = async ( _, _, ct ) =>
            {
                var blob = await CouchbaseSnapshotCapture.CaptureAsync( _cluster, _restApi, _container.BucketName, ct );
                return new SnapshotCaptureResult( blob );
            }
        };

        var result = await verifier.VerifyAsync( ctx, generated );

        if ( result is VerificationResult.Failed failed )
            Assert.Fail( $"Verification failed: {failed.Detail}\n{failed.DiffSummary}" );

        Assert.IsInstanceOfType<VerificationResult.Success>( result );
    }

    [TestMethod]
    public async Task PopulatedBucket_RoundTrip_ReturnsSuccess()
    {
        var queryIndexes = _cluster.QueryIndexes;

        await queryIndexes.CreatePrimaryIndexAsync( _container.BucketName,
            new CreatePrimaryQueryIndexOptions().IndexName( "idx_ver_primary" ) );
        await queryIndexes.CreateIndexAsync( _container.BucketName, "idx_ver_email", new[] { "email" } );

        var ctx = MakeContext();
        var generated = await GenerateAsync( ctx );

        // Defense-in-depth.
        StringAssert.Contains( generated.Content, "idx_ver_primary" );
        StringAssert.Contains( generated.Content, "idx_ver_email" );

        var verifier = new CouchbaseSquashVerifier( new CouchbaseSnapshotCanonicalizer() )
        {
            CaptureFromGeneratedAsync = async ( content, _, ct ) =>
            {
                // Reset bucket index state, then apply the generated squash.
                await DropTestArtifactsAsync();
                await ApplyGeneratedAsync( content, ct );
                var blob = await CouchbaseSnapshotCapture.CaptureAsync( _cluster, _restApi, _container.BucketName, ct );
                return new SnapshotCaptureResult( blob );
            }
        };

        var result = await verifier.VerifyAsync( ctx, generated );

        if ( result is VerificationResult.Failed failed )
            Assert.Fail( $"Verification failed: {failed.Detail}\n{failed.DiffSummary}" );

        Assert.IsInstanceOfType<VerificationResult.Success>( result );
    }

    // ---- helpers -----------------------------------------------------------

    private CouchbaseSquashGenerationContext MakeContext() => new(
        squashName: "Squash_2000",
        squashVersion: 2000,
        cluster: _cluster,
        restApi: _restApi,
        bucketName: _container.BucketName,
        captureSnapshotAsync: async ( _, ct ) =>
        {
            var blob = await CouchbaseSnapshotCapture.CaptureAsync( _cluster, _restApi, _container.BucketName, ct );
            return new SnapshotCaptureResult( blob );
        } );

    private async Task<SquashGenerationResult.Generated> GenerateAsync( CouchbaseSquashGenerationContext ctx )
    {
        var descriptors = new[]
        {
            new MigrationDescriptor( typeof( object ), new MigrationAttribute( 1000 ), Array.Empty<long>() ),
            new MigrationDescriptor( typeof( object ), new MigrationAttribute( 2000 ), Array.Empty<long>() ),
        };

        var strategy = new HybridStrategy(
            new CouchbaseSnapshotCanonicalizer(),
            new CouchbaseDataOpClassifier() );

        var result = await strategy.GenerateAsync( ctx, descriptors, new SquashGenerationOptions() );

        if ( result is SquashGenerationResult.Failed failed )
            Assert.Fail( $"GenerateAsync returned Failed: {failed.Detail}" );

        return (SquashGenerationResult.Generated) result;
    }

    // Walk the canonical content's [indexes] section; recreate each index
    // via the SDK. Buckets + scopes + collections are not recreated here
    // because the test fixture's _container.BucketName already exists -- buckets are
    // applied via the REST API in the full Phase 5 CLI apply path.
    private async Task ApplyGeneratedAsync( string content, CancellationToken ct )
    {
        var sections = ParseSections( content );
        var queryIndexes = _cluster.QueryIndexes;

        if ( !sections.TryGetValue( "indexes", out var indexesJson ) )
            return;

        using var doc = JsonDocument.Parse( indexesJson );
        if ( doc.RootElement.ValueKind != JsonValueKind.Object )
            return;

        // The top-level keys of [indexes] are composite identity strings
        // (bucket/scope/collection/name); each value is the index row.
        foreach ( var entry in doc.RootElement.EnumerateObject() )
        {
            var row = entry.Value;
            if ( row.ValueKind != JsonValueKind.Object )
                continue;

            string name = TryGetString( row, "name" );
            if ( string.IsNullOrEmpty( name ) )
                continue;

            bool isPrimary = TryGetBool( row, "is_primary" )
                          || string.Equals( TryGetString( row, "using" ), "gsi", StringComparison.OrdinalIgnoreCase )
                             && row.TryGetProperty( "index_key", out var ik ) && ik.ValueKind == JsonValueKind.Array && ik.GetArrayLength() == 0;

            if ( isPrimary )
            {
                await queryIndexes.CreatePrimaryIndexAsync( _container.BucketName,
                    new CreatePrimaryQueryIndexOptions().IndexName( name ).IgnoreIfExists( true ) );
                continue;
            }

            if ( !row.TryGetProperty( "index_key", out var keyArr ) || keyArr.ValueKind != JsonValueKind.Array )
                continue;

            var keys = new List<string>();
            foreach ( var k in keyArr.EnumerateArray() )
                if ( k.ValueKind == JsonValueKind.String )
                    keys.Add( k.GetString() );

            if ( keys.Count > 0 )
                await queryIndexes.CreateIndexAsync( _container.BucketName, name, keys, new CreateQueryIndexOptions().IgnoreIfExists( true ) );
        }
    }

    private async Task DropTestArtifactsAsync()
    {
        var queryIndexes = _cluster.QueryIndexes;
        foreach ( var name in CreatedIndexes )
        {
            try { await queryIndexes.DropIndexAsync( _container.BucketName, name ); } catch { }
        }
    }

    private static string TryGetString( JsonElement row, string key ) =>
        row.TryGetProperty( key, out var v ) && v.ValueKind == JsonValueKind.String ? v.GetString() : null;

    private static bool TryGetBool( JsonElement row, string key ) =>
        row.TryGetProperty( key, out var v ) && (v.ValueKind == JsonValueKind.True || v.ValueKind == JsonValueKind.False)
            ? v.GetBoolean()
            : false;

    private static Dictionary<string, string> ParseSections( string content )
    {
        var bodies = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
        string current = null;
        var buffer = new StringBuilder();

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

    private static void FlushSection( string section, StringBuilder buffer, Dictionary<string, string> bodies )
    {
        if ( section == null || buffer.Length == 0 )
            return;
        var body = buffer.ToString().Trim();
        if ( body.Length > 0 )
            bodies[section] = body;
    }
}

#endif
