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
}
