#nullable enable
using FluentAssertions;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Ast;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Grammar;

namespace Hyperbee.Migrations.Tests.Providers.OpenSearch.Internal;

// R-12 — NO WAIT("<reason>") modifier on mutating verbs.
//
// Grammar contract:
//   - Five mutating verbs accept the modifier: CREATE INDEX, REINDEX,
//     UPDATE SETTINGS, ALIAS SWAP, APPLY POLICY.
//   - Justification is required and must be non-empty (mirrors UNSAFE).
//   - Bare `NO WAIT` (no parentheses, no string) fails at parse time.
//   - The modifier appears at the END of the statement so it doesn't
//     conflict with WITH BODY / VIA ALIAS / etc.

[TestClass]
public class NoWaitParserTests
{
    private readonly OpenSearchStatementParser _parser = new();

    // ---- CREATE INDEX ----

    [TestMethod]
    public void CreateIndex_NoWaitWithReason_Parses()
    {
        var ast = (CreateIndexAst) _parser.Parse(
            "CREATE INDEX users NO WAIT(\"replicas allocate slowly on this cluster\")" );

        ast.NoWaitJustification.Should().Be( "replicas allocate slowly on this cluster" );
    }

    [TestMethod]
    public void CreateIndex_WithBodyAndNoWait_BothParse()
    {
        // NO WAIT comes AFTER WITH BODY in canonical form.
        var ast = (CreateIndexAst) _parser.Parse(
            "CREATE INDEX users IF NOT EXISTS WITH BODY $b NO WAIT(\"big mapping; manual wait via dashboards\")" );

        ast.IfNotExists.Should().BeTrue();
        ast.Body.Should().BeOfType<BodyRef>();
        ast.NoWaitJustification.Should().Contain( "big mapping" );
    }

    [TestMethod]
    public void CreateIndex_WithoutNoWait_NullJustification()
    {
        var ast = (CreateIndexAst) _parser.Parse( "CREATE INDEX users" );
        ast.NoWaitJustification.Should().BeNull();
    }

    // ---- REINDEX ----

    [TestMethod]
    public void Reindex_NoWaitAlongsideUnsafe_BothCaptured()
    {
        // The two modifiers are independent: UNSAFE opts out of the
        // op_type:create safe-default; NO WAIT opts out of the implicit
        // wait. Authors with reason to skip both can stack them.
        var ast = (ReindexAst) _parser.Parse(
            "REINDEX UNSAFE(\"empty dst seeded from a script\") FROM src TO dst NO WAIT(\"task-API polling done out-of-band\")" );

        ast.UnsafeJustification.Should().Be( "empty dst seeded from a script" );
        ast.NoWaitJustification.Should().Be( "task-API polling done out-of-band" );
        ast.InjectOpTypeCreate.Should().BeFalse(
            because: "UNSAFE flips op_type:create off" );
    }

    // ---- ALIAS SWAP ----

    [TestMethod]
    public void AliasSwap_NoWait_Parses()
    {
        var ast = (AliasSwapAst) _parser.Parse(
            "ALIAS SWAP `users-current` FROM `users-v1` TO `users-v2` NO WAIT(\"swap is atomic; no shard relocation expected\")" );

        ast.NoWaitJustification.Should().Contain( "atomic" );
    }

    // ---- UPDATE SETTINGS ----

    [TestMethod]
    public void UpdateSettings_NoWait_Parses()
    {
        var ast = (UpdateSettingsAst) _parser.Parse(
            "UPDATE SETTINGS ON users WITH BODY $s NO WAIT(\"refresh-interval bump only; no shard movement\")" );

        ast.NoWaitJustification.Should().Contain( "refresh-interval" );
    }

    // ---- APPLY POLICY ----

    [TestMethod]
    public void ApplyPolicy_NoWait_Parses()
    {
        var ast = (ApplyPolicyAst) _parser.Parse(
            "APPLY POLICY hot-warm-cold TO logs-* NO WAIT(\"policy attaches metadata only; no shard movement\")" );

        ast.NoWaitJustification.Should().Contain( "metadata" );
    }

    // ---- parse-time rejection (R-12 spec) ----
    //
    // EOF-anchoring on the top-level parser ensures bare / empty / whitespace-only
    // NO WAIT, and NO WAIT on non-mutating verbs (DROP INDEX), all fail at parse time.

    [TestMethod]
    public void CreateIndex_BareNoWait_NoParens_ParseFails()
    {
        var act = () => _parser.Parse( "CREATE INDEX users NO WAIT" );
        act.Should().Throw<OpenSearchParseException>();
    }

    [TestMethod]
    public void CreateIndex_NoWaitEmptyJustification_ParseFails()
    {
        var act = () => _parser.Parse( "CREATE INDEX users NO WAIT(\"\")" );
        act.Should().Throw<OpenSearchParseException>();
    }

    [TestMethod]
    public void CreateIndex_NoWaitWhitespaceJustification_ParseFails()
    {
        var act = () => _parser.Parse( "CREATE INDEX users NO WAIT(\"   \")" );
        act.Should().Throw<OpenSearchParseException>();
    }

    [TestMethod]
    public void DropIndex_NoWaitNotPermitted_ParseFails()
    {
        // DROP INDEX is non-mutating in the R-12 sense (no shard movement
        // to wait on); the modifier is reserved for the five mutating verbs.
        var act = () => _parser.Parse( "DROP INDEX users NO WAIT(\"nothing to wait on\")" );
        act.Should().Throw<OpenSearchParseException>();
    }
}
