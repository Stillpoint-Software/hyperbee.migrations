using FluentAssertions;
using Hyperbee.Migrations.Providers.OpenSearch.Squash;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 2 Task 2.2: OpenSearchDataOpClassifier unit coverage.
//
// Exercises both input shapes:
//   - statement form: CREATE/DROP family + APPLY POLICY + UPDATE MAPPING +
//     ALIAS + REFRESH + WAIT (structural); REINDEX FROM + MIGRATE INDEX
//     (data ops)
//   - call-site form: receiver-anchored _?client.<verb>(:
//     Index/Update/Delete/Bulk/Reindex (data); Get/Search/Count/Exists
//     (read); Indices/Cluster/Ingest/Cat sub-clients (structural)
//
// Plus the non-determinism scan + default-deny behavior.

[TestClass]
public class OpenSearchDataOpClassifierTests
{
    private static readonly OpenSearchDataOpClassifier Classifier = new();

    // ---- statement-form: structural ----------------------------------------

    [TestMethod]
    public void Statement_CreateIndex_IsStructural()
    {
        var c = Classifier.Classify( "CREATE INDEX users WITH BODY @body.json" );
        c.IsDataOp.Should().BeFalse();
        c.IsUnclassified.Should().BeFalse();
    }

    [TestMethod]
    public void Statement_DropIndex_IsStructural()
    {
        var c = Classifier.Classify( "DROP INDEX users" );
        c.IsDataOp.Should().BeFalse();
        c.IsUnclassified.Should().BeFalse();
    }

    [TestMethod]
    public void Statement_CreateTemplate_IsStructural()
    {
        var c = Classifier.Classify( "CREATE TEMPLATE my-template WITH BODY @t.json" );
        c.IsDataOp.Should().BeFalse();
    }

    [TestMethod]
    public void Statement_CreateComponent_IsStructural()
    {
        var c = Classifier.Classify( "CREATE COMPONENT my-component WITH BODY @c.json" );
        c.IsDataOp.Should().BeFalse();
    }

    [TestMethod]
    public void Statement_CreatePolicy_IsStructural()
    {
        var c = Classifier.Classify( "CREATE POLICY hot-warm-delete WITH BODY @policy.json" );
        c.IsDataOp.Should().BeFalse();
    }

    [TestMethod]
    public void Statement_ApplyPolicy_IsStructural()
    {
        var c = Classifier.Classify( "APPLY POLICY hot-warm-delete TO logs-*" );
        c.IsDataOp.Should().BeFalse();
    }

    [TestMethod]
    public void Statement_UpdateMapping_IsStructural()
    {
        var c = Classifier.Classify( "UPDATE MAPPING users WITH BODY @mapping.json" );
        c.IsDataOp.Should().BeFalse();
    }

    [TestMethod]
    public void Statement_UpdateSettings_IsStructural()
    {
        var c = Classifier.Classify( "UPDATE SETTINGS users WITH BODY @settings.json" );
        c.IsDataOp.Should().BeFalse();
    }

    [TestMethod]
    public void Statement_AliasSwap_IsStructural()
    {
        var c = Classifier.Classify( "ALIAS SWAP write_alias FROM users_v1 TO users_v2" );
        c.IsDataOp.Should().BeFalse();
    }

    [TestMethod]
    public void Statement_AliasAdd_IsStructural()
    {
        var c = Classifier.Classify( "ALIAS ADD users_v2 TO read_alias" );
        c.IsDataOp.Should().BeFalse();
    }

    [TestMethod]
    public void Statement_Refresh_IsStructural()
    {
        var c = Classifier.Classify( "REFRESH users" );
        c.IsDataOp.Should().BeFalse();
        c.IsUnclassified.Should().BeFalse();
    }

    [TestMethod]
    public void Statement_WaitForHealth_IsStructural()
    {
        var c = Classifier.Classify( "WAIT FOR HEALTH GREEN" );
        c.IsDataOp.Should().BeFalse();
    }

    [TestMethod]
    public void Statement_WaitUntilTask_IsStructural()
    {
        var c = Classifier.Classify( "WAIT UNTIL TASK 'task-id-123'" );
        c.IsDataOp.Should().BeFalse();
    }

    // ---- statement-form: data ops ------------------------------------------

    [TestMethod]
    public void Statement_ReindexFrom_IsDataOp()
    {
        var c = Classifier.Classify( "REINDEX FROM users_v1 TO users_v2" );
        c.IsDataOp.Should().BeTrue();
        c.RequiresPreservation.Should().BeTrue();
    }

    [TestMethod]
    public void Statement_MigrateIndex_IsDataOp()
    {
        var c = Classifier.Classify( "MIGRATE INDEX users_v1 TO users_v2 USING settings @s.json USING mapping @m.json" );
        c.IsDataOp.Should().BeTrue();
        c.RequiresPreservation.Should().BeTrue();
    }

    // ---- call-site form: data ops ------------------------------------------

    [TestMethod]
    public void CallSite_IndexAsync_IsDataOp()
    {
        var c = Classifier.Classify( "await _client.IndexAsync(doc, idx => idx.Index(\"users\"))" );
        c.IsDataOp.Should().BeTrue();
        c.RequiresPreservation.Should().BeTrue();
    }

    [TestMethod]
    public void CallSite_IndexDocument_IsDataOp()
    {
        var c = Classifier.Classify( "_client.IndexDocument(doc)" );
        c.IsDataOp.Should().BeTrue();
    }

    [TestMethod]
    public void CallSite_UpdateAsync_IsDataOp()
    {
        var c = Classifier.Classify( "await _client.UpdateAsync<MyDoc>(id, u => u.Doc(...))" );
        c.IsDataOp.Should().BeTrue();
    }

    [TestMethod]
    public void CallSite_UpdateByQueryAsync_IsDataOp()
    {
        var c = Classifier.Classify( "await _client.UpdateByQueryAsync<MyDoc>(...)" );
        c.IsDataOp.Should().BeTrue();
    }

    [TestMethod]
    public void CallSite_DeleteAsync_IsDataOp()
    {
        var c = Classifier.Classify( "await _client.DeleteAsync<MyDoc>(id)" );
        c.IsDataOp.Should().BeTrue();
    }

    [TestMethod]
    public void CallSite_DeleteByQueryAsync_IsDataOp()
    {
        var c = Classifier.Classify( "await _client.DeleteByQueryAsync<MyDoc>(...)" );
        c.IsDataOp.Should().BeTrue();
    }

    [TestMethod]
    public void CallSite_BulkAsync_IsDataOp()
    {
        var c = Classifier.Classify( "await _client.BulkAsync(b => b.IndexMany(docs))" );
        c.IsDataOp.Should().BeTrue();
    }

    [TestMethod]
    public void CallSite_ReindexAsync_IsDataOp()
    {
        var c = Classifier.Classify( "await _client.ReindexAsync<MyDoc>(r => r.From(\"users_v1\").To(\"users_v2\"))" );
        c.IsDataOp.Should().BeTrue();
    }

    [TestMethod]
    public void CallSite_ReindexOnServerAsync_IsDataOp()
    {
        var c = Classifier.Classify( "await _client.ReindexOnServerAsync(r => r.Source(...).Destination(...))" );
        c.IsDataOp.Should().BeTrue();
    }

    // ---- call-site form: reads ---------------------------------------------

    [TestMethod]
    public void CallSite_GetAsync_NotDataOp()
    {
        var c = Classifier.Classify( "var doc = await _client.GetAsync<MyDoc>(id)" );
        c.IsDataOp.Should().BeFalse();
        c.IsUnclassified.Should().BeFalse();
    }

    [TestMethod]
    public void CallSite_SearchAsync_NotDataOp()
    {
        var c = Classifier.Classify( "var results = await _client.SearchAsync<MyDoc>(s => s.Query(...))" );
        c.IsDataOp.Should().BeFalse();
        c.IsUnclassified.Should().BeFalse();
    }

    [TestMethod]
    public void CallSite_CountAsync_NotDataOp()
    {
        var c = Classifier.Classify( "var n = await _client.CountAsync<MyDoc>(...)" );
        c.IsDataOp.Should().BeFalse();
    }

    [TestMethod]
    public void CallSite_ExistsAsync_NotDataOp()
    {
        var c = Classifier.Classify( "var exists = await _client.ExistsAsync<MyDoc>(id)" );
        c.IsDataOp.Should().BeFalse();
    }

    [TestMethod]
    public void CallSite_IndexExistsAsync_NotDataOp()
    {
        // IndexExists is a HEAD probe (read), not a write. Verify the
        // verb-list explicitly enumerates it -- otherwise the prefix
        // match against "Index" would false-positive.
        var c = Classifier.Classify( "var exists = await _client.IndexExistsAsync(\"users\")" );
        c.IsDataOp.Should().BeFalse();
        c.IsUnclassified.Should().BeFalse();
    }

    // ---- call-site form: structural via sub-clients ------------------------

    [TestMethod]
    public void CallSite_IndicesCreate_IsStructural()
    {
        // _client.Indices.CreateAsync(...) is structural. Receiver path
        // `_client.Indices` differs from `_client.Index` (the data-op verb).
        // The sub-client pattern matches before the data-op write pattern
        // can false-positive on `Index` prefix.
        var c = Classifier.Classify( "await _client.Indices.CreateAsync(\"users\", i => i.Map(...))" );
        c.IsDataOp.Should().BeFalse();
        c.IsUnclassified.Should().BeFalse();
    }

    [TestMethod]
    public void CallSite_IndicesDelete_IsStructural()
    {
        var c = Classifier.Classify( "await _client.Indices.DeleteAsync(\"users\")" );
        c.IsDataOp.Should().BeFalse();
        c.IsUnclassified.Should().BeFalse();
    }

    [TestMethod]
    public void CallSite_IndicesPutMapping_IsStructural()
    {
        var c = Classifier.Classify( "await _client.Indices.PutMappingAsync(...)" );
        c.IsDataOp.Should().BeFalse();
    }

    [TestMethod]
    public void CallSite_IndicesPutTemplate_IsStructural()
    {
        var c = Classifier.Classify( "await _client.Indices.PutTemplateV2Async(...)" );
        c.IsDataOp.Should().BeFalse();
    }

    [TestMethod]
    public void CallSite_ClusterPutComponentTemplate_IsStructural()
    {
        var c = Classifier.Classify( "await _client.Cluster.PutComponentTemplateAsync(...)" );
        c.IsDataOp.Should().BeFalse();
    }

    [TestMethod]
    public void CallSite_IngestPutPipeline_IsStructural()
    {
        var c = Classifier.Classify( "await _client.Ingest.PutPipelineAsync(\"my-pipeline\", ...)" );
        c.IsDataOp.Should().BeFalse();
    }

    [TestMethod]
    public void CallSite_CatPlugins_IsStructural()
    {
        var c = Classifier.Classify( "await _client.Cat.PluginsAsync()" );
        c.IsDataOp.Should().BeFalse();
        c.IsUnclassified.Should().BeFalse();
    }

    [TestMethod]
    public void CallSite_TasksList_IsStructural()
    {
        var c = Classifier.Classify( "await _client.Tasks.ListAsync()" );
        c.IsDataOp.Should().BeFalse();
    }

    // ---- default-deny ------------------------------------------------------

    [TestMethod]
    public void UnknownVerb_DefaultDeny()
    {
        var c = Classifier.Classify( "EXPLAIN ANALYZE foo" );
        c.IsUnclassified.Should().BeTrue();
        c.RequiresAnnotation.Should().BeTrue();
    }

    [TestMethod]
    public void EmptyInput_DefaultDeny()
    {
        var c = Classifier.Classify( "" );
        c.IsUnclassified.Should().BeTrue();
        c.RequiresAnnotation.Should().BeTrue();
    }

    // ---- non-determinism ---------------------------------------------------

    [TestMethod]
    public void NonDeterminism_DateTimeUtcNow_Flagged()
    {
        var c = Classifier.Classify( "await _client.IndexAsync(new { ts = DateTime.UtcNow })" );
        c.IsDataOp.Should().BeTrue();
        c.EmissionHint.Should().Contain( "DateTime.UtcNow" );
        c.RequiresAnnotation.Should().BeTrue();
    }

    [TestMethod]
    public void NonDeterminism_GuidNewGuid_Flagged()
    {
        var c = Classifier.Classify( "await _client.IndexAsync(new { id = Guid.NewGuid() })" );
        c.EmissionHint.Should().Contain( "Guid.NewGuid" );
    }

    [TestMethod]
    public void NonDeterminism_NewRandomWithoutSeed_Flagged()
    {
        var c = Classifier.Classify( "var r = new Random(); await _client.IndexAsync(new { v = r.Next() })" );
        c.EmissionHint.Should().Contain( "new Random" );
    }

    [TestMethod]
    public void NonDeterminism_NewRandomWithSeed_NotFlagged()
    {
        var c = Classifier.Classify( "var r = new Random(42); await _client.IndexAsync(new { v = r.Next() })" );
        c.EmissionHint.Should().BeNull();
        c.RequiresAnnotation.Should().BeFalse();
    }

    [TestMethod]
    public void NonDeterminism_RandomShared_Flagged()
    {
        var c = Classifier.Classify( "await _client.IndexAsync(new { v = Random.Shared.Next() })" );
        c.EmissionHint.Should().Contain( "Random.Shared" );
    }

    [TestMethod]
    public void NonDeterminism_OnStructural_StillFlagged()
    {
        // Non-determinism inside a structural call-site (e.g., a template
        // name computed at runtime) is also flagged via EmissionHint
        // regardless of the data-op verdict.
        var c = Classifier.Classify(
            "await _client.Indices.CreateAsync($\"users_{DateTime.UtcNow:yyyyMMdd}\", i => i.Map(...))" );
        c.IsDataOp.Should().BeFalse();
        c.EmissionHint.Should().Contain( "DateTime.UtcNow" );
    }

    [TestMethod]
    public void ScanNonDeterminism_MultipleHits_DedupedAndSorted()
    {
        var hits = OpenSearchDataOpClassifier.ScanNonDeterminism(
            "DateTime.UtcNow; Guid.NewGuid(); DateTime.UtcNow; Random.Shared.Next()" );

        hits.Should().Equal( "DateTime.UtcNow", "Guid.NewGuid", "Random.Shared" );
    }

    [TestMethod]
    public void ScanNonDeterminism_NullOrEmpty_ReturnsEmpty()
    {
        OpenSearchDataOpClassifier.ScanNonDeterminism( null ).Should().BeEmpty();
        OpenSearchDataOpClassifier.ScanNonDeterminism( "" ).Should().BeEmpty();
    }
}
