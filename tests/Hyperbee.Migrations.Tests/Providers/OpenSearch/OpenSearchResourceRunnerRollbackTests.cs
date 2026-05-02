#nullable enable
using FluentAssertions;
using Hyperbee.Migrations;
using Hyperbee.Migrations.Providers.OpenSearch;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Bootstrap;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Dispatch;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Grammar;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Middleware;
using Hyperbee.Migrations.Providers.OpenSearch.Resources;
using Microsoft.Extensions.Logging.Abstractions;
using NSubstitute;
using OpenSearch.Client;

namespace Hyperbee.Migrations.Tests.Providers.OpenSearch;

// Pre-flight validation tests for the rollback path. These exercise the
// validation pass that runs BEFORE any statement is dispatched, so they
// don't require a live cluster — the failure surfaces from the JSON-shape
// check at the top of RollbackStatementsFromJsonAsync.
//
// End-to-end rollback semantics (full-rollback success, partial-rollback
// ledger write, ForceResume bypass) live in the integration tests against
// a real OpenSearch cluster — that's where R-19's correctness contract is
// actually load-bearing.

[TestClass]
public class OpenSearchResourceRunnerRollbackTests
{
    // No [Migration] attribute on purpose: RunnerTests scan the test assembly
    // for migrations that have the attribute, so an attributed nested class
    // would inflate that scan's count and break existing assertions. The
    // direct-JSON rollback path under test here does not require the
    // attribute (it doesn't call Migration.VersionedName).
    private sealed class FakeMigration : Migration
    {
        public override Task UpAsync( CancellationToken cancellationToken = default ) => Task.CompletedTask;
        public override Task DownAsync( CancellationToken cancellationToken = default ) => Task.CompletedTask;
    }

    private static OpenSearchResourceRunner<FakeMigration> BuildRunner()
    {
        var client = Substitute.For<IOpenSearchClient>();
        var options = new OpenSearchMigrationOptions();
        var dispatcher = new StatementDispatcher( new SafeDefaultMergeMiddleware() );
        var parser = new OpenSearchStatementParser();
        var recordStore = Substitute.For<IMigrationRecordStore>();
        return new OpenSearchResourceRunner<FakeMigration>(
            client, options, dispatcher, parser, TimeProvider.System,
            NullLogger<FakeMigration>.Instance, recordStore );
    }

    [TestMethod]
    public async Task RollbackFromJson_AllStatementsHaveRollback_PassesValidation_ThenAttemptsDispatch()
    {
        // Pre-flight passes when every statement carries a rollback. We don't
        // care about dispatch outcome here (the substituted client will fail
        // dispatch); we only assert the validation gate doesn't throw
        // RollbackNotSupportedException.
        const string json = """
            {
              "statements": [
                { "statement": "CREATE INDEX users", "rollback": "DROP INDEX users IF EXISTS" },
                { "statement": "CREATE INDEX orders", "rollback": "DROP INDEX orders IF EXISTS" }
              ]
            }
            """;

        var runner = BuildRunner();
        var act = async () => await runner.RollbackStatementsFromJsonAsync( json, recordId: "rec-1" );

        // Validation passes; dispatch fails against the no-op client. We
        // expect SOMETHING to throw (the substituted client returns nulls
        // and crashes) — but it must NOT be RollbackNotSupportedException
        // from the validation pass.
        try
        {
            await act();
            Assert.Fail( "expected the substituted client to fail dispatch" );
        }
        catch ( RollbackNotSupportedException )
        {
            Assert.Fail( "validation should have passed; rollback fields are present" );
        }
        catch
        {
            // expected — dispatch fails because the substituted client returns nulls
        }
    }

    [TestMethod]
    public async Task RollbackFromJson_FirstStatementMissingRollback_Throws_BeforeAnyDispatch()
    {
        // R-19: validation runs before any dispatch. A statement missing
        // rollback aborts the entire Down — we never start dispatching the
        // ones that DO have rollbacks, otherwise we'd leave the cluster
        // half-rolled-back.
        const string json = """
            {
              "statements": [
                { "statement": "CREATE INDEX users" },
                { "statement": "CREATE INDEX orders", "rollback": "DROP INDEX orders IF EXISTS" }
              ]
            }
            """;

        var runner = BuildRunner();
        var act = async () => await runner.RollbackStatementsFromJsonAsync( json, recordId: "rec-1" );

        var ex = await act.Should().ThrowAsync<RollbackNotSupportedException>();
        ex.Which.StatementIndex.Should().Be( 0 );
        ex.Which.Message.Should().Contain( "rollback" );
    }

    [TestMethod]
    public async Task RollbackFromJson_LastStatementMissingRollback_Throws_NoCascadingFailure()
    {
        // Validation walks the full list before dispatching. A missing
        // rollback at the END should still abort cleanly with the right
        // index in the exception.
        const string json = """
            {
              "statements": [
                { "statement": "CREATE INDEX users", "rollback": "DROP INDEX users IF EXISTS" },
                { "statement": "REINDEX FROM users TO users-v2" }
              ]
            }
            """;

        var runner = BuildRunner();
        var act = async () => await runner.RollbackStatementsFromJsonAsync( json, recordId: "rec-1" );

        var ex = await act.Should().ThrowAsync<RollbackNotSupportedException>();
        ex.Which.StatementIndex.Should().Be( 1 );
    }

    [TestMethod]
    public async Task RollbackFromJson_MissingStatementsArray_Throws()
    {
        const string json = """{ "wrong": "shape" }""";
        var runner = BuildRunner();
        var act = async () => await runner.RollbackStatementsFromJsonAsync( json, recordId: "rec-1" );

        await act.Should().ThrowAsync<InvalidOperationException>()
            .WithMessage( "*statements*" );
    }

    [TestMethod]
    public async Task RollbackFromJson_EmptyJson_Throws()
    {
        var runner = BuildRunner();
        var act = async () => await runner.RollbackStatementsFromJsonAsync( "", recordId: "rec-1" );
        await act.Should().ThrowAsync<Exception>();
    }

    [TestMethod]
    public void Status_Constants_MatchSchemaKeywords()
    {
        // The ledger schema declares these exact strings as keywords (R-06).
        // Pinning them here so they cannot drift from the index mapping
        // without a test failure.
        OpenSearchMigrationRecord.StatusSucceeded.Should().Be( "succeeded" );
        OpenSearchMigrationRecord.StatusFailed.Should().Be( "failed" );
        OpenSearchMigrationRecord.StatusPartiallyRolledBack.Should().Be( "partially_rolled_back" );
    }

    [TestMethod]
    public void RollbackNotSupportedException_CarriesStatementIndex()
    {
        var ex = new RollbackNotSupportedException( 7, "missing rollback at 7" );
        ex.StatementIndex.Should().Be( 7 );
        ex.Message.Should().Contain( "missing rollback" );
    }

    [TestMethod]
    public void OpenSearchPartialRollbackException_CarriesRecordIdAndIndex()
    {
        var ex = new OpenSearchPartialRollbackException( "rec-42", 3, "boom" );
        ex.RecordId.Should().Be( "rec-42" );
        ex.FailedStatementIndex.Should().Be( 3 );
    }
}
