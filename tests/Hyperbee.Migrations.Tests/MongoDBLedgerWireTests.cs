#nullable enable
using FluentAssertions;
using Hyperbee.Migrations.Providers.MongoDB;
using MongoDB.Bson;
using MongoDB.Bson.Serialization;
using MongoDB.Driver;

namespace Hyperbee.Migrations.Tests;

// Wire-shape tests for the MongoDB ledger (ADR-0029 Rule 3).
//
// These render the REAL filters the record store issues and compare them against
// the REAL BSON the record store writes, both through the driver's serializer
// registry. No mock, no container.
//
// The invariant under test (ADR-0029 Rule 2):
//
//   Every field name a ledger query references must be a field name the ledger
//   writer actually produces. The library does not get to assume a casing --
//   it only gets to be self-consistent.
//
// Note these assertions are convention-INDEPENDENT on purpose. They do not
// assert "the field is called Replaces"; they assert "the field the filter asks
// for is the field the writer wrote". A consumer who registers a global
// camelCase convention pack changes both sides together, and these still pass.
// Pinning a literal casing here would have made the tests agree with a bug.
//
// Regression this pins (v3.0.0 - v3.1.0):
//
//   IntersectWithSquashedAsync built its filter half typed and half literal:
//
//       Filter.Eq( x => x.Kind, Squash )        -> renders through the class map -> "Kind"
//       Filter.In( "replaces", ... )            -> raw string, renders verbatim  -> "replaces"
//
//   The driver's default element name is the member name, so the writer stored
//   `Replaces`. The rendered filter { "Kind": 1, "replaces": { "$in": [...] } }
//   could never match. IntersectWithSquashedAsync silently returned empty for
//   every input, so squashes were never recognized as covering their replaced
//   versions and those migrations re-ran.

[TestClass]
public class MongoDBLedgerWireTests
{
    private static readonly long[] Replaced = [900L, 901L];

    private static MigrationRecord SquashRecord() => new()
    {
        Id = "1000.alpha",
        Checksum = "sha256:abc",
        Kind = MigrationRecordKind.Squash,
        Replaces = Replaced
    };

    private static RenderArgs<MigrationRecord> RenderArgs() => new(
        BsonSerializer.SerializerRegistry.GetSerializer<MigrationRecord>(),
        BsonSerializer.SerializerRegistry );

    // The element names the writer actually emits for a ledger row.
    private static HashSet<string> WriterFieldNames() =>
        SquashRecord().ToBsonDocument().Names.ToHashSet( StringComparer.Ordinal );

    // Field names a rendered query references, ignoring operators ($and, $in, ...).
    private static List<string> QueryFieldNames( BsonValue rendered )
    {
        var names = new List<string>();
        Collect( rendered );
        return names;

        void Collect( BsonValue value )
        {
            switch ( value )
            {
                case BsonDocument document:
                    foreach ( var element in document.Elements )
                    {
                        if ( !element.Name.StartsWith( '$' ) )
                            names.Add( element.Name );

                        Collect( element.Value );
                    }
                    break;

                case BsonArray array:
                    foreach ( var item in array )
                        Collect( item );
                    break;
            }
        }
    }

    // ---- the regression --------------------------------------------------

    [TestMethod]
    public void SquashFilter_ReferencesOnlyFieldsTheWriterProduces()
    {
        var rendered = MongoDBRecordStore.BuildSquashFilter( Replaced ).Render( RenderArgs() );

        var referenced = QueryFieldNames( rendered );
        var written = WriterFieldNames();

        referenced.Should().NotBeEmpty();
        referenced.Should().OnlyContain(
            name => written.Contains( name ),
            "a ledger query may only reference field names the ledger writer emits; " +
            $"writer emits [{string.Join( ", ", written )}], filter references " +
            $"[{string.Join( ", ", referenced )}]" );
    }

    [TestMethod]
    public void SquashFilter_SelectsASquashRowCoveringTheRequestedVersion()
    {
        // Hand-evaluate the rendered predicate against the document the writer
        // produces. This is the end-to-end claim: this filter finds this row.
        var rendered = MongoDBRecordStore.BuildSquashFilter( [900L] ).Render( RenderArgs() );
        var document = SquashRecord().ToBsonDocument();

        var names = QueryFieldNames( rendered );
        names.Should().HaveCount( 2, "the filter is (kind == Squash) AND (replaces contains any of ...)" );

        var kindField = names[0];
        var replacesField = names[1];

        document[kindField].ToInt32().Should().Be( (int) MigrationRecordKind.Squash );
        document[replacesField].AsBsonArray.Select( v => v.ToInt64() ).Should().Contain( 900L );
    }

    [TestMethod]
    public void AppliedFilter_ReferencesOnlyFieldsTheWriterProduces()
    {
        var rendered = MongoDBRecordStore.BuildAppliedFilter( ["1000.alpha"] ).Render( RenderArgs() );

        var referenced = QueryFieldNames( rendered );
        var written = WriterFieldNames();

        referenced.Should().NotBeEmpty();
        referenced.Should().OnlyContain( name => written.Contains( name ) );
    }

    [TestMethod]
    public void ReplacesProjection_ReferencesOnlyFieldsTheWriterProduces()
    {
        // IntersectWithSquashedAsync projects Replaces and reads it back off the
        // deserialized record; a projection naming a field the writer never wrote
        // returns rows with an empty Replaces and silently covers nothing.
        var rendered = Builders<MigrationRecord>.Projection
            .Include( x => x.Replaces )
            .Render( RenderArgs() );

        var referenced = QueryFieldNames( rendered );
        var written = WriterFieldNames();

        referenced.Should().NotBeEmpty();
        referenced.Should().OnlyContain( name => written.Contains( name ) );
    }
}
