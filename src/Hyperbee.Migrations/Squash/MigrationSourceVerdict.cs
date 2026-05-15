namespace Hyperbee.Migrations.Squash;

/// <summary>
/// Per-class verdict produced by <see cref="ISquashProvider.ScanSource"/>.
/// The CLI walks the returned verdicts, cross-references against the
/// subsumed migrations, and refuses squash generation when any subsumed
/// class still requires annotation (ADR-0019 A5 default-deny).
/// </summary>
public sealed record MigrationSourceVerdict
{
    /// <summary>Source file path (informational; used in error messages).</summary>
    public required string FilePath { get; init; }

    /// <summary>Class name as seen by the Roslyn scanner.</summary>
    public required string ClassName { get; init; }

    /// <summary>
    /// True when the class lacks both <c>[DataMigration]</c> and
    /// <c>[StructuralOnly]</c> AND the scanner heuristics detected at
    /// least one data op or non-determinism source. CLI rejects generation
    /// when any subsumed class flags this true.
    /// </summary>
    public required bool RequiresAnnotation { get; init; }

    public IReadOnlyList<string> DataOpHits { get; init; } = Array.Empty<string>();
    public IReadOnlyList<string> NonDeterminismHits { get; init; } = Array.Empty<string>();
}
