namespace Hyperbee.Migrations.Squash;

/// <summary>
/// Classification of a single statement or call site as it relates to
/// squash codegen (per ADR-0019 A8). The classifier is used by the squash
/// generator to decide which migrations carry user data ops that must be
/// preserved verbatim vs. structural ops that can be regenerated from a
/// canonical snapshot.
/// </summary>
/// <param name="IsDataOp">
/// True if the call site emits user data (INSERT/UPDATE/DELETE/MERGE/COPY/
/// TRUNCATE/SELECT INTO/CREATE TABLE AS SELECT).
/// </param>
/// <param name="RequiresPreservation">
/// True if the call site must be carried forward into the squashed migration
/// body verbatim (data ops, opaque DO blocks, function bodies that contain
/// DML).
/// </param>
/// <param name="IsUnclassified">
/// True if the classifier could not determine the classification with
/// confidence. Default-deny per ADR-0019: an unclassified call site forces
/// the squash to refuse with diagnostics until the operator annotates
/// (<c>[DataMigration]</c> or <c>[StructuralOnly]</c>).
/// </param>
/// <param name="RequiresAnnotation">
/// True when the classifier suspects a data op on a migration class that
/// hasn't been explicitly annotated. Per ADR-0019 A5 the annotation is
/// mandatory; this flag drives the squash refusal.
/// </param>
/// <param name="EmissionHint">
/// Optional human-readable hint about how the codegen should emit this
/// call site. Null when no hint is available.
/// </param>
public sealed record DataOpClassification(
    bool IsDataOp,
    bool RequiresPreservation,
    bool IsUnclassified,
    bool RequiresAnnotation,
    string EmissionHint = null );
