#nullable enable
using FluentAssertions;
using Hyperbee.Migrations;
using Hyperbee.Migrations.Providers.OpenSearch;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Dispatch;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Grammar;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Middleware;
using Hyperbee.Migrations.Providers.OpenSearch.Resources;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenSearch.Client;

namespace Hyperbee.Migrations.Tests.Providers.OpenSearch;

// R-15 — file-level context filter at the resource-runner entry point.
//
// The filter runs before any statement is parsed/dispatched, so we can
// exercise it with a substituted client (no live cluster needed). Skipping
// returns cleanly; the file-has-context-but-ActiveContext-null case
// throws under RequireExplicit and skips with INFO under SkipIfUnset
// (the SDK default). Matching is comma-separated, case-sensitive.

[TestClass]
public class OpenSearchContextFilterTests
{
    // Same scaffolding pattern as the rollback tests — no [Migration]
    // attribute so RunnerTests assembly scans don't pick this up.
    private sealed class FakeMigration : Migration
    {
        public override Task UpAsync( CancellationToken cancellationToken = default ) => Task.CompletedTask;
    }

    private static OpenSearchResourceRunner<FakeMigration> BuildRunner( OpenSearchMigrationOptions options )
    {
        var client = Substitute.For<IOpenSearchClient>();
        var dispatcher = new StatementDispatcher( new SafeDefaultMergeMiddleware() );
        var parser = new OpenSearchStatementParser();
        var recordStore = Substitute.For<IMigrationRecordStore>();
        return new OpenSearchResourceRunner<FakeMigration>(
            client, options, dispatcher, parser, TimeProvider.System,
            NullLogger<FakeMigration>.Instance, recordStore );
    }

    private const string JsonNoContext = """
        { "statements": [ { "statement": "REFRESH users" } ] }
        """;

    private const string JsonContextProd = """
        {
          "context": ["prod"],
          "statements": [ { "statement": "REFRESH users" } ]
        }
        """;

    private const string JsonContextProdStaging = """
        {
          "context": ["prod", "staging"],
          "statements": [ { "statement": "REFRESH users" } ]
        }
        """;

    // ---- No context block: always run regardless of ActiveContext ----

    [TestMethod]
    public async Task NoContextBlock_RunsRegardlessOfActiveContext()
    {
        var options = new OpenSearchMigrationOptions { ActiveContext = null };
        var runner = BuildRunner( options );

        // The substituted client has no Indices.RefreshAsync stub so the
        // dispatcher will fail when it actually tries to dispatch. The
        // failure means we PASSED the context gate — exactly what we want
        // to verify. A clean skip would have thrown nothing (early return).
        var act = async () => await runner.RunStatementsFromJsonAsync( JsonNoContext );
        await act.Should().ThrowAsync<Exception>(
            "context gate should pass and dispatch should be attempted (and fail with the substituted client)" );
    }

    // ---- Context match: file's context intersects ActiveContext ----

    [TestMethod]
    public async Task ContextMatches_Runs()
    {
        var options = new OpenSearchMigrationOptions { ActiveContext = "prod" };
        var runner = BuildRunner( options );

        var act = async () => await runner.RunStatementsFromJsonAsync( JsonContextProd );
        await act.Should().ThrowAsync<Exception>(
            "context matched, dispatch attempted (and fails on substituted client)" );
    }

    [TestMethod]
    public async Task CommaSeparatedActiveContext_AnyTagMatch_Runs()
    {
        // ActiveContext can carry multiple tags so a single deployment can
        // claim membership in several contexts.
        var options = new OpenSearchMigrationOptions { ActiveContext = "canary,prod" };
        var runner = BuildRunner( options );

        var act = async () => await runner.RunStatementsFromJsonAsync( JsonContextProdStaging );
        await act.Should().ThrowAsync<Exception>(
            "ActiveContext `canary,prod` intersects file context `[prod, staging]`" );
    }

    // ---- Context mismatch: silent skip ----

    [TestMethod]
    public async Task ContextMismatch_SkipsCleanly()
    {
        var options = new OpenSearchMigrationOptions { ActiveContext = "dev" };
        var runner = BuildRunner( options );

        // Skipped resources return cleanly — no dispatch attempt, no throw.
        var act = async () => await runner.RunStatementsFromJsonAsync( JsonContextProd );
        await act.Should().NotThrowAsync(
            "ActiveContext `dev` does not match file context `[prod]`; runner returns early" );
    }

    [TestMethod]
    public async Task ContextMatch_IsCaseSensitive()
    {
        // Context tags are identifiers, not free-form text; matching is
        // case-sensitive so `prod` and `Prod` are distinct.
        var options = new OpenSearchMigrationOptions { ActiveContext = "Prod" };
        var runner = BuildRunner( options );

        var act = async () => await runner.RunStatementsFromJsonAsync( JsonContextProd );
        await act.Should().NotThrowAsync(
            "case-sensitive: ActiveContext `Prod` does not match file context `prod`" );
    }

    // ---- ActiveContext null with file context block ----

    [TestMethod]
    public async Task ActiveContextNull_FileHasContext_PolicySkipIfUnset_SkipsSilently()
    {
        // SDK default — silent skip with INFO log.
        var options = new OpenSearchMigrationOptions
        {
            ActiveContext = null,
            ContextResolutionPolicy = ContextResolutionPolicy.SkipIfUnset
        };
        var runner = BuildRunner( options );

        var act = async () => await runner.RunStatementsFromJsonAsync( JsonContextProd );
        await act.Should().NotThrowAsync();
    }

    [TestMethod]
    public async Task ActiveContextNull_FileHasContext_PolicyRequireExplicit_Throws()
    {
        // Production default — throws with remediation naming the config key.
        var options = new OpenSearchMigrationOptions
        {
            ActiveContext = null,
            ContextResolutionPolicy = ContextResolutionPolicy.RequireExplicit
        };
        var runner = BuildRunner( options );

        var act = async () => await runner.RunStatementsFromJsonAsync( JsonContextProd );
        var ex = await act.Should().ThrowAsync<MissingActiveContextException>();
        ex.Which.Message.Should().Contain( "Migrations:ActiveContext" );
        ex.Which.Message.Should().Contain( "RequireExplicit" );
        ex.Which.Message.Should().Contain( "prod" );
    }

    [TestMethod]
    public async Task EmptyContextArray_TreatedAsNoFilter_AlwaysRuns()
    {
        // Degenerate `context: []` should not lock everyone out. Treat it
        // as if no context block were present.
        const string json = """
            { "context": [], "statements": [ { "statement": "REFRESH users" } ] }
            """;

        var options = new OpenSearchMigrationOptions
        {
            ActiveContext = null,
            ContextResolutionPolicy = ContextResolutionPolicy.RequireExplicit
        };
        var runner = BuildRunner( options );

        var act = async () => await runner.RunStatementsFromJsonAsync( json );

        // Empty context array is degenerate; the gate should pass through
        // and dispatch should be attempted (and fail against the substituted
        // client). Critically, MissingActiveContextException must NOT fire.
        try
        {
            await act();
            Assert.Fail( "expected dispatch failure on substituted client" );
        }
        catch ( MissingActiveContextException )
        {
            Assert.Fail( "empty context array is degenerate; RequireExplicit should NOT trip" );
        }
        catch
        {
            // expected — dispatch fails on the substituted client; that's
            // proof the gate passed through.
        }
    }

    // ---- Rollback path uses the same gate ----

    [TestMethod]
    public async Task RollbackPath_RespectsContextFilter()
    {
        // Mismatched context skips the rollback path too — symmetric with up.
        const string json = """
            {
              "context": ["prod"],
              "statements": [
                { "statement": "REFRESH users", "rollback": "REFRESH users" }
              ]
            }
            """;

        var options = new OpenSearchMigrationOptions { ActiveContext = "dev" };
        var runner = BuildRunner( options );

        var act = async () => await runner.RollbackStatementsFromJsonAsync( json, recordId: "rec-1" );
        await act.Should().NotThrowAsync(
            "rollback skips just like up when context doesn't match" );
    }
}
