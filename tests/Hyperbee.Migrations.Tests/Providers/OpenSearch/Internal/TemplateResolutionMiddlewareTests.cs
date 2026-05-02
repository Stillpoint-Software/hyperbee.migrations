#nullable enable
using FluentAssertions;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Middleware;

namespace Hyperbee.Migrations.Tests.Providers.OpenSearch.Internal;

[TestClass]
public class TemplateResolutionMiddlewareTests
{
    // GET /_index_template/<name> response shape — these tests cover the pure
    // JSON-shape extraction. Live-cluster integration is tested separately.

    [TestMethod]
    public void ExtractTemplateBlock_StandardResponse_ReturnsInnerTemplate()
    {
        const string body = """
            {
              "index_templates": [
                {
                  "name": "users-template",
                  "index_template": {
                    "index_patterns": ["users-*"],
                    "template": {
                      "settings": { "number_of_shards": 2 },
                      "mappings": { "properties": { "id": { "type": "keyword" } } }
                    },
                    "priority": 100
                  }
                }
              ]
            }
            """;

        var template = TemplateResolutionMiddleware.ExtractTemplateBlock( body, "users-template" );

        template.Should().NotBeNull();
        template!["settings"]!["number_of_shards"]!.GetValue<int>().Should().Be( 2 );
        template["mappings"]!["properties"]!["id"]!["type"]!.GetValue<string>().Should().Be( "keyword" );
    }

    [TestMethod]
    public void ExtractTemplateBlock_TemplateWithoutInnerTemplateBlock_ReturnsNull()
    {
        // A template that only carries `index_patterns` + `composed_of` (e.g.,
        // pure component-template glue) has no `template` block — extraction
        // returns null and the caller (middleware) treats that as "no body".
        const string body = """
            {
              "index_templates": [
                {
                  "name": "logs-glue",
                  "index_template": {
                    "index_patterns": ["logs-*"],
                    "composed_of": ["common-mappings"]
                  }
                }
              ]
            }
            """;

        var template = TemplateResolutionMiddleware.ExtractTemplateBlock( body, "logs-glue" );
        template.Should().BeNull();
    }

    [TestMethod]
    public void ExtractTemplateBlock_EmptyArray_Throws()
    {
        const string body = """{ "index_templates": [] }""";

        var act = () => TemplateResolutionMiddleware.ExtractTemplateBlock( body, "missing" );
        act.Should().Throw<InvalidOperationException>()
            .WithMessage( "*not found*" );
    }

    [TestMethod]
    public void ExtractTemplateBlock_MissingIndexTemplatesKey_Throws()
    {
        const string body = """{ "wrong_shape": true }""";

        var act = () => TemplateResolutionMiddleware.ExtractTemplateBlock( body, "x" );
        act.Should().Throw<InvalidOperationException>()
            .WithMessage( "*not found*" );
    }

    [TestMethod]
    public void ExtractTemplateBlock_InvalidJson_Throws()
    {
        var act = () => TemplateResolutionMiddleware.ExtractTemplateBlock( "{not json", "x" );
        act.Should().Throw<InvalidOperationException>()
            .WithMessage( "*not valid JSON*" );
    }

    [TestMethod]
    public void ExtractTemplateBlock_EmptyBody_Throws()
    {
        var act = () => TemplateResolutionMiddleware.ExtractTemplateBlock( "", "x" );
        act.Should().Throw<InvalidOperationException>()
            .WithMessage( "*empty response*" );
    }

    // ---- Extract returns (body, hasComposedOf) (R-17 refinement) ----

    [TestMethod]
    public void Extract_TemplateWithoutComposedOf_HasComposedOfFalse()
    {
        const string body = """
            {
              "index_templates": [
                {
                  "name": "users-template",
                  "index_template": {
                    "index_patterns": ["users-*"],
                    "template": { "settings": { "number_of_shards": 2 } }
                  }
                }
              ]
            }
            """;

        var result = TemplateResolutionMiddleware.Extract( body, "users-template" );

        result.Body.Should().NotBeNull();
        result.HasComposedOf.Should().BeFalse();
    }

    [TestMethod]
    public void Extract_TemplateWithComposedOf_HasComposedOfTrue()
    {
        // R-17 refinement: templates that reference component templates need
        // to signal that to the dispatcher so dynamic:strict injection is
        // skipped — same semantics as the inline-body composed_of skip in
        // SafeDefaultMergeMiddleware, lifted to the runtime-resolved path.
        const string body = """
            {
              "index_templates": [
                {
                  "name": "logs-template",
                  "index_template": {
                    "index_patterns": ["logs-*"],
                    "composed_of": ["common-mappings", "logs-settings"],
                    "template": { "settings": { "number_of_shards": 1 } }
                  }
                }
              ]
            }
            """;

        var result = TemplateResolutionMiddleware.Extract( body, "logs-template" );

        result.Body.Should().NotBeNull();
        result.HasComposedOf.Should().BeTrue();
    }

    [TestMethod]
    public void Extract_TemplateWithEmptyComposedOfArray_HasComposedOfFalse()
    {
        // Treat empty composed_of as "no composition" — the user pinned the
        // shape but didn't attach components. Inject dynamic:strict normally.
        const string body = """
            {
              "index_templates": [
                {
                  "name": "empty",
                  "index_template": {
                    "index_patterns": ["x-*"],
                    "composed_of": [],
                    "template": { "settings": { "number_of_shards": 1 } }
                  }
                }
              ]
            }
            """;

        var result = TemplateResolutionMiddleware.Extract( body, "empty" );

        result.HasComposedOf.Should().BeFalse();
    }

    [TestMethod]
    public void Extract_PureComposedOfTemplate_BodyNullAndComposedOfTrue()
    {
        // A "glue" template that only carries composed_of with no inner
        // template block. Body is null, signal is true — the dispatcher must
        // tolerate a null body and still observe the composed_of flag.
        const string body = """
            {
              "index_templates": [
                {
                  "name": "glue",
                  "index_template": {
                    "index_patterns": ["logs-*"],
                    "composed_of": ["base"]
                  }
                }
              ]
            }
            """;

        var result = TemplateResolutionMiddleware.Extract( body, "glue" );

        result.Body.Should().BeNull();
        result.HasComposedOf.Should().BeTrue();
    }
}
