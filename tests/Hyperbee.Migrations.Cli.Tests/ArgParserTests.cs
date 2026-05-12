using FluentAssertions;
using Hyperbee.Migrations.Cli;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hyperbee.Migrations.Cli.Tests;

// R-P9 smoke coverage for the CLI's argument parser. The CLI surface ships
// in v3.0 as the operator entry-point for `squash` + `recover` verbs;
// these tests pin the parser contract so a future refactor doesn't break
// operator invocations.

[TestClass]
public class ArgParserTests
{
    [TestMethod]
    public void Parse_SpaceSeparatedValue_ResolvesViaRequired()
    {
        var p = ArgParser.Parse( new[] { "--provider", "postgres" } );
        p.Required( "provider" ).Should().Be( "postgres" );
    }

    [TestMethod]
    public void Parse_EqualsSeparatedValue_ResolvesViaRequired()
    {
        var p = ArgParser.Parse( new[] { "--provider=postgres" } );
        p.Required( "provider" ).Should().Be( "postgres" );
    }

    [TestMethod]
    public void Parse_BareFlagWithoutValue_BecomesTrue()
    {
        var p = ArgParser.Parse( new[] { "--dry-run" } );
        p.HasFlag( "dry-run" ).Should().BeTrue();
    }

    [TestMethod]
    public void Parse_FlagFollowedByOption_IsBoolean()
    {
        // --dry-run --connection postgres://... should parse --dry-run as
        // a bool flag, not consume --connection as its value.
        var p = ArgParser.Parse( new[] { "--dry-run", "--connection", "postgres://x" } );
        p.HasFlag( "dry-run" ).Should().BeTrue();
        p.Required( "connection" ).Should().Be( "postgres://x" );
    }

    [TestMethod]
    public void Parse_RepeatedOption_AccumulatesInMany()
    {
        var p = ArgParser.Parse( new[] { "--env", "dev", "--env", "prod" } );
        p.Many( "env" ).Should().BeEquivalentTo( new[] { "dev", "prod" } );
        p.Optional( "env" ).Should().Be( "prod", "Optional returns the last value" );
    }

    [TestMethod]
    public void Parse_PositionalArgs_CapturedSeparately()
    {
        var p = ArgParser.Parse( new[] { "from-mid-range", "--env", "dev" } );
        p.Positional.Should().BeEquivalentTo( new[] { "from-mid-range" } );
        p.Required( "env" ).Should().Be( "dev" );
    }

    [TestMethod]
    public void Required_Missing_Throws()
    {
        var p = ArgParser.Parse( Array.Empty<string>() );
        Action act = () => p.Required( "provider" );
        act.Should().Throw<ArgumentException>().WithMessage( "*--provider*required*" );
    }

    [TestMethod]
    public void Optional_Missing_ReturnsFallback()
    {
        var p = ArgParser.Parse( Array.Empty<string>() );
        p.Optional( "provider", "default" ).Should().Be( "default" );
    }

    [TestMethod]
    public void Optional_Missing_NullByDefault()
    {
        var p = ArgParser.Parse( Array.Empty<string>() );
        p.Optional( "provider" ).Should().BeNull();
    }

    [TestMethod]
    public void OptionLookup_IsCaseInsensitive()
    {
        var p = ArgParser.Parse( new[] { "--Provider", "postgres" } );
        p.Required( "provider" ).Should().Be( "postgres" );
        p.Required( "PROVIDER" ).Should().Be( "postgres" );
    }

    // ---- ParseRange ---------------------------------------------------

    [TestMethod]
    public void ParseRange_HappyPath()
    {
        var (from, to) = ArgParser.ParseRange( "1000-2000" );
        from.Should().Be( 1000 );
        to.Should().Be( 2000 );
    }

    [TestMethod]
    public void ParseRange_Empty_Throws()
    {
        Action act = () => ArgParser.ParseRange( "" );
        act.Should().Throw<ArgumentException>().WithMessage( "*--range*required*" );
    }

    [TestMethod]
    public void ParseRange_MissingDash_Throws()
    {
        Action act = () => ArgParser.ParseRange( "1000" );
        act.Should().Throw<ArgumentException>().WithMessage( "*format*" );
    }

    [TestMethod]
    public void ParseRange_NonInteger_Throws()
    {
        Action act = () => ArgParser.ParseRange( "abc-2000" );
        act.Should().Throw<ArgumentException>().WithMessage( "*endpoints must be integers*" );
    }

    [TestMethod]
    public void ParseRange_DescendingRange_Throws()
    {
        Action act = () => ArgParser.ParseRange( "2000-1000" );
        act.Should().Throw<ArgumentException>().WithMessage( "*end (1000) is less than start (2000)*" );
    }

    [TestMethod]
    public void ParseRange_EqualEndpoints_Allowed()
    {
        // Single-version range is a valid (degenerate) squash range.
        var (from, to) = ArgParser.ParseRange( "1000-1000" );
        from.Should().Be( 1000 );
        to.Should().Be( 1000 );
    }

    // ---- R-12: schema whitelist + did-you-mean ------------------------

    private static readonly ArgSchema Schema = ArgSchema.Of(
        knownFlags: new[] { "provider", "connection", "range", "output", "remove-originals", "regenerate" },
        booleanFlags: new[] { "remove-originals", "regenerate" } );

    [TestMethod]
    public void SchemaParse_KnownFlags_AreAccepted()
    {
        var p = ArgParser.Parse( new[] { "--provider", "postgres", "--connection", "postgres://x" }, Schema );
        p.Required( "provider" ).Should().Be( "postgres" );
        p.Required( "connection" ).Should().Be( "postgres://x" );
    }

    [TestMethod]
    public void SchemaParse_UnknownFlag_ThrowsWithDidYouMean()
    {
        // R-12: --conneciton (typo of --connection) must reject with a
        // did-you-mean suggestion against the closest known flag.
        Action act = () => ArgParser.Parse(
            new[] { "--conneciton", "x" }, Schema );

        act.Should().Throw<ArgumentException>()
            .WithMessage( "*unknown flag --conneciton*" )
            .WithMessage( "*Did you mean --connection?*" );
    }

    [TestMethod]
    public void SchemaParse_UnknownFlag_ListsKnownFlags()
    {
        Action act = () => ArgParser.Parse(
            new[] { "--totally-unrelated", "x" }, Schema );

        act.Should().Throw<ArgumentException>()
            .WithMessage( "*unknown flag --totally-unrelated*" )
            .WithMessage( "*Known flags: *--provider*" );
    }

    [TestMethod]
    public void SchemaParse_NonBooleanFlagFollowedByFlag_Throws()
    {
        // R-12: previously `--connection --range 1-2` silently parsed
        // connection="true". Now it must error.
        Action act = () => ArgParser.Parse(
            new[] { "--connection", "--range", "1-2" }, Schema );

        act.Should().Throw<ArgumentException>()
            .WithMessage( "*--connection requires a value*" );
    }

    [TestMethod]
    public void SchemaParse_NonBooleanFlagAtEnd_Throws()
    {
        Action act = () => ArgParser.Parse(
            new[] { "--connection" }, Schema );

        act.Should().Throw<ArgumentException>()
            .WithMessage( "*--connection requires a value*" );
    }

    [TestMethod]
    public void SchemaParse_BooleanFlagFollowedByFlag_IsTrue()
    {
        // Boolean flags retain their value-less form.
        var p = ArgParser.Parse(
            new[] { "--remove-originals", "--connection", "x" }, Schema );

        p.HasFlag( "remove-originals" ).Should().BeTrue();
        p.Required( "connection" ).Should().Be( "x" );
    }

    [TestMethod]
    public void SchemaParse_BooleanFlagDoesNotConsumePositional()
    {
        // `--remove-originals positional --connection x` -> positional captured,
        // --remove-originals stays boolean.
        var p = ArgParser.Parse(
            new[] { "--remove-originals", "leftover", "--connection", "x" }, Schema );

        p.HasFlag( "remove-originals" ).Should().BeTrue();
        p.Positional.Should().BeEquivalentTo( new[] { "leftover" } );
        p.Required( "connection" ).Should().Be( "x" );
    }

    [TestMethod]
    public void SchemaParse_InlineEqualsValue_AcceptedForKnownFlag()
    {
        var p = ArgParser.Parse(
            new[] { "--provider=postgres", "--connection=postgres://x" }, Schema );
        p.Required( "provider" ).Should().Be( "postgres" );
        p.Required( "connection" ).Should().Be( "postgres://x" );
    }

    [TestMethod]
    public void SchemaParse_UnrecognizedFlag_NoCloseMatch_OmitsDidYouMean()
    {
        // When no candidate is within the edit-distance threshold, the
        // error message lists the known flags but omits the "did you mean"
        // suggestion so we don't propose an unrelated flag.
        Action act = () => ArgParser.Parse(
            new[] { "--xyz", "v" }, Schema );

        act.Should().Throw<ArgumentException>()
            .WithMessage( "*unknown flag --xyz*" )
            .Where( e => !e.Message.Contains( "Did you mean", StringComparison.OrdinalIgnoreCase ) );
    }
}
