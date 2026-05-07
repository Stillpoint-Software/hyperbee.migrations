namespace Hyperbee.Migrations;

/// <summary>
/// Lightweight projection of a discovered migration: its <see cref="Type"/>,
/// its <see cref="MigrationAttribute"/>, and the resolved set of versions it
/// replaces (per ADR-0019). The runner builds these during discovery (Phase 2)
/// and passes them to squash strategies via
/// <c>ISquashStrategy.GenerateAsync</c>.
/// </summary>
/// <remarks>
/// <para>
/// <see cref="ResolvedReplaces"/> is the union of <see cref="MigrationAttribute.Replaces"/>
/// and the parsed <see cref="MigrationAttribute.ReplacesRange"/>, intersected
/// with the assembly's discovered version set. Empty for regular (non-squash)
/// migrations.
/// </para>
/// </remarks>
public sealed record MigrationDescriptor(
    Type Type,
    MigrationAttribute Attribute,
    IReadOnlyList<long> ResolvedReplaces );
