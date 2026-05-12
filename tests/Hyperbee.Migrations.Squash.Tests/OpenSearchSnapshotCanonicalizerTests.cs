using FluentAssertions;
using Hyperbee.Migrations.Providers.OpenSearch.Squash;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace Hyperbee.Migrations.Squash.Tests;

// Phase 2 Task 2.4: OpenSearchSnapshotCanonicalizer unit coverage.
//
// Exercises:
//   - section parsing ([index_template], [ism_policy], etc.)
//   - recursive JSON key sort (ordinal) at every nesting level
//   - ephemeral field stripping at every nesting level
//   - painless source preservation (opaque-string per Task 2.0 spike)
//   - idempotence (C12 byte-stability across re-runs)
//   - cross-section determinism (section ordering canonical)
//
// The C12 determinism gate integration test (R-P5) re-runs the full strategy
// against a real OpenSearch container and asserts byte-equal output across
// runs; that test lives in the Phase 2 integration suite.

[TestClass]
public class OpenSearchSnapshotCanonicalizerTests
{
    private static readonly OpenSearchSnapshotCanonicalizer Canonicalizer = new();

    [TestMethod]
    public void ProviderId_IsOpenSearch()
    {
        Canonicalizer.ProviderId.Should().Be( "opensearch" );
    }

    // ---- section parsing ---------------------------------------------------

    [TestMethod]
    public void Canonicalize_BasicSnapshot_EmitsSectionHeaderedCanonicalForm()
    {
        const string snapshot = """
            # opensearch-snapshot v1
            # cluster: test-cluster

            [index_template]
            {"my-template": {"index_patterns": ["users-*"], "template": {"settings": {"number_of_shards": 1}}}}

            [component_template]
            {"my-component": {"template": {"mappings": {"properties": {"name": {"type": "text"}}}}}}
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        canon.Should().Contain( "# opensearch-squash v1" );
        canon.Should().Contain( "[component_template]" );
        canon.Should().Contain( "[index_template]" );
        canon.Should().Contain( "my-template" );
        canon.Should().Contain( "my-component" );
    }

    [TestMethod]
    public void Canonicalize_SectionsEmittedInAlphabeticalOrder()
    {
        // Source order is [index_template] then [alias]. Output must be
        // alphabetical: [alias] before [index_template].
        const string snapshot = """
            [index_template]
            {"t1": {}}

            [alias]
            {"a1": {"aliases": {}}}
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        var aliasIdx = canon.IndexOf( "[alias]", StringComparison.Ordinal );
        var templateIdx = canon.IndexOf( "[index_template]", StringComparison.Ordinal );

        aliasIdx.Should().BeGreaterThan( -1 );
        templateIdx.Should().BeGreaterThan( -1 );
        aliasIdx.Should().BeLessThan( templateIdx,
            "sections must emit in alphabetical order regardless of capture sequence" );
    }

    [TestMethod]
    public void Canonicalize_EmptySnapshot_EmitsHeaderOnly()
    {
        var canon = Canonicalizer.Canonicalize( "" );

        canon.Should().StartWith( "# opensearch-squash v1" );
        canon.Should().NotContain( "[" );
    }

    // ---- JSON key sorting --------------------------------------------------

    [TestMethod]
    public void Canonicalize_SortsTopLevelKeysOrdinal()
    {
        const string snapshot = """
            [alias]
            {"zebra-alias": {"aliases": {}}, "apple-alias": {"aliases": {}}}
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        var appleIdx = canon.IndexOf( "apple-alias", StringComparison.Ordinal );
        var zebraIdx = canon.IndexOf( "zebra-alias", StringComparison.Ordinal );

        appleIdx.Should().BeGreaterThan( -1 );
        zebraIdx.Should().BeGreaterThan( -1 );
        appleIdx.Should().BeLessThan( zebraIdx );
    }

    [TestMethod]
    public void Canonicalize_SortsNestedKeysOrdinal()
    {
        // Nested object keys must also be sorted -- this is the load-bearing
        // property for byte-stability of mappings, settings, and template
        // bodies which are arbitrarily deep.
        const string snapshot = """
            [index_template]
            {"t1": {"zebra_field": 1, "apple_field": 2, "mango_field": {"nested_z": "z", "nested_a": "a"}}}
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        // Top-level inside t1: apple_field, mango_field, zebra_field (ordinal).
        var appleIdx = canon.IndexOf( "apple_field", StringComparison.Ordinal );
        var mangoIdx = canon.IndexOf( "mango_field", StringComparison.Ordinal );
        var zebraIdx = canon.IndexOf( "zebra_field", StringComparison.Ordinal );

        appleIdx.Should().BeLessThan( mangoIdx );
        mangoIdx.Should().BeLessThan( zebraIdx );

        // Inside mango_field: nested_a before nested_z.
        var nestedAIdx = canon.IndexOf( "nested_a", StringComparison.Ordinal );
        var nestedZIdx = canon.IndexOf( "nested_z", StringComparison.Ordinal );
        nestedAIdx.Should().BeLessThan( nestedZIdx );
    }

    [TestMethod]
    public void Canonicalize_PreservesArrayOrder()
    {
        // Arrays preserve order. Index patterns, ISM state transitions, etc.
        // depend on declaration order; sorting them would alter semantics.
        const string snapshot = """
            [index_template]
            {"t1": {"index_patterns": ["logs-2024-*", "logs-2023-*", "logs-2022-*"]}}
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        var idx2024 = canon.IndexOf( "logs-2024", StringComparison.Ordinal );
        var idx2023 = canon.IndexOf( "logs-2023", StringComparison.Ordinal );
        var idx2022 = canon.IndexOf( "logs-2022", StringComparison.Ordinal );

        idx2024.Should().BeLessThan( idx2023 );
        idx2023.Should().BeLessThan( idx2022 );
    }

    // ---- ephemeral stripping -----------------------------------------------

    [TestMethod]
    public void Canonicalize_StripsCreationDate()
    {
        const string snapshot = """
            [index_template]
            {"t1": {"creation_date": "1700000000", "version": 42, "index_patterns": ["x-*"]}}
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        canon.Should().NotContain( "creation_date" );
        canon.Should().NotContain( "1700000000" );
        // version is also ephemeral
        canon.Should().NotContain( "\"version\"" );
        canon.Should().Contain( "index_patterns" );
    }

    [TestMethod]
    public void Canonicalize_StripsEphemeralsAtEveryNestingLevel()
    {
        // Ephemerals nest deep in OpenSearch responses: index_metadata has
        // settings.index.creation_date, settings.index.uuid, etc.
        const string snapshot = """
            [index_metadata]
            {
              "users": {
                "creation_date": "1700000000",
                "settings": {
                  "index": {
                    "uuid": "abc-123",
                    "version": {"created": "2130099"},
                    "number_of_shards": "1"
                  }
                },
                "mappings": {
                  "_meta": {"version": "ignored"},
                  "properties": {"name": {"type": "text"}}
                }
              }
            }
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        canon.Should().NotContain( "creation_date" );
        canon.Should().NotContain( "uuid" );
        canon.Should().NotContain( "\"version\"" );
        canon.Should().Contain( "number_of_shards" );
        canon.Should().Contain( "\"name\"" );
    }

    [TestMethod]
    public void Canonicalize_StripsIsmPolicyMetadata()
    {
        const string snapshot = """
            [ism_policy]
            {
              "policies": [{
                "_id": "hot-warm-delete",
                "policy_version": 3,
                "last_updated_time": 1700000000000,
                "seq_no": 5,
                "primary_term": 1,
                "policy": {
                  "description": "hot-warm-delete",
                  "default_state": "hot",
                  "states": [{"name": "hot", "actions": [], "transitions": []}]
                }
              }]
            }
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        canon.Should().NotContain( "policy_version" );
        canon.Should().NotContain( "last_updated_time" );
        canon.Should().NotContain( "seq_no" );
        canon.Should().NotContain( "primary_term" );
        canon.Should().Contain( "hot-warm-delete" );
        canon.Should().Contain( "default_state" );
    }

    [TestMethod]
    public void Canonicalize_StripsProvidedName()
    {
        // OpenSearch echoes the index name as `provided_name` inside the
        // settings.index block; it's redundant because the index name is
        // the dictionary key. Stripping prevents byte-divergence when an
        // operator's index naming differs slightly between captures.
        const string snapshot = """
            [index_metadata]
            {"users": {"settings": {"index": {"provided_name": "users", "number_of_shards": "1"}}}}
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        canon.Should().NotContain( "provided_name" );
        canon.Should().Contain( "number_of_shards" );
    }

    // ---- painless preservation (Task 2.0 spike conclusion) -----------------

    [TestMethod]
    public void Canonicalize_PainlessSource_PreservedVerbatim()
    {
        // Per the Task 2.0 painless-equivalence spike: painless source rides
        // through as opaque JSON string content. Whitespace, comments, and
        // language constructs must round-trip byte-for-byte.
        const string snapshot = """
            [ingest_pipeline]
            {
              "audit-pipeline": {
                "description": "Adds audit metadata",
                "processors": [
                  {
                    "script": {
                      "source": "// add audit timestamp\n  ctx['audited_at'] = '2024-01-01';\n  /* preserve me */"
                    }
                  }
                ]
              }
            }
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        // The painless source must appear in the canonical output with
        // its comments and whitespace intact. JSON encoding may escape \n
        // as \\n -- that's part of the standard JSON string literal form,
        // so the canonical output contains the escaped sequence verbatim.
        canon.Should().Contain( "// add audit timestamp" );
        canon.Should().Contain( "/* preserve me */" );
        canon.Should().Contain( "ctx['audited_at']" );
    }

    [TestMethod]
    public void Canonicalize_PainlessWithDoubleQuotesInString_RoundTripsViaJsonEscape()
    {
        // Painless containing double quotes (which JSON requires escaped)
        // must round-trip cleanly. The writer's UnsafeRelaxedJsonEscaping
        // encoder escapes only the minimal JSON-required set.
        const string snapshot = """
            [ingest_pipeline]
            {"pipeline": {"processors": [{"script": {"source": "ctx['msg'] = \"hello world\""}}]}}
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        // The escape `\"` survives the round-trip.
        canon.Should().Contain( "\\\"hello world\\\"" );
    }

    // ---- idempotence (C12) -------------------------------------------------

    [TestMethod]
    public void Canonicalize_IsIdempotent()
    {
        const string snapshot = """
            [index_template]
            {"t1": {"creation_date": "111", "index_patterns": ["x-*"], "version": 1}}

            [alias]
            {"a1": {"aliases": {"users-current": {}}}}
            """;

        var first = Canonicalizer.Canonicalize( snapshot );
        var second = Canonicalizer.Canonicalize( first );

        second.Should().Be( first, "Canonicalize must be idempotent for the C12 determinism gate" );
    }

    [TestMethod]
    public void Canonicalize_DifferentKeyOrders_ProduceSameOutput()
    {
        // Two captures of the same logical state with different server-side
        // JSON key ordering must produce byte-equal canonical output.
        const string snapshotA = """
            [index_template]
            {"t1": {"index_patterns": ["x-*"], "template": {"settings": {"number_of_shards": "1"}}}}
            """;

        const string snapshotB = """
            [index_template]
            {"t1": {"template": {"settings": {"number_of_shards": "1"}}, "index_patterns": ["x-*"]}}
            """;

        var canonA = Canonicalizer.Canonicalize( snapshotA );
        var canonB = Canonicalizer.Canonicalize( snapshotB );

        canonB.Should().Be( canonA );
    }

    [TestMethod]
    public void Canonicalize_DifferentEphemeralValues_ProduceSameOutput()
    {
        // Re-capturing after a no-op operation yields different ephemeral
        // timestamps but identical structural content. Canonicalization
        // strips the ephemerals so the bytes match.
        const string snapshotA = """
            [index_template]
            {"t1": {"creation_date": "1700000000", "index_patterns": ["x-*"]}}
            """;

        const string snapshotB = """
            [index_template]
            {"t1": {"creation_date": "1800000000", "index_patterns": ["x-*"]}}
            """;

        Canonicalizer.Canonicalize( snapshotA )
            .Should().Be( Canonicalizer.Canonicalize( snapshotB ) );
    }

    // ---- normalization edge cases ------------------------------------------

    [TestMethod]
    public void Canonicalize_NormalizesLineEndings()
    {
        var crlfSnapshot = "[alias]\r\n{\"a1\":{\"aliases\":{}}}\r\n";

        var canon = Canonicalizer.Canonicalize( crlfSnapshot );

        canon.Should().NotContain( "\r" );
        canon.Should().Contain( "\"a1\"" );
    }

    [TestMethod]
    public void Canonicalize_PreservesNumberRepresentation()
    {
        // Numeric fields in OpenSearch mappings sometimes encode integers
        // as "1" (string) or floats as 1.0 or 1e0. The canonicalizer
        // preserves the operator's representation exactly (via GetRawText)
        // because round-tripping through double would lose precision.
        const string snapshot = """
            [index_template]
            {"t1": {"a": "1", "b": 1, "c": 1.0, "d": 1e3}}
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        canon.Should().Contain( "\"a\": \"1\"" );
        canon.Should().Contain( "\"b\": 1" );
        canon.Should().Contain( "\"c\": 1.0" );
        canon.Should().Contain( "\"d\": 1e3" );
    }

    [TestMethod]
    public void Canonicalize_CaseInsensitiveSectionHeaders()
    {
        const string snapshot = """
            [INDEX_TEMPLATE]
            {"t1": {"index_patterns": ["x-*"]}}

            [Alias]
            {"a1": {"aliases": {}}}
            """;

        var canon = Canonicalizer.Canonicalize( snapshot );

        // Section headers are normalized to lowercase on emit.
        canon.Should().Contain( "[alias]" );
        canon.Should().Contain( "[index_template]" );
        canon.Should().NotContain( "[INDEX_TEMPLATE]" );
        canon.Should().NotContain( "[Alias]" );
    }

    // ---- error handling ----------------------------------------------------

    [TestMethod]
    public void Canonicalize_InvalidJsonSection_ThrowsMigrationException()
    {
        const string snapshot = """
            [alias]
            { this is not valid json
            """;

        Action act = () => Canonicalizer.Canonicalize( snapshot );
        act.Should().Throw<MigrationException>()
            .WithMessage( "*not valid JSON*" )
            .Which.Message.Should().Contain( "[alias]" );
    }

    [TestMethod]
    public void Canonicalize_NullInput_Throws()
    {
        Action act = () => Canonicalizer.Canonicalize( null! );
        act.Should().Throw<ArgumentNullException>();
    }

    [TestMethod]
    public void EmitScript_IsCanonicalize()
    {
        const string snapshot = """
            [alias]
            {"a1": {"aliases": {}}}
            """;

        Canonicalizer.EmitScript( snapshot ).Should().Be( Canonicalizer.Canonicalize( snapshot ) );
    }

    // ---- internal helpers --------------------------------------------------

    [TestMethod]
    public void ParseSections_ExtractsMultipleSections()
    {
        const string snapshot = """
            # comment

            [a]
            {"x": 1}

            [b]
            {"y": 2}
            """;

        var sections = OpenSearchSnapshotCanonicalizer.ParseSections( snapshot );

        sections.HasAny.Should().BeTrue();
        sections.Bodies.Should().HaveCount( 2 );
        sections.Bodies["a"].Should().Contain( "\"x\"" );
        sections.Bodies["b"].Should().Contain( "\"y\"" );
    }

    [TestMethod]
    public void ParseSections_NoHeaders_ReturnsEmpty()
    {
        var sections = OpenSearchSnapshotCanonicalizer.ParseSections( "just some text without headers" );
        sections.HasAny.Should().BeFalse();
    }

    [TestMethod]
    public void EphemeralCatalog_IncludesPhase0AppendixCFields()
    {
        // The ephemeral catalog must include the fields documented in
        // Phase 0 Appendix C for OpenSearch. Any change to this catalog
        // is a canonicalization-rule change that R-P7 says requires an
        // ADR-0019 amendment.
        OpenSearchSnapshotCanonicalizer.Ephemerals.Should().Contain( "creation_date" );
        OpenSearchSnapshotCanonicalizer.Ephemerals.Should().Contain( "uuid" );
        OpenSearchSnapshotCanonicalizer.Ephemerals.Should().Contain( "version" );
        OpenSearchSnapshotCanonicalizer.Ephemerals.Should().Contain( "policy_version" );
        OpenSearchSnapshotCanonicalizer.Ephemerals.Should().Contain( "last_updated_time" );
    }
}
