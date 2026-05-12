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
}
