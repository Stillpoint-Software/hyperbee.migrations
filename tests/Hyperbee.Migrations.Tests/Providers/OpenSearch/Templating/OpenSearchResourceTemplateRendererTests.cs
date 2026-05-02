using System.Text.Json;
using Hyperbee.Migrations.Providers.OpenSearch.Templating;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hyperbee.Migrations.Tests.Providers.OpenSearch.Templating;

[TestClass]
public class OpenSearchResourceTemplateRendererTests
{
    [TestMethod]
    public void Render_simple_substitution_resolves_scope_prefixed_variable()
    {
        // arrange
        var renderer = new OpenSearchResourceTemplateRenderer(
            env: new Dictionary<string, string>(),
            config: new Dictionary<string, string> { ["foo"] = "bar" },
            runtime: new Dictionary<string, string>(),
            secrets: new Dictionary<string, SecretValue>() );

        // act
        var result = renderer.Render( "{{config.foo}}" );

        // assert
        Assert.AreEqual( "bar", result );
    }

    [TestMethod]
    public void Render_conditional_inside_json_emits_well_formed_json()
    {
        // arrange
        var renderer = new OpenSearchResourceTemplateRenderer(
            env: new Dictionary<string, string>(),
            config: new Dictionary<string, string> { ["enabled"] = "true" },
            runtime: new Dictionary<string, string>(),
            secrets: new Dictionary<string, SecretValue>() );

        // Hyperbee.Templating uses `{{if ...}}` (no leading `#`); the README
        // showing `{{#if}}` is misleading vs the 3.4.1 engine surface.
        const string template = "{ \"x\": {{if config.enabled}}1{{else}}0{{/if}} }";

        // act
        var result = renderer.Render( template );

        // assert
        Assert.AreEqual( "{ \"x\": 1 }", result );

        using var doc = JsonDocument.Parse( result );
        Assert.AreEqual( 1, doc.RootElement.GetProperty( "x" ).GetInt32() );
    }

    [TestMethod]
    public void Render_iteration_inside_json_produces_well_formed_json_array()
    {
        // arrange
        // The runtime scope holds a CSV-encoded collection. Hyperbee.Templating
        // 3.4.1 does not yet expose the index variant `each n,i:...` documented
        // in source comments, so we emulate first-element detection with an
        // inline define token (`seen:1`) flipped after each iteration.
        var renderer = new OpenSearchResourceTemplateRenderer(
            env: new Dictionary<string, string>(),
            config: new Dictionary<string, string>(),
            runtime: new Dictionary<string, string> { ["nodes"] = "alpha,beta,gamma" },
            secrets: new Dictionary<string, SecretValue>() );

        // The fat-arrow expression uses the explicit indexer form because dotted
        // scope keys (`runtime.nodes`) aren't valid C# member access in the
        // engine's expression rewriter. `{{if seen}}...{{/if}}` emits the
        // separating comma only after the first iteration.
        const string template =
            "{ \"nodes\": [{{each n:x => x[\"runtime.nodes\"].Split(\",\")}}" +
            "{{if seen}},{{/if}}\"{{n}}\"{{seen:1}}" +
            "{{/each}}] }";

        // act
        var result = renderer.Render( template );

        // assert
        using var doc = JsonDocument.Parse( result );
        var nodes = doc.RootElement.GetProperty( "nodes" );
        Assert.AreEqual( JsonValueKind.Array, nodes.ValueKind );
        Assert.AreEqual( 3, nodes.GetArrayLength() );
        Assert.AreEqual( "alpha", nodes[0].GetString() );
        Assert.AreEqual( "beta", nodes[1].GetString() );
        Assert.AreEqual( "gamma", nodes[2].GetString() );
    }
}
