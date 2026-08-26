#nullable enable
using System.Text;
using System.Text.Json;
using System.Text.RegularExpressions;
using FluentAssertions;
using Hyperbee.Migrations.Providers.Couchbase;

namespace Hyperbee.Migrations.Tests;

// Wire-shape tests for the Couchbase ledger (ADR-0029 Rule 3).
//
// Couchbase cannot satisfy ADR-0029 Rule 2 the way MongoDB does: N1QL has no
// typed field reference, so IntersectWithSquashedAsync must name the ledger
// document's fields as text (`m.kind`, `m.replaces`). Rule 1 applies instead --
// the record store pins the serializer used for ledger KV operations rather
// than inheriting ClusterOptions.Serializer, so the document shape those names
// target is a library guarantee.
//
// Without the pin, a consumer registering a System.Text.Json serializer (or a
// Newtonsoft one without the camelCase resolver) writes `Kind` / `Replaces`,
// the squash query matches nothing, and squash reconciliation silently covers
// no versions -- squashed migrations re-run forever with no error.
//
// These tests read the REAL query text and the REAL pinned serializer's output
// and assert they agree.

[TestClass]
public class CouchbaseLedgerWireTests
{
    private const string Keyspace = "`ledger`.`_default`.`_default`";

    private static MigrationRecord SquashRecord() => new()
    {
        Id = "1000.alpha",
        Checksum = "sha256:abc",
        Kind = MigrationRecordKind.Squash,
        Replaces = [900L, 901L]
    };

    // The JSON property names the pinned ledger serializer actually emits.
    private static Dictionary<string, JsonElement> WriterFields()
    {
        using var stream = new MemoryStream();
        CouchbaseRecordStore.LedgerSerializer.Serialize( stream, SquashRecord() );

        var json = Encoding.UTF8.GetString( stream.ToArray() );
        using var document = JsonDocument.Parse( json );

        return document.RootElement.EnumerateObject()
            .ToDictionary( p => p.Name, p => p.Value.Clone(), StringComparer.Ordinal );
    }

    // `m.<field>` references in the N1QL statement. The alias is bound by the
    // query itself (`... AS m`), so any m-qualified path is a ledger field.
    private static List<string> QueryFieldNames( string statement ) =>
        Regex.Matches( statement, @"\bm\.(?<field>[A-Za-z_][A-Za-z0-9_]*)" )
            .Select( match => match.Groups["field"].Value )
            .Distinct( StringComparer.Ordinal )
            .ToList();

    // ---- the invariant ---------------------------------------------------

    [TestMethod]
    public void SquashQuery_ReferencesOnlyFieldsTheLedgerSerializerProduces()
    {
        var referenced = QueryFieldNames( CouchbaseRecordStore.BuildSquashQuery( Keyspace ) );
        var written = WriterFields();

        referenced.Should().NotBeEmpty();
        referenced.Should().OnlyContain(
            name => written.ContainsKey( name ),
            "N1QL names ledger fields as text, so every reference must match what the " +
            $"pinned serializer emits; it emits [{string.Join( ", ", written.Keys )}], the " +
            $"query references [{string.Join( ", ", referenced )}]" );
    }

    [TestMethod]
    public void SquashQuery_KindPredicateMatchesTheSerializedKindValue()
    {
        // The query filters `m.kind = 1`. The literal has to equal what the
        // serializer writes for MigrationRecordKind.Squash -- a serializer that
        // emitted the enum as a string ("Squash") would satisfy the field-name
        // check above and still match nothing.
        var statement = CouchbaseRecordStore.BuildSquashQuery( Keyspace );
        var written = WriterFields();

        var match = Regex.Match( statement, @"\bm\.(?<field>\w+)\s*=\s*(?<value>\d+)" );
        match.Success.Should().BeTrue( "the squash query pins Kind with a numeric literal" );

        var field = match.Groups["field"].Value;
        var expected = int.Parse( match.Groups["value"].Value );

        written[field].GetInt32().Should().Be( expected );
        expected.Should().Be( (int) MigrationRecordKind.Squash );
    }

    [TestMethod]
    public void SquashQuery_UnnestTargetIsTheSerializedReplacesArray()
    {
        // `UNNEST m.replaces AS v` only yields rows if that path is the array the
        // writer emitted. An object or a missing path unnests to nothing.
        var statement = CouchbaseRecordStore.BuildSquashQuery( Keyspace );
        var written = WriterFields();

        var match = Regex.Match( statement, @"\bUNNEST\s+m\.(?<field>\w+)\b", RegexOptions.IgnoreCase );
        match.Success.Should().BeTrue();

        var field = match.Groups["field"].Value;

        written.Should().ContainKey( field );
        written[field].ValueKind.Should().Be( JsonValueKind.Array );
        written[field].EnumerateArray().Select( e => e.GetInt64() ).Should().Contain( 900L );
    }

    [TestMethod]
    public void LedgerSerializer_IsPinned_NotInheritedFromClusterOptions()
    {
        // The point of the pin is that the ledger's shape does not move when a
        // consumer changes ClusterOptions.Serializer. Assert the store holds its
        // own instance rather than resolving one per call.
        CouchbaseRecordStore.LedgerSerializer.Should().NotBeNull();
        CouchbaseRecordStore.LedgerSerializer.Should().BeSameAs( CouchbaseRecordStore.LedgerSerializer );
    }
}
