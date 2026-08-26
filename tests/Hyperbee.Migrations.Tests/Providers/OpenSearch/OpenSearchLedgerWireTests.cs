#nullable enable
using System.Text;
using FluentAssertions;
using Hyperbee.Migrations.Providers.OpenSearch;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Bootstrap;
using Microsoft.Extensions.Logging.Abstractions;
using OpenSearch.Client;
using OpenSearch.Net;

namespace Hyperbee.Migrations.Tests.Providers.OpenSearch;

// Wire-shape tests for the ledger record store.
//
// These drive the REAL OpenSearch.Client (real descriptors, real serializer,
// real request pipeline) over an InMemoryConnection, so request construction
// and serialization actually execute -- only the socket is faked. That is the
// test tier the suite was missing: OpenSearchRecordStoreTests substitutes
// IOpenSearchClient, so nothing in it ever serializes a request, and the
// container-backed integration tests are compile-gated behind
// `#if INTEGRATIONS` + [TestCategory("LocalOnly")] and excluded from CI.
//
// The invariant under test:
//
//   Every ledger request the provider issues must carry its target index
//   EXPLICITLY. The provider must NEVER depend on OpenSearch.Client's
//   type -> index inference (ConnectionSettings.DefaultMappingFor<T>() /
//   DefaultIndex()), because that is CONSUMER-owned client configuration while
//   the ledger index name is LIBRARY-owned migration configuration
//   (OpenSearchMigrationOptions.LedgerIndex). The library cannot require a
//   consumer to declare a mapping for a document type the library keeps internal.
//
// Regression this pins (v3.0.0 - v3.1.0):
//
//   IntersectWithAppliedAsync built its _mget with `.GetMany<T>( ids )`, which
//   stamps each body entry's index from `IndexName.From<OpenSearchMigrationRecord>()`.
//   With no DefaultMappingFor / DefaultIndex on the ConnectionSettings -- which is
//   exactly what AddOpenSearchClient and AddOpenSearchAwsClient build -- request
//   serialization threw before a byte reached the wire:
//
//       UnexpectedOpenSearchClientException: Index name is null for the given
//       type and no default index is set.
//
//   MigrationRunner.RunAsync calls IntersectWithAppliedAsync unconditionally
//   whenever at least one migration is discovered, so this killed every run.

[TestClass]
public class OpenSearchLedgerWireTests
{
    private const string LedgerIndex = ".hyperbee-migrations-test";

    // A ConnectionSettings built the way the shipped client factories build it:
    // endpoint (+ transport) and auth only. NO DefaultMappingFor, NO DefaultIndex.
    // If a ledger request needs type -> index inference, it fails here.
    private static IOpenSearchClient BareClient(
        string responseJson,
        int statusCode = 200,
        List<IApiCallDetails>? calls = null )
    {
        var connection = new InMemoryConnection( Encoding.UTF8.GetBytes( responseJson ), statusCode );
        var pool = new SingleNodeConnectionPool( new Uri( "http://localhost:9200" ) );

        var settings = new ConnectionSettings( pool, connection )
            .DisableDirectStreaming(); // retain RequestBodyInBytes for wire assertions

        if ( calls is not null )
            settings = settings.OnRequestCompleted( calls.Add );

        return new OpenSearchClient( settings );
    }

    private static OpenSearchRecordStore BuildStore( IOpenSearchClient client )
    {
        var options = new OpenSearchMigrationOptions { LedgerIndex = LedgerIndex };

        var bootstrapper = new OpenSearchBootstrapper(
            Array.Empty<IBootstrapStep>(), client, options, TimeProvider.System, NullLoggerFactory.Instance );

        return new OpenSearchRecordStore(
            client, bootstrapper, options, TimeProvider.System,
            NullLogger<OpenSearchRecordStore>.Instance );
    }

    // ---- the regression --------------------------------------------------

    [TestMethod]
    public async Task IntersectWithAppliedAsync_ClientWithoutTypeIndexInference_DoesNotThrow()
    {
        // The defining case. A client with no DefaultMappingFor<OpenSearchMigrationRecord>
        // and no DefaultIndex is what every consumer gets from AddOpenSearchClient /
        // AddOpenSearchAwsClient. Reconciliation must work on it.
        var store = BuildStore( BareClient( """{"docs":[]}""" ) );

        var act = async () => await store.IntersectWithAppliedAsync( ["1000.alpha", "1001.beta"] );

        await act.Should().NotThrowAsync(
            "the ledger index is library-owned configuration (OpenSearchMigrationOptions.LedgerIndex); " +
            "the provider must not require consumer-side type -> index inference" );
    }

    [TestMethod]
    public async Task IntersectWithAppliedAsync_TargetsLedgerIndex_WithRealtimeSemantics()
    {
        var calls = new List<IApiCallDetails>();
        var store = BuildStore( BareClient( """{"docs":[]}""", calls: calls ) );

        await store.IntersectWithAppliedAsync( ["1000.alpha", "1001.beta"] );

        calls.Should().ContainSingle();
        var uri = calls[0].Uri!;

        uri.AbsolutePath.Should().Be( $"/{LedgerIndex}/_mget",
            "the ledger index comes from OpenSearchMigrationOptions.LedgerIndex, not from the CLR type" );
        uri.Query.Should().Contain( "realtime=true",
            "reconciliation reads through the translog, not the refresh-bound search path (ADR-0019 Phase 3)" );

        var body = Encoding.UTF8.GetString( calls[0].RequestBodyInBytes ?? [] );
        body.Should().Contain( "1000.alpha" ).And.Contain( "1001.beta" );
        body.Should().NotContain( "OpenSearchMigrationRecord",
            "no CLR type name may leak into the request body" );
    }

    [TestMethod]
    public async Task IntersectWithAppliedAsync_ReturnsOnlyFoundIds()
    {
        // _mget answers for every requested id; only `found: true` entries count as applied.
        const string body = """
            {
              "docs": [
                { "_index": ".hyperbee-migrations-test", "_id": "1000.alpha", "found": true,  "_source": { "id": "1000.alpha" } },
                { "_index": ".hyperbee-migrations-test", "_id": "1001.beta",  "found": false },
                { "_index": ".hyperbee-migrations-test", "_id": "1002.gamma", "found": true,  "_source": { "id": "1002.gamma" } }
              ]
            }
            """;

        var store = BuildStore( BareClient( body ) );

        var applied = await store.IntersectWithAppliedAsync( ["1000.alpha", "1001.beta", "1002.gamma"] );

        applied.Should().BeEquivalentTo( ["1000.alpha", "1002.gamma"] );
    }

    [TestMethod]
    public async Task IntersectWithAppliedAsync_EmptyCandidates_IssuesNoRequest()
    {
        // Guard the short-circuit. It is also why the defect survived any smoke
        // test that happened to discover zero migrations: with no candidates the
        // broken request is never built.
        var calls = new List<IApiCallDetails>();
        var store = BuildStore( BareClient( "", statusCode: 500, calls: calls ) );

        var applied = await store.IntersectWithAppliedAsync( [] );

        applied.Should().BeEmpty();
        calls.Should().BeEmpty();
    }

    // ---- the generalized invariant ---------------------------------------

    [TestMethod]
    public async Task AllLedgerOperations_OnClientWithoutTypeIndexInference_DoNotThrow()
    {
        // Every ledger path, on a bare client. This is the guard that catches the
        // NEXT inference regression, not just this one: any provider call site that
        // reintroduces implicit type -> index resolution shows up here.
        var failures = new List<string>();

        await Probe( nameof( OpenSearchRecordStore.ExistsAsync ), """{"found": false}""",
            store => store.ExistsAsync( "1000.alpha" ) );

        await Probe( nameof( OpenSearchRecordStore.ReadAsync ), """{"found": false}""",
            store => store.ReadAsync( "1000.alpha" ) );

        await Probe( nameof( OpenSearchRecordStore.WriteAsync ), """{"result": "created", "_id": "1000.alpha"}""",
            store => store.WriteAsync( "1000.alpha" ) );

        await Probe( nameof( OpenSearchRecordStore.DeleteAsync ), """{"result": "deleted", "_id": "1000.alpha"}""",
            store => store.DeleteAsync( "1000.alpha" ) );

        await Probe( nameof( OpenSearchRecordStore.IntersectWithAppliedAsync ), """{"docs":[]}""",
            store => store.IntersectWithAppliedAsync( ["1000.alpha"] ) );

        await Probe( nameof( OpenSearchRecordStore.IntersectWithSquashedAsync ), """{"hits":{"total":{"value":0},"hits":[]}}""",
            store => store.IntersectWithSquashedAsync( [1000L] ) );

        failures.Should().BeEmpty();

        async Task Probe( string name, string responseJson, Func<OpenSearchRecordStore, Task> act )
        {
            try
            {
                await act( BuildStore( BareClient( responseJson ) ) );
            }
            catch ( Exception ex ) when ( IsIndexInferenceFailure( ex ) )
            {
                failures.Add( $"{name} depends on type -> index inference: {ex.Message}" );
            }
            catch
            {
                // Any other failure is a canned-response artifact, not the
                // invariant under test. Only inference failures are counted.
            }
        }

        static bool IsIndexInferenceFailure( Exception? ex )
        {
            while ( ex is not null )
            {
                if ( ex.Message.Contains( "Index name is null for the given type", StringComparison.Ordinal ) )
                    return true;
                ex = ex.InnerException;
            }
            return false;
        }
    }
}
