#nullable enable
using System.Text.Json.Nodes;
using FluentAssertions;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Ast;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Middleware;

namespace Hyperbee.Migrations.Tests.Providers.OpenSearch.Internal;

[TestClass]
public class SafeDefaultMergeMiddlewareTests
{
    private readonly SafeDefaultMergeMiddleware _middleware = new();

    private static JsonNode? Parse( string json )
        => JsonNode.Parse( json );

    private static CreateIndexAst Create( string name = "users", BodyRef? body = null, bool inject = true )
        => new( name, IfNotExists: false, Body: body, InjectDynamicStrict: inject );

    private static ReindexAst Reindex( string src = "users", string dst = "users-v2", BodyRef? body = null, bool inject = true, string? unsafeReason = null )
        => new( src, dst, body, InjectOpTypeCreate: inject, UnsafeJustification: unsafeReason );

    // ---- CREATE INDEX merge cases ----

    [TestMethod]
    public void CreateIndex_NoBody_InjectsMappingsDynamicStrict()
    {
        var merged = _middleware.Merge( Create(), body: null );

        var dynamicValue = merged["mappings"]!["dynamic"]!.GetValue<string>();
        dynamicValue.Should().Be( "strict" );
    }

    [TestMethod]
    public void CreateIndex_FlatBodyWithoutMappings_InjectsMappingsDynamicStrict()
    {
        var body = Parse( """
            { "settings": { "number_of_shards": 2 } }
            """ );

        var merged = _middleware.Merge( Create(), body );

        merged["settings"]!["number_of_shards"]!.GetValue<int>().Should().Be( 2 );
        merged["mappings"]!["dynamic"]!.GetValue<string>().Should().Be( "strict" );
    }

    [TestMethod]
    public void CreateIndex_BodyWithMappingsPropertiesOnly_PreservesPropertiesAndAddsDynamicStrict()
    {
        var body = Parse( """
            { "mappings": { "properties": { "id": { "type": "keyword" } } } }
            """ );

        var merged = _middleware.Merge( Create(), body );

        merged["mappings"]!["properties"]!["id"]!["type"]!.GetValue<string>().Should().Be( "keyword" );
        merged["mappings"]!["dynamic"]!.GetValue<string>().Should().Be( "strict" );
    }

    [TestMethod]
    public void CreateIndex_BodyWithExplicitDynamicTrue_PreservesUserValue()
    {
        var body = Parse( """
            { "mappings": { "dynamic": "true", "properties": { "id": { "type": "keyword" } } } }
            """ );

        var merged = _middleware.Merge( Create(), body );

        merged["mappings"]!["dynamic"]!.GetValue<string>().Should().Be( "true" );
    }

    [TestMethod]
    public void CreateIndex_BodyWithComposedOf_SkipsInjection()
    {
        var body = Parse( """
            { "composed_of": ["users-component"], "settings": { "number_of_shards": 1 } }
            """ );

        var merged = _middleware.Merge( Create(), body );

        merged.ContainsKey( "mappings" ).Should().BeFalse();
        merged["composed_of"].Should().NotBeNull();
    }

    [TestMethod]
    public void CreateIndex_InjectionDisabled_PassesBodyThrough()
    {
        var body = Parse( """
            { "settings": { "number_of_shards": 2 } }
            """ );

        var merged = _middleware.Merge( Create( inject: false ), body );

        merged.ContainsKey( "mappings" ).Should().BeFalse();
    }

    [TestMethod]
    public void CreateIndex_OriginalBody_NotMutated()
    {
        var original = Parse( """
            { "settings": { "number_of_shards": 2 } }
            """ );
        var originalCopy = original!.ToJsonString();

        _middleware.Merge( Create(), original );

        // Caller's tree is untouched
        original.ToJsonString().Should().Be( originalCopy );
    }

    // ---- REINDEX merge cases ----

    [TestMethod]
    public void Reindex_NoBody_BuildsFullPayloadWithOpTypeCreate()
    {
        var merged = _middleware.Merge( Reindex(), body: null );

        merged["source"]!["index"]!.GetValue<string>().Should().Be( "users" );
        merged["dest"]!["index"]!.GetValue<string>().Should().Be( "users-v2" );
        merged["dest"]!["op_type"]!.GetValue<string>().Should().Be( "create" );
    }

    [TestMethod]
    public void Reindex_BodyWithDestObject_AddsOpTypeCreate()
    {
        var body = Parse( """
            { "source": { "index": "users", "query": { "match_all": {} } }, "dest": { "index": "users-v2" } }
            """ );

        var merged = _middleware.Merge( Reindex(), body );

        merged["dest"]!["op_type"]!.GetValue<string>().Should().Be( "create" );
        merged["source"]!["query"].Should().NotBeNull(); // user fields preserved
    }

    [TestMethod]
    public void Reindex_BodyWithExplicitOpTypeCreate_Idempotent()
    {
        var body = Parse( """
            { "source": { "index": "users" }, "dest": { "index": "users-v2", "op_type": "create" } }
            """ );

        var merged = _middleware.Merge( Reindex(), body );

        merged["dest"]!["op_type"]!.GetValue<string>().Should().Be( "create" );
    }

    [TestMethod]
    public void Reindex_BodyWithConflictingOpTypeIndex_ThrowsSafeDefaultConflict()
    {
        var body = Parse( """
            { "source": { "index": "users" }, "dest": { "index": "users-v2", "op_type": "index" } }
            """ );

        var act = () => _middleware.Merge( Reindex(), body );

        act.Should().Throw<SafeDefaultConflictException>()
            .WithMessage( "*op_type: \"index\"*UNSAFE*" );
    }

    [TestMethod]
    public void Reindex_UnsafeBranch_PassesConflictingOpTypeThrough()
    {
        var body = Parse( """
            { "source": { "index": "users" }, "dest": { "index": "users-v2", "op_type": "index" } }
            """ );

        // UNSAFE branch — author opt-out, no enforcement
        var merged = _middleware.Merge(
            Reindex( inject: false, unsafeReason: "OPS-1234" ),
            body );

        merged["dest"]!["op_type"]!.GetValue<string>().Should().Be( "index" );
    }
}
