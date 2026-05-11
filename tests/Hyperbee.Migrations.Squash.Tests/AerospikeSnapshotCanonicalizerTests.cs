using FluentAssertions;
using Hyperbee.Migrations.Providers.Aerospike.Squash;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 1 (R-P1, R-P5, R-P8): AerospikeSnapshotCanonicalizer unit coverage.
//
// Exercises:
//   - section parsing ([sets] / [sindex] headers, content extraction)
//   - info-entry parsing (semicolon-separated key=value bags)
//   - ephemeral-field stripping (objects, memory_used, keys, etc.)
//   - deterministic sort + emit (sets by ns+set, indexes by ns+indexname)
//   - idempotence: Canonicalize(Canonicalize(x)) == Canonicalize(x)
//   - statement-form input round-trip via AerospikeStatementClassifier
//   - EmitScript = Canonicalize identity
//
// The C12 determinism gate (R-P5) integration test that re-runs full codegen
// and asserts byte-equal output lives in the Phase 1 integration suite.

[TestClass]
public class AerospikeSnapshotCanonicalizerTests
{
    private static readonly AerospikeSnapshotCanonicalizer Canonicalizer = new();

    [TestMethod]
    public void ProviderId_IsAerospike()
    {
        Canonicalizer.ProviderId.Should().Be( "aerospike" );
    }

    [TestMethod]
    public void Canonicalize_BasicSnapshot_EmitsCreateSetAndCreateIndex()
    {
        const string snapshot = """
            # aerospike-snapshot v1
            # namespace: test

            [sets]
            ns=test:set=users:objects=1234:tombstones=0:memory_used=5012345;ns=test:set=orders:objects=42

            [sindex]
            ns=test:indexname=idx_email:set=users:bin=email:type=STRING:state=RW:keys=1234;ns=test:indexname=idx_age:set=users:bin=age:type=NUMERIC
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        canon.Should().Contain( "CREATE SET test.orders;" );
        canon.Should().Contain( "CREATE SET test.users;" );
        canon.Should().Contain( "CREATE INDEX WAIT idx_age ON test.users(age) NUMERIC;" );
        canon.Should().Contain( "CREATE INDEX WAIT idx_email ON test.users(email) STRING;" );

        // ephemeral fields must not appear in the canonical output
        canon.Should().NotContain( "objects=" );
        canon.Should().NotContain( "tombstones=" );
        canon.Should().NotContain( "memory_used=" );
        canon.Should().NotContain( "state=" );
        canon.Should().NotContain( "keys=" );
    }

    [TestMethod]
    public void Canonicalize_SortsSetsByNamespaceAndName()
    {
        const string snapshot = """
            [sets]
            ns=test:set=zebras;ns=test:set=apples;ns=other:set=zebras
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        var apples = canon.IndexOf( "CREATE SET test.apples", StringComparison.Ordinal );
        var zebras = canon.IndexOf( "CREATE SET test.zebras", StringComparison.Ordinal );
        var otherZebras = canon.IndexOf( "CREATE SET other.zebras", StringComparison.Ordinal );

        apples.Should().BeGreaterThan( -1 );
        zebras.Should().BeGreaterThan( -1 );
        otherZebras.Should().BeGreaterThan( -1 );

        // Ordinal sort by (ns, set): other.zebras < test.apples < test.zebras
        otherZebras.Should().BeLessThan( apples );
        apples.Should().BeLessThan( zebras );
    }

    [TestMethod]
    public void Canonicalize_SortsIndexesByNamespaceAndName()
    {
        const string snapshot = """
            [sindex]
            ns=test:indexname=z_idx:set=users:bin=z:type=STRING;ns=test:indexname=a_idx:set=users:bin=a:type=STRING;ns=other:indexname=m_idx:set=u:bin=m:type=NUMERIC
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        var aIdx = canon.IndexOf( "WAIT a_idx ", StringComparison.Ordinal );
        var zIdx = canon.IndexOf( "WAIT z_idx ", StringComparison.Ordinal );
        var mIdx = canon.IndexOf( "WAIT m_idx ", StringComparison.Ordinal );

        mIdx.Should().BeGreaterThan( -1 ).And.BeLessThan( aIdx );
        aIdx.Should().BeLessThan( zIdx );
    }

    [TestMethod]
    public void Canonicalize_IsIdempotent()
    {
        const string snapshot = """
            [sets]
            ns=test:set=users:objects=42;ns=test:set=orders

            [sindex]
            ns=test:indexname=idx_a:set=users:bin=name:type=STRING:keys=10
            """;

        var first = Canonicalizer.Canonicalize( snapshot );
        var second = Canonicalizer.Canonicalize( first );

        second.Should().Be( first, "Canonicalize must be idempotent for the C12 determinism gate" );
    }

    [TestMethod]
    public void Canonicalize_NormalizesLineEndings()
    {
        var crlfSnapshot = "[sets]\r\nns=test:set=users\r\n\r\n[sindex]\r\nns=test:indexname=idx:set=users:bin=name:type=STRING\r\n";

        var canon = Canonicalizer.Canonicalize( crlfSnapshot );

        canon.Should().NotContain( "\r" );
        canon.Should().Contain( "CREATE SET test.users;\n" );
    }

    [TestMethod]
    public void Canonicalize_StatementFormInput_NormalizesAndSorts()
    {
        // No section headers -- treat as already-canonical statement form.
        const string already = """
            CREATE INDEX WAIT z_idx ON test.users(z) STRING;
            CREATE INDEX WAIT a_idx ON test.users(a) STRING;
            CREATE SET test.zebras;
            CREATE SET test.apples;
            """;

        var canon = Canonicalizer.Canonicalize( already );

        var apples = canon.IndexOf( "test.apples", StringComparison.Ordinal );
        var zebras = canon.IndexOf( "test.zebras", StringComparison.Ordinal );
        var aIdx = canon.IndexOf( "WAIT a_idx", StringComparison.Ordinal );
        var zIdx = canon.IndexOf( "WAIT z_idx", StringComparison.Ordinal );

        apples.Should().BeLessThan( zebras );
        aIdx.Should().BeLessThan( zIdx );

        // round-trip idempotence
        Canonicalizer.Canonicalize( canon ).Should().Be( canon );
    }

    [TestMethod]
    public void Canonicalize_EmptySnapshot_EmitsHeaderOnly()
    {
        var canon = Canonicalizer.Canonicalize( "" );

        canon.Should().StartWith( "-- aerospike-squash v1" );
        canon.Should().NotContain( "CREATE" );
    }

    [TestMethod]
    public void Canonicalize_OnlySetsSection_OmitsIndexesBlock()
    {
        const string snapshot = """
            [sets]
            ns=test:set=users
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        canon.Should().Contain( "CREATE SET test.users;" );
        canon.Should().NotContain( "secondary indexes" );
        canon.Should().NotContain( "CREATE INDEX" );
    }

    [TestMethod]
    public void Canonicalize_IndexTypeDefault_NormalizesToString()
    {
        const string snapshot = """
            [sindex]
            ns=test:indexname=idx:set=users:bin=name:type=DEFAULT
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        canon.Should().Contain( "STRING" );
    }

    [TestMethod]
    public void Canonicalize_IndexTypeMissing_DefaultsToString()
    {
        const string snapshot = """
            [sindex]
            ns=test:indexname=idx:set=users:bin=name
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        canon.Should().Contain( "STRING" );
    }

    [TestMethod]
    public void Canonicalize_IndexTypeGeo_PreservedAsGeo2DSphere()
    {
        const string snapshot = """
            [sindex]
            ns=test:indexname=idx_loc:set=places:bin=location:type=GEO2DSPHERE
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        canon.Should().Contain( "CREATE INDEX WAIT idx_loc ON test.places(location) GEO2DSPHERE;" );
    }

    [TestMethod]
    public void Canonicalize_SectionHeaderCaseInsensitive()
    {
        const string snapshot = """
            [SETS]
            ns=test:set=users

            [SIndex]
            ns=test:indexname=idx:set=users:bin=name:type=STRING
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        canon.Should().Contain( "CREATE SET test.users;" );
        canon.Should().Contain( "CREATE INDEX WAIT idx" );
    }

    [TestMethod]
    public void EmitScript_IsCanonicalize()
    {
        const string snapshot = """
            [sets]
            ns=test:set=users
            """;

        Canonicalizer.EmitScript( snapshot ).Should().Be( Canonicalizer.Canonicalize( snapshot ) );
    }

    // ---- internal helper tests ---------------------------------------------

    [TestMethod]
    public void ParseSections_ExtractsBothSections()
    {
        const string snapshot = """
            # comment

            [sets]
            ns=test:set=users

            [sindex]
            ns=test:indexname=idx:set=users:bin=name:type=STRING
            """;

        var sections = AerospikeSnapshotCanonicalizer.ParseSections( snapshot );

        sections.HasAny.Should().BeTrue();
        sections.Sets.Should().Contain( "ns=test:set=users" );
        sections.SIndex.Should().Contain( "indexname=idx" );
    }

    [TestMethod]
    public void ParseSections_NoHeaders_ReturnsEmpty()
    {
        var sections = AerospikeSnapshotCanonicalizer.ParseSections( "CREATE SET test.users;" );

        sections.HasAny.Should().BeFalse();
    }

    [TestMethod]
    public void ParseEntries_SemicolonSeparated_YieldsEachEntry()
    {
        const string response = "ns=test:set=users:objects=42;ns=test:set=orders:objects=10";

        var entries = AerospikeSnapshotCanonicalizer.ParseEntries( response ).ToList();

        entries.Should().HaveCount( 2 );
        entries[0]["ns"].Should().Be( "test" );
        entries[0]["set"].Should().Be( "users" );
        entries[0]["objects"].Should().Be( "42" );
        entries[1]["set"].Should().Be( "orders" );
    }

    [TestMethod]
    public void ParseEntries_KeyLookupIsCaseInsensitive()
    {
        const string response = "NS=test:SET=users";

        var entries = AerospikeSnapshotCanonicalizer.ParseEntries( response ).ToList();

        entries[0]["ns"].Should().Be( "test" );
        entries[0]["set"].Should().Be( "users" );
    }
}
