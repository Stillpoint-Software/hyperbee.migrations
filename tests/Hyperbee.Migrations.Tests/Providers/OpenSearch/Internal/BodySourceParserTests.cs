#nullable enable
using FluentAssertions;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Ast;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Grammar;

namespace Hyperbee.Migrations.Tests.Providers.OpenSearch.Internal;

// ADR-0017 — body-source grammar. Three resolution forms; this test class
// covers the parser surface (`$name` vs `@path`) and the parse-time rejection
// of absolute paths and `..` traversal. Resolution-side semantics (the
// `bodies` section, sibling fallback, file loading) live in the resource-
// runner integration tests.

[TestClass]
public class BodySourceParserTests
{
    private readonly OpenSearchStatementParser _parser = new();

    // ---- $name form (sibling/structured) ----

    [TestMethod]
    public void DollarName_ProducesBodyRef()
    {
        var ast = (CreateIndexAst) _parser.Parse( "CREATE INDEX users WITH BODY $usersIndex" );
        ast.Body.Should().BeOfType<BodyRef>().Which.Name.Should().Be( "usersIndex" );
    }

    [TestMethod]
    public void DollarName_HyphenatedAndDotted_ProducesBodyRef()
    {
        var ast = (CreateIndexAst) _parser.Parse( "CREATE INDEX users WITH BODY $users-index.v1" );
        ast.Body.Should().BeOfType<BodyRef>().Which.Name.Should().Be( "users-index.v1" );
    }

    // ---- @path form (direct file reference) ----

    [TestMethod]
    public void AtPath_ProducesBodyFileRef()
    {
        var ast = (CreateIndexAst) _parser.Parse( "CREATE INDEX users WITH BODY @bodies/users-mapping.json" );
        ast.Body.Should().BeOfType<BodyFileRef>().Which.Path.Should().Be( "bodies/users-mapping.json" );
    }

    [TestMethod]
    public void AtPath_NestedDirectories_ProducesBodyFileRef()
    {
        var ast = (CreateIndexAst) _parser.Parse( "CREATE INDEX users WITH BODY @bodies/sub/dir/users.json" );
        ast.Body.Should().BeOfType<BodyFileRef>().Which.Path.Should().Be( "bodies/sub/dir/users.json" );
    }

    [TestMethod]
    public void AtPath_BackslashSeparators_AcceptedAndPreservedForRuntimeNormalize()
    {
        // The runtime normalizes `\` to `/` before resource lookup, but the
        // parser accepts both since path-string conventions vary by author OS.
        var ast = (CreateIndexAst) _parser.Parse( @"CREATE INDEX users WITH BODY @bodies\users.json" );
        ast.Body.Should().BeOfType<BodyFileRef>().Which.Path.Should().Be( @"bodies\users.json" );
    }

    [TestMethod]
    public void AtPath_OnReindex_ProducesBodyFileRef()
    {
        // Body-source forms are uniform across all body-bearing verbs.
        var ast = (ReindexAst) _parser.Parse( "REINDEX FROM src TO dst WITH BODY @bodies/script.json" );
        ast.Body.Should().BeOfType<BodyFileRef>().Which.Path.Should().Be( "bodies/script.json" );
    }

    [TestMethod]
    public void AtPath_OnUpdateMapping_ProducesBodyFileRef()
    {
        var ast = (UpdateMappingAst) _parser.Parse( "UPDATE MAPPING ON users WITH BODY @bodies/mapping.json" );
        ast.Body.Should().BeOfType<BodyFileRef>().Which.Path.Should().Be( "bodies/mapping.json" );
    }

    [TestMethod]
    public void AtPath_OnCreateTemplate_ProducesBodyFileRef()
    {
        var ast = (CreateTemplateAst) _parser.Parse( "CREATE TEMPLATE my-tpl WITH BODY @bodies/tpl.json" );
        ast.Body.Should().BeOfType<BodyFileRef>().Which.Path.Should().Be( "bodies/tpl.json" );
    }

    // ---- parse-time rejection of unsafe paths ----

    [TestMethod]
    public void AtPath_AbsoluteUnix_RejectedAtParseTime()
    {
        var act = () => _parser.Parse( "CREATE INDEX users WITH BODY @/etc/passwd" );
        act.Should().Throw<Exception>()
            .Where( e => e.Message.Contains( "absolute" ) || e.Message.Contains( "relative" ) );
    }

    [TestMethod]
    public void AtPath_AbsoluteWindows_RejectedAtParseTime()
    {
        var act = () => _parser.Parse( @"CREATE INDEX users WITH BODY @\bodies\users.json" );
        act.Should().Throw<Exception>()
            .Where( e => e.Message.Contains( "absolute" ) || e.Message.Contains( "relative" ) );
    }

    // Drive-letter prefix rejection (cross-platform asymmetry guard).
    // `Path.IsPathRooted` is platform-dependent — `C:/foo` reads as rooted on
    // Windows but not on Linux — so an author editing on one host could
    // produce a manifest that's silently rooted on another. The validator
    // checks the rooted shape explicitly.
    [TestMethod]
    [DataRow( "C:/foo/bar.json" )]
    [DataRow( @"C:\foo\bar.json" )]
    [DataRow( "c:/foo/bar.json" )]
    [DataRow( @"Z:\bar.json" )]
    [DataRow( "a:/x.json" )]
    public void AtPath_DriveLetterPrefix_RejectedAtParseTime( string path )
    {
        var act = () => _parser.Parse( $"CREATE INDEX users WITH BODY @{path}" );
        act.Should().Throw<Exception>()
            .Where( e => e.Message.Contains( "absolute" ) || e.Message.Contains( "drive-letter" ) );
    }

    // Colon anywhere in a body path is rejected — embedded resource names
    // never use it, and accepting it would muddy the drive-letter check.
    [TestMethod]
    public void AtPath_ColonInPath_RejectedAtParseTime()
    {
        var act = () => _parser.Parse( "CREATE INDEX users WITH BODY @bodies/foo:bar.json" );
        act.Should().Throw<Exception>()
            .Where( e => e.Message.Contains( ":" ) );
    }

    [TestMethod]
    public void AtPath_ParentTraversal_RejectedAtParseTime()
    {
        // R-... — body files must stay within the migration's resource folder.
        // Reject `..` segments so authors can't escape the embedded-resource
        // namespace at runtime.
        var act = () => _parser.Parse( "CREATE INDEX users WITH BODY @bodies/../../secrets.json" );
        act.Should().Throw<Exception>()
            .Where( e => e.Message.Contains( ".." ) || e.Message.Contains( "traverse" ) );
    }

    [TestMethod]
    public void AtPath_LeadingParentTraversal_RejectedAtParseTime()
    {
        var act = () => _parser.Parse( "CREATE INDEX users WITH BODY @../shared/users.json" );
        act.Should().Throw<Exception>()
            .Where( e => e.Message.Contains( ".." ) || e.Message.Contains( "traverse" ) );
    }

    [TestMethod]
    public void AtPath_FilenameWithDots_NotMistakenForParentTraversal()
    {
        // `users.v2.json` has dots but no `..` segment. The validator splits
        // on `/` first then checks each segment, so this should pass.
        var ast = (CreateIndexAst) _parser.Parse( "CREATE INDEX users WITH BODY @bodies/users.v2.json" );
        ast.Body.Should().BeOfType<BodyFileRef>().Which.Path.Should().Be( "bodies/users.v2.json" );
    }

    // ---- mutual exclusion at the grammar level ----

    [TestMethod]
    public void EitherFormButNotBoth_AtSyntaxLevel()
    {
        // The grammar's body-ref alternative is OneOf($name | @path) — there
        // is no syntactic way to combine them in a single WITH BODY clause.
        // (Mixed inline + file references in one statement live in the
        //  `bodies` section, resolved at runtime, not in the statement string.)
        var dollarAst = _parser.Parse( "CREATE INDEX users WITH BODY $body" );
        var atAst = _parser.Parse( "CREATE INDEX users WITH BODY @body.json" );

        ((CreateIndexAst) dollarAst).Body.Should().BeOfType<BodyRef>();
        ((CreateIndexAst) atAst).Body.Should().BeOfType<BodyFileRef>();
    }
}
