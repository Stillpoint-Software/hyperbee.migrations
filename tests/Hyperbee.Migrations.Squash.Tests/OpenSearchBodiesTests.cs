using FluentAssertions;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Grammar;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 4 follow-up — OpenSearch BODIES { ... } header + inline brace-balanced
// `WITH BODY { ... }` body lift (per ADR-0022 + ADR-0017).

[TestClass]
public class OpenSearchBodiesTests
{
    // ----------------- BodiesHeaderExtractor -----------------

    [TestMethod]
    public void Header_ExtractsPathAndInlineEntries()
    {
        var script = """
            BODIES {
              users_template: @users_template.json
              logs_inline: { "settings": { "number_of_shards": 1 } }
            }

            CREATE TEMPLATE users WITH BODY $users_template;
            CREATE INDEX logs WITH BODY $logs_inline;
            """;

        var result = BodiesHeaderExtractor.Extract( script );

        result.Bodies.Should().HaveCount( 2 );

        var users = result.Bodies["users_template"];
        users.Should().BeOfType<BodiesHeaderExtractor.BodiesPathEntry>();
        ((BodiesHeaderExtractor.BodiesPathEntry) users).Path.Should().Be( "users_template.json" );

        var logs = result.Bodies["logs_inline"];
        logs.Should().BeOfType<BodiesHeaderExtractor.BodiesInlineEntry>();
        ((BodiesHeaderExtractor.BodiesInlineEntry) logs).Json.Should().Contain( "number_of_shards" );

        result.RemainingScript.Should().NotContain( "BODIES" );
        result.RemainingScript.Should().Contain( "CREATE TEMPLATE users WITH BODY $users_template" );
    }

    [TestMethod]
    public void Header_AbsentReturnsScriptUnchanged()
    {
        var script = "CREATE INDEX foo WITH BODY @foo.json;";
        var result = BodiesHeaderExtractor.Extract( script );

        result.Bodies.Should().BeEmpty();
        result.RemainingScript.Should().Be( script );
    }

    [TestMethod]
    public void Header_DuplicateEntryNameThrows()
    {
        var script = """
            BODIES {
              shared: @a.json
              shared: { "x": 1 }
            }
            """;
        var act = () => BodiesHeaderExtractor.Extract( script );
        act.Should().Throw<OpenSearchParseException>()
            .WithMessage( "*duplicate*shared*" );
    }

    [TestMethod]
    public void Header_UnbalancedJsonThrows()
    {
        var script = """
            BODIES {
              broken: { "x": 1
            }
            """;
        var act = () => BodiesHeaderExtractor.Extract( script );
        act.Should().Throw<OpenSearchParseException>().WithMessage( "*unbalanced*" );
    }

    // ----------------- BraceBalancedConsumer -----------------

    [TestMethod]
    public void BraceBalanced_NestedObjects()
    {
        var text = "{ \"a\": { \"b\": [1, 2, { \"c\": 3 }] } }TAIL";
        var end = BraceBalancedConsumer.ConsumeBalanced( text, 0, out var captured );

        end.Should().Be( text.IndexOf( "TAIL", StringComparison.Ordinal ) );
        captured.Should().StartWith( "{" );
        captured.Should().EndWith( "}" );
        captured.Should().Contain( "\"c\": 3" );
    }

    [TestMethod]
    public void BraceBalanced_RespectsStringLiteralWithEscapedBrace()
    {
        var text = "{ \"name\": \"has } brace inside\" }TAIL";
        var end = BraceBalancedConsumer.ConsumeBalanced( text, 0, out var captured );

        end.Should().Be( text.IndexOf( "TAIL", StringComparison.Ordinal ) );
        captured.Should().Contain( "has } brace inside" );
    }

    [TestMethod]
    public void BraceBalanced_UnbalancedReturnsStartIndex()
    {
        var text = "{ \"x\": 1 ";
        var end = BraceBalancedConsumer.ConsumeBalanced( text, 0, out var captured );

        end.Should().Be( 0 );
        captured.Should().BeEmpty();
    }

    // ----------------- InlineBodyExtractor -----------------

    [TestMethod]
    public void InlineExtractor_LiftsInlineBodyToSynthetic()
    {
        var script = """
            CREATE INDEX logs WITH BODY { "settings": { "number_of_shards": 1 } };
            REFRESH logs;
            """;

        var result = InlineBodyExtractor.Extract( script );

        result.SyntheticBodies.Should().HaveCount( 1 );
        var entry = result.SyntheticBodies.Values.Single();
        entry.Should().BeOfType<BodiesHeaderExtractor.BodiesInlineEntry>();
        ((BodiesHeaderExtractor.BodiesInlineEntry) entry).Json.Should().Contain( "number_of_shards" );

        result.RewrittenScript.Should().Contain( "WITH BODY $synthetic_inline_1" );
        result.RewrittenScript.Should().NotContain( "{ \"settings\"" );
    }

    [TestMethod]
    public void InlineExtractor_MultipleInlineBodies_GetSequentialNames()
    {
        var script = """
            CREATE INDEX a WITH BODY { "x": 1 };
            CREATE INDEX b WITH BODY { "y": 2 };
            CREATE INDEX c WITH BODY { "z": 3 };
            """;

        var result = InlineBodyExtractor.Extract( script );

        result.SyntheticBodies.Keys.Should().BeEquivalentTo( new[]
        {
            "synthetic_inline_1", "synthetic_inline_2", "synthetic_inline_3"
        } );
        result.RewrittenScript.Should().Contain( "WITH BODY $synthetic_inline_1" );
        result.RewrittenScript.Should().Contain( "WITH BODY $synthetic_inline_2" );
        result.RewrittenScript.Should().Contain( "WITH BODY $synthetic_inline_3" );
    }

    [TestMethod]
    public void InlineExtractor_PreservesNonInlineWithBody()
    {
        var script = """
            CREATE TEMPLATE t WITH BODY @template.json;
            CREATE INDEX i WITH BODY $named;
            """;

        var result = InlineBodyExtractor.Extract( script );
        result.SyntheticBodies.Should().BeEmpty();
        result.RewrittenScript.Should().Be( script, "no inline `WITH BODY {` shapes — script unchanged" );
    }

    [TestMethod]
    public void InlineExtractor_RespectsStringLiteralContext()
    {
        // A `WITH BODY {` inside a single-quoted string literal must NOT be
        // extracted. (Unlikely shape but the extractor's correctness depends
        // on this filter.)
        var script = "CREATE INDEX foo WITH BODY @real.json -- comment with WITH BODY { fake } in it\nREFRESH foo;";

        var result = InlineBodyExtractor.Extract( script );
        result.SyntheticBodies.Should().BeEmpty();
    }

    [TestMethod]
    public void InlineExtractor_NestedJsonInsideBody()
    {
        var script = """
            CREATE INDEX logs WITH BODY {
              "settings": { "number_of_shards": 3 },
              "mappings": {
                "properties": {
                  "level": { "type": "keyword" }
                }
              }
            };
            """;

        var result = InlineBodyExtractor.Extract( script );

        result.SyntheticBodies.Should().HaveCount( 1 );
        var entry = (BodiesHeaderExtractor.BodiesInlineEntry) result.SyntheticBodies.Values.Single();
        entry.Json.Should().Contain( "number_of_shards" );
        entry.Json.Should().Contain( "level" );
    }
}
