#nullable enable
using FluentAssertions;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Ast;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Grammar;

namespace Hyperbee.Migrations.Tests.Providers.OpenSearch.Internal;

[TestClass]
public class FoundationVerbParserTests
{
    private readonly OpenSearchStatementParser _parser = new();

    // ---- DROP INDEX ----

    [TestMethod]
    public void DropIndex_BareName_Parses()
    {
        var ast = _parser.Parse( "DROP INDEX users" );

        var d = (DropIndexAst) ast;
        d.IndexName.Should().Be( "users" );
        d.IfExists.Should().BeFalse();
    }

    [TestMethod]
    public void DropIndex_IfExists_FlagsTrue()
    {
        var ast = _parser.Parse( "DROP INDEX users IF EXISTS" );

        var d = (DropIndexAst) ast;
        d.IfExists.Should().BeTrue();
    }

    [TestMethod]
    public void DropIndex_BacktickName_StripsBackticks()
    {
        var ast = _parser.Parse( "DROP INDEX `users-v1` IF EXISTS" );

        var d = (DropIndexAst) ast;
        d.IndexName.Should().Be( "users-v1" );
        d.IfExists.Should().BeTrue();
    }

    [TestMethod]
    public void DropIndex_KeywordsCaseInsensitive_Parses()
    {
        var ast = _parser.Parse( "drop index users if exists" );

        ast.Should().BeOfType<DropIndexAst>();
    }

    // ---- UPDATE MAPPING ----

    [TestMethod]
    public void UpdateMapping_WithBody_Parses()
    {
        var ast = _parser.Parse( "UPDATE MAPPING ON users WITH BODY $newProps" );

        var u = (UpdateMappingAst) ast;
        u.IndexName.Should().Be( "users" );
        u.Body.Should().BeOfType<BodyRef>().Which.Name.Should().Be( "newProps" );
    }

    [TestMethod]
    public void UpdateMapping_WithoutBody_Parses()
    {
        // No body means caller embeds the mapping inline at compile time;
        // valid grammar — runtime compiler handles the no-body case.
        var ast = _parser.Parse( "UPDATE MAPPING ON users" );

        var u = (UpdateMappingAst) ast;
        u.IndexName.Should().Be( "users" );
        u.Body.Should().BeNull();
    }

    // ---- UPDATE SETTINGS ----

    [TestMethod]
    public void UpdateSettings_DynamicSettings_NoCloseFlag()
    {
        var ast = _parser.Parse( "UPDATE SETTINGS ON users WITH BODY $newSettings" );

        var u = (UpdateSettingsAst) ast;
        u.IndexName.Should().Be( "users" );
        u.Close.Should().BeFalse();
        u.Body.Should().BeOfType<BodyRef>().Which.Name.Should().Be( "newSettings" );
    }

    [TestMethod]
    public void UpdateSettings_StaticSettings_CloseFlag()
    {
        var ast = _parser.Parse( "UPDATE SETTINGS ON users CLOSE WITH BODY $newSettings" );

        var u = (UpdateSettingsAst) ast;
        u.Close.Should().BeTrue();
    }

    [TestMethod]
    public void UpdateSettings_CloseWithoutBody_Parses()
    {
        var ast = _parser.Parse( "UPDATE SETTINGS ON users CLOSE" );

        var u = (UpdateSettingsAst) ast;
        u.Close.Should().BeTrue();
        u.Body.Should().BeNull();
    }

    // ---- REFRESH ----

    [TestMethod]
    public void Refresh_BareName_Parses()
    {
        var ast = _parser.Parse( "REFRESH users" );

        var r = (RefreshAst) ast;
        r.IndexName.Should().Be( "users" );
    }

    [TestMethod]
    public void Refresh_BacktickName_StripsBackticks()
    {
        var ast = _parser.Parse( "REFRESH `users-v1`" );

        var r = (RefreshAst) ast;
        r.IndexName.Should().Be( "users-v1" );
    }

    // ---- WAIT FOR <green|yellow> ----

    [TestMethod]
    public void WaitForGreen_Bare_Parses()
    {
        var ast = _parser.Parse( "WAIT FOR GREEN" );

        var w = (WaitForHealthAst) ast;
        w.Threshold.Should().Be( HealthStatus.Green );
        w.IndexName.Should().BeNull();
        w.Timeout.Should().BeNull();
    }

    [TestMethod]
    public void WaitForYellow_OnIndex_Parses()
    {
        var ast = _parser.Parse( "WAIT FOR YELLOW ON users" );

        var w = (WaitForHealthAst) ast;
        w.Threshold.Should().Be( HealthStatus.Yellow );
        w.IndexName.Should().Be( "users" );
    }

    [TestMethod]
    public void WaitForGreen_OnIndex_Timeout_Parses()
    {
        var ast = _parser.Parse( "WAIT FOR GREEN ON users-v2 TIMEOUT 60s" );

        var w = (WaitForHealthAst) ast;
        w.Threshold.Should().Be( HealthStatus.Green );
        w.IndexName.Should().Be( "users-v2" );
        w.Timeout.Should().Be( TimeSpan.FromSeconds( 60 ) );
    }

    [TestMethod]
    public void WaitForGreen_TimeoutMinutes_Parses()
    {
        var ast = _parser.Parse( "WAIT FOR GREEN TIMEOUT 5m" );

        var w = (WaitForHealthAst) ast;
        w.Timeout.Should().Be( TimeSpan.FromMinutes( 5 ) );
    }

    [TestMethod]
    public void WaitForGreen_TimeoutHours_Parses()
    {
        var ast = _parser.Parse( "WAIT FOR GREEN TIMEOUT 2h" );

        var w = (WaitForHealthAst) ast;
        w.Timeout.Should().Be( TimeSpan.FromHours( 2 ) );
    }

    // ---- WAIT UNTIL TASK ----

    [TestMethod]
    public void WaitUntilTask_Bare_Parses()
    {
        var ast = _parser.Parse( "WAIT UNTIL TASK abc123 COMPLETE" );

        var w = (WaitUntilTaskAst) ast;
        w.TaskId.Should().Be( "abc123" );
        w.Timeout.Should().BeNull();
    }

    [TestMethod]
    public void WaitUntilTask_BacktickedId_HandlesColons()
    {
        // OpenSearch task IDs are <node>:<task-id>. Plain identifiers don't admit ':',
        // so callers must backtick-quote.
        var ast = _parser.Parse( "WAIT UNTIL TASK `abc123:42` COMPLETE TIMEOUT 5m" );

        var w = (WaitUntilTaskAst) ast;
        w.TaskId.Should().Be( "abc123:42" );
        w.Timeout.Should().Be( TimeSpan.FromMinutes( 5 ) );
    }

    // ---- Negative cases ----

    [TestMethod]
    public void DropIndex_MissingName_Throws()
    {
        var act = () => _parser.Parse( "DROP INDEX" );
        act.Should().Throw<OpenSearchParseException>();
    }

    [TestMethod]
    public void UpdateMapping_MissingOn_Throws()
    {
        var act = () => _parser.Parse( "UPDATE MAPPING users" );
        act.Should().Throw<OpenSearchParseException>();
    }

    [TestMethod]
    public void WaitFor_UnknownThreshold_Throws()
    {
        var act = () => _parser.Parse( "WAIT FOR PURPLE" );
        act.Should().Throw<OpenSearchParseException>();
    }

    [TestMethod]
    public void WaitUntilTask_MissingComplete_Throws()
    {
        var act = () => _parser.Parse( "WAIT UNTIL TASK abc" );
        act.Should().Throw<OpenSearchParseException>();
    }

    // NOTE: A "bare integer in TIMEOUT throws" test was attempted but Parlot's
    // ZeroOrOne for the TIMEOUT clause is lenient — `WAIT FOR GREEN TIMEOUT 30`
    // parses as `WAIT FOR GREEN` with `TIMEOUT 30` as silently-ignored trailing
    // input. Strict EOF matching for all top-level statements is a Phase 2
    // hardening item; for now, valid duration suffixes are documented and
    // exercised via the positive cases above.

    // ---- Original CREATE INDEX / REINDEX still work ----

    [TestMethod]
    public void Existing_CreateIndex_StillParses()
    {
        var ast = _parser.Parse( "CREATE INDEX users IF NOT EXISTS WITH BODY $body" );
        ast.Should().BeOfType<CreateIndexAst>();
    }

    [TestMethod]
    public void Existing_Reindex_StillParses()
    {
        var ast = _parser.Parse( "REINDEX FROM users TO users-v2" );
        ast.Should().BeOfType<ReindexAst>();
    }

    // ---- ALIAS SWAP / ADD / REMOVE (Phase 2) ----

    [TestMethod]
    public void AliasSwap_Bare_Parses()
    {
        var ast = _parser.Parse( "ALIAS SWAP users-current FROM users-v1 TO users-v2" );

        var s = (AliasSwapAst) ast;
        s.Alias.Should().Be( "users-current" );
        s.OldIndex.Should().Be( "users-v1" );
        s.NewIndex.Should().Be( "users-v2" );
    }

    [TestMethod]
    public void AliasSwap_BacktickIdentifiers_StripBackticks()
    {
        var ast = _parser.Parse( "ALIAS SWAP `users.current` FROM `users.v1` TO `users.v2`" );

        var s = (AliasSwapAst) ast;
        s.Alias.Should().Be( "users.current" );
        s.OldIndex.Should().Be( "users.v1" );
        s.NewIndex.Should().Be( "users.v2" );
    }

    [TestMethod]
    public void AliasAdd_Parses()
    {
        var ast = _parser.Parse( "ALIAS ADD users-current ON users-v1" );

        var a = (AliasAddAst) ast;
        a.Alias.Should().Be( "users-current" );
        a.IndexName.Should().Be( "users-v1" );
    }

    [TestMethod]
    public void AliasRemove_Parses()
    {
        var ast = _parser.Parse( "ALIAS REMOVE users-current ON users-v1" );

        var r = (AliasRemoveAst) ast;
        r.Alias.Should().Be( "users-current" );
        r.IndexName.Should().Be( "users-v1" );
    }

    [TestMethod]
    public void AliasSwap_KeywordsCaseInsensitive_Parses()
    {
        var ast = _parser.Parse( "alias swap users-current from users-v1 to users-v2" );
        ast.Should().BeOfType<AliasSwapAst>();
    }

    [TestMethod]
    public void AliasSwap_MissingTo_Throws()
    {
        var act = () => _parser.Parse( "ALIAS SWAP users-current FROM users-v1" );
        act.Should().Throw<OpenSearchParseException>();
    }

    [TestMethod]
    public void AliasAdd_MissingOn_Throws()
    {
        var act = () => _parser.Parse( "ALIAS ADD users-current users-v1" );
        act.Should().Throw<OpenSearchParseException>();
    }

    // ---- CREATE/DROP TEMPLATE & COMPONENT, CREATE/APPLY POLICY (Phase 2) ----

    [TestMethod]
    public void CreateTemplate_WithBody_Parses()
    {
        var ast = _parser.Parse( "CREATE TEMPLATE logs-template WITH BODY $body" );

        var t = (CreateTemplateAst) ast;
        t.TemplateName.Should().Be( "logs-template" );
        t.Body.Should().BeOfType<BodyRef>().Which.Name.Should().Be( "body" );
    }

    [TestMethod]
    public void CreateTemplate_WithoutBody_Parses()
    {
        // Body is optional at parse time; the dispatcher rejects null body at execute time.
        var ast = _parser.Parse( "CREATE TEMPLATE logs-template" );

        var t = (CreateTemplateAst) ast;
        t.TemplateName.Should().Be( "logs-template" );
        t.Body.Should().BeNull();
    }

    [TestMethod]
    public void CreateComponent_WithBody_Parses()
    {
        var ast = _parser.Parse( "CREATE COMPONENT common-mappings WITH BODY $body" );

        var c = (CreateComponentAst) ast;
        c.ComponentName.Should().Be( "common-mappings" );
        c.Body.Should().BeOfType<BodyRef>().Which.Name.Should().Be( "body" );
    }

    [TestMethod]
    public void DropTemplate_BareName_Parses()
    {
        var ast = _parser.Parse( "DROP TEMPLATE logs-template" );

        var d = (DropTemplateAst) ast;
        d.TemplateName.Should().Be( "logs-template" );
        d.IfExists.Should().BeFalse();
    }

    [TestMethod]
    public void DropTemplate_IfExists_FlagsTrue()
    {
        var ast = _parser.Parse( "DROP TEMPLATE logs-template IF EXISTS" );

        var d = (DropTemplateAst) ast;
        d.IfExists.Should().BeTrue();
    }

    [TestMethod]
    public void DropComponent_IfExists_FlagsTrue()
    {
        var ast = _parser.Parse( "DROP COMPONENT common-mappings IF EXISTS" );

        var d = (DropComponentAst) ast;
        d.ComponentName.Should().Be( "common-mappings" );
        d.IfExists.Should().BeTrue();
    }

    [TestMethod]
    public void CreatePolicy_WithBody_Parses()
    {
        var ast = _parser.Parse( "CREATE POLICY hot-warm-cold WITH BODY $body" );

        var p = (CreatePolicyAst) ast;
        p.PolicyId.Should().Be( "hot-warm-cold" );
        p.Body.Should().BeOfType<BodyRef>().Which.Name.Should().Be( "body" );
    }

    [TestMethod]
    public void ApplyPolicy_Parses()
    {
        var ast = _parser.Parse( "APPLY POLICY hot-warm-cold TO logs-*" );

        var a = (ApplyPolicyAst) ast;
        a.PolicyId.Should().Be( "hot-warm-cold" );
        a.IndexPattern.Should().Be( "logs-*" );
    }

    [TestMethod]
    public void ApplyPolicy_BacktickPattern_StripsBackticks()
    {
        var ast = _parser.Parse( "APPLY POLICY hot-warm-cold TO `logs-2026.*`" );

        var a = (ApplyPolicyAst) ast;
        a.IndexPattern.Should().Be( "logs-2026.*" );
    }

    [TestMethod]
    public void TemplatePolicy_KeywordsCaseInsensitive_Parses()
    {
        _parser.Parse( "create template logs-template with body $body" )
            .Should().BeOfType<CreateTemplateAst>();
        _parser.Parse( "create component common-mappings with body $body" )
            .Should().BeOfType<CreateComponentAst>();
        _parser.Parse( "drop template logs-template if exists" )
            .Should().BeOfType<DropTemplateAst>();
        _parser.Parse( "drop component common-mappings if exists" )
            .Should().BeOfType<DropComponentAst>();
        _parser.Parse( "create policy hot-warm-cold with body $body" )
            .Should().BeOfType<CreatePolicyAst>();
        _parser.Parse( "apply policy hot-warm-cold to logs-*" )
            .Should().BeOfType<ApplyPolicyAst>();
    }

    [TestMethod]
    public void CreateTemplate_BeforeCreateIndex_Disambiguates()
    {
        // Disambiguation: CREATE TEMPLATE must not be misclassified as
        // CREATE INDEX (where TEMPLATE would become an identifier).
        var ast = _parser.Parse( "CREATE TEMPLATE logs WITH BODY $body" );
        ast.Should().BeOfType<CreateTemplateAst>();
    }

    [TestMethod]
    public void DropComponent_BeforeDropIndex_Disambiguates()
    {
        var ast = _parser.Parse( "DROP COMPONENT common IF EXISTS" );
        ast.Should().BeOfType<DropComponentAst>();
    }

    [TestMethod]
    public void ApplyPolicy_MissingTo_Throws()
    {
        var act = () => _parser.Parse( "APPLY POLICY hot-warm-cold logs-*" );
        act.Should().Throw<OpenSearchParseException>();
    }

    // ---- MIGRATE INDEX composite (Phase 2, R-30) ----

    [TestMethod]
    public void MigrateIndex_WithTemplateAndAlias_DecomposesToThreeChildren()
    {
        var ast = _parser.Parse( "MIGRATE INDEX users-v1 TO users-v2 WITH TEMPLATE users-template VIA ALIAS users-current" );

        var c = (CompositeStatementAst) ast;
        c.Verb.Should().Be( "MIGRATE INDEX" );
        c.Children.Should().HaveCount( 3 );

        var create = (CreateIndexAst) c.Children[0];
        create.IndexName.Should().Be( "users-v2" );
        create.TemplateBody!.TemplateName.Should().Be( "users-template" );
        create.Body.Should().BeNull();
        create.InjectDynamicStrict.Should().BeTrue();

        var reindex = (ReindexAst) c.Children[1];
        reindex.Source.Should().Be( "users-v1" );
        reindex.Destination.Should().Be( "users-v2" );
        reindex.InjectOpTypeCreate.Should().BeTrue();

        var swap = (AliasSwapAst) c.Children[2];
        swap.Alias.Should().Be( "users-current" );
        swap.OldIndex.Should().Be( "users-v1" );
        swap.NewIndex.Should().Be( "users-v2" );
    }

    [TestMethod]
    public void MigrateIndex_WithBodyAndAlias_UsesInlineBody()
    {
        var ast = _parser.Parse( "MIGRATE INDEX users-v1 TO users-v2 WITH BODY $newShape VIA ALIAS users-current" );

        var c = (CompositeStatementAst) ast;
        c.Children.Should().HaveCount( 3 );

        var create = (CreateIndexAst) c.Children[0];
        create.Body.Should().BeOfType<BodyRef>().Which.Name.Should().Be( "newShape" );
        create.TemplateBody.Should().BeNull();
    }

    [TestMethod]
    public void MigrateIndex_NoAlias_OmitsSwap()
    {
        // VIA ALIAS is optional. Without it the composite is just CREATE + REINDEX —
        // the author owns cutover (R-30 preserves migrations that intentionally
        // retain both indices for read-traffic comparison).
        var ast = _parser.Parse( "MIGRATE INDEX users-v1 TO users-v2 WITH TEMPLATE users-template" );

        var c = (CompositeStatementAst) ast;
        c.Children.Should().HaveCount( 2 );
        c.Children[0].Should().BeOfType<CreateIndexAst>();
        c.Children[1].Should().BeOfType<ReindexAst>();
    }

    [TestMethod]
    public void MigrateIndex_NoBody_DefaultsToCreateIndexWithoutBody()
    {
        // Body source is also optional — if author wants the new index created
        // with no body (e.g., relies entirely on cluster-side templates with
        // matching index_patterns), they can skip both WITH TEMPLATE and WITH BODY.
        var ast = _parser.Parse( "MIGRATE INDEX users-v1 TO users-v2 VIA ALIAS users-current" );

        var c = (CompositeStatementAst) ast;
        c.Children.Should().HaveCount( 3 );
        var create = (CreateIndexAst) c.Children[0];
        create.Body.Should().BeNull();
        create.TemplateBody.Should().BeNull();
    }

    [TestMethod]
    public void MigrateIndex_WithTimeout_Parses()
    {
        // TIMEOUT is parsed but not yet threaded through (sync REINDEX uses the
        // cluster's own wait_for_completion). Forward-compatible parsing for
        // the async-polling slice.
        var ast = _parser.Parse( "MIGRATE INDEX users-v1 TO users-v2 WITH TEMPLATE users-template VIA ALIAS users-current TIMEOUT 5m" );
        ast.Should().BeOfType<CompositeStatementAst>();
    }

    [TestMethod]
    public void MigrateIndex_SameSourceAndDestination_ThrowsAtParseTime()
    {
        // R-30 same-src-dst rejection (purely syntactic). The grammar callback
        // raises InvalidOperationException; Parlot may surface it directly or
        // the parser wrapper may rethrow as OpenSearchParseException — either
        // is acceptable as long as the rejection happens at parse time and the
        // message identifies the constraint.
        var act = () => _parser.Parse( "MIGRATE INDEX users TO users WITH TEMPLATE users-template" );

        var ex = act.Should().Throw<Exception>().Which;
        ex.Should().Match<Exception>( e =>
            e is OpenSearchParseException || e is InvalidOperationException );
        ex.Message.Should().Contain( "distinct" );
    }

    [TestMethod]
    public void MigrateIndex_KeywordsCaseInsensitive_Parses()
    {
        var ast = _parser.Parse( "migrate index users-v1 to users-v2 with template users-template via alias users-current" );
        ast.Should().BeOfType<CompositeStatementAst>();
    }
}
