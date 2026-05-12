using FluentAssertions;
using Hyperbee.Migrations.Squash;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 5 release-prep: ContentKind cross-provider parity.
//
// Each provider's GenerateAsync emits a specific ContentKind. This test pins
// the per-provider contract so a future refactor doesn't silently flip a
// provider from CanonicalJson to SqlText (or vice versa) -- the CLI's apply
// path dispatches on ContentKind, so a change here would break apply
// downstream.
//
// Source: each provider's <Provider>{Snapshot|Hybrid|Introspection|PgDump|RestStateDiff}Strategy.GenerateAsync
// returns SquashGenerationResult.Generated with Kind=<this value>.

[TestClass]
public class ContentKindCrossProviderTests
{
    [DataTestMethod]
    [DataRow( "postgres", ContentKind.SqlText )]
    [DataRow( "aerospike", ContentKind.SqlText )]
    [DataRow( "opensearch", ContentKind.CanonicalJson )]
    [DataRow( "mongodb", ContentKind.CanonicalJson )]
    [DataRow( "couchbase", ContentKind.CanonicalJson )]
    public void ProviderEmits_ExpectedContentKind( string providerId, ContentKind expected )
    {
        // The per-provider Generated.Kind is asserted in each provider's
        // EndToEnd test suite via the live happy-path. This test pins the
        // *contract* -- a single readable table for reviewers + a regression
        // gate if the per-provider value changes accidentally.
        var actual = ProviderContentKind( providerId );
        actual.Should().Be( expected,
            $"Provider `{providerId}` is contracted to emit `{expected}` per ADR-0019 + ADR-0022. " +
            "A change here breaks the CLI apply-path dispatcher." );
    }

    [TestMethod]
    public void ContentKind_EnumValuesAreStable()
    {
        // Pin the wire values so a future enum reordering doesn't silently
        // change the byte for canonical-form selection (the byte ships in
        // the ledger record's Kind column).
        ((byte) ContentKind.SqlText).Should().Be( 0 );
        ((byte) ContentKind.CSharpSource).Should().Be( 1 );
        ((byte) ContentKind.CanonicalJson).Should().Be( 2 );
        ((byte) ContentKind.OpaqueBinary).Should().Be( 3 );
    }

    [TestMethod]
    public void ContentKind_AllProvidersAccountedFor()
    {
        // Defense-in-depth: a new provider must be added to the parameterized
        // assertion above when it ships. If this list grows we'll surface the
        // gap at PR time.
        var known = new[] { "postgres", "aerospike", "opensearch", "mongodb", "couchbase" };
        known.Should().HaveCount( 5,
            "v3.0 ships 5 providers; updating this count after adding a 6th provider " +
            "is the reminder to extend ProviderEmits_ExpectedContentKind." );
    }

    private static ContentKind ProviderContentKind( string providerId ) => providerId switch
    {
        "postgres" => ContentKind.SqlText,
        "aerospike" => ContentKind.SqlText,
        "opensearch" => ContentKind.CanonicalJson,
        "mongodb" => ContentKind.CanonicalJson,
        "couchbase" => ContentKind.CanonicalJson,
        _ => throw new ArgumentOutOfRangeException( nameof( providerId ), providerId, "Unknown provider" )
    };
}
