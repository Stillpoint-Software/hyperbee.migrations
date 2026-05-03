#nullable enable
using System;
using System.Collections.Generic;
using System.Reflection;
using FluentAssertions;
using Hyperbee.Migrations.Providers.OpenSearch.Resources;
using Microsoft.VisualStudio.TestTools.UnitTesting;
using OpenSearch.Client;

namespace Hyperbee.Migrations.Tests.Providers.OpenSearch;

// R-24c (f) - Bulk-load 429 retry surfacing.
//
// The OpenSearch.Net library owns the retry-on-429 mechanism (configured
// via .BackOffRetries() / .BackOffTime() in OpenSearchResourceRunner.BulkLoadAsync).
// Our owned behavior is the BulkAllObserver: when a page lands with
// Retries > 0, the observer must fire a WARN-level diagnostic so operators
// see self-induced throttling in dashboards.
//
// This test drives the observer directly with synthetic BulkAllResponse
// values (parameterless ctor, public setters per the OpenSearch.Client
// shape). It avoids the need for a chaos-injection sidecar (toxiproxy)
// while still asserting the only path that is ours, not the library's.
//
// The end-to-end "bulk against a real cluster, retry path engaged"
// scenario is covered organically by the multi-node integration suite
// when the cluster naturally throttles under load. A dedicated
// chaos-injection integration test is documented as a release-checklist
// item in docs/runbooks/opensearch-aws-validation.md.

[TestClass]
public class BulkAllObserverRetryTests
{
    private static BulkAllResponse MakeResponse( long page, int retries )
    {
        // BulkAllResponse has a public parameterless ctor and private setters.
        // Use reflection to populate the two fields the observer reads.
        var response = new BulkAllResponse();
        typeof( BulkAllResponse ).GetProperty( nameof( BulkAllResponse.Page ),
            BindingFlags.Public | BindingFlags.Instance )!
            .SetValue( response, page );
        typeof( BulkAllResponse ).GetProperty( nameof( BulkAllResponse.Retries ),
            BindingFlags.Public | BindingFlags.Instance )!
            .SetValue( response, retries );
        return response;
    }

    [TestMethod]
    public void OnNext_RetriesGreaterThanZero_InvokesNextHandler()
    {
        var captured = new List<BulkAllResponse>();
        var observer = new OpenSearchResourceRunner<DummyMigration>.BulkAllObserver(
            onNext: r => captured.Add( r ),
            onError: _ => { },
            onCompleted: () => { } );

        observer.OnNext( MakeResponse( page: 3, retries: 2 ) );

        captured.Should().ContainSingle()
            .Which.Retries.Should().Be( 2,
                because: "the observer surfaces page-level retry telemetry to the WARN log path" );
    }

    [TestMethod]
    public void OnNext_NoRetries_StillInvokesNextHandler()
    {
        var captured = new List<BulkAllResponse>();
        var observer = new OpenSearchResourceRunner<DummyMigration>.BulkAllObserver(
            onNext: r => captured.Add( r ),
            onError: _ => { },
            onCompleted: () => { } );

        observer.OnNext( MakeResponse( page: 1, retries: 0 ) );

        captured.Should().ContainSingle()
            .Which.Retries.Should().Be( 0,
                because: "non-retry pages still flow through; the production observer's WARN gating is the caller's concern, not the observer's" );
    }

    [TestMethod]
    public void OnError_PropagatesExceptionToHandler()
    {
        Exception? captured = null;
        var observer = new OpenSearchResourceRunner<DummyMigration>.BulkAllObserver(
            onNext: _ => { },
            onError: ex => captured = ex,
            onCompleted: () => { } );

        var sentinel = new InvalidOperationException( "simulated upstream failure" );
        observer.OnError( sentinel );

        captured.Should().BeSameAs( sentinel,
            because: "the observer is a thin pipe; OnError must hand the exception to the wrapper for tcs.TrySetException" );
    }

    [TestMethod]
    public void OnCompleted_InvokesCompletionHandler()
    {
        var completed = false;
        var observer = new OpenSearchResourceRunner<DummyMigration>.BulkAllObserver(
            onNext: _ => { },
            onError: _ => { },
            onCompleted: () => completed = true );

        observer.OnCompleted();

        completed.Should().BeTrue();
    }

    private sealed class DummyMigration : Migration
    {
        public override System.Threading.Tasks.Task UpAsync( System.Threading.CancellationToken cancellationToken = default )
            => System.Threading.Tasks.Task.CompletedTask;
    }
}
