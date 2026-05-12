using System.Reflection;

namespace Hyperbee.Migrations;

/// <summary>
/// Single-shot reflection helper for the CLI / runner / recovery verb to
/// locate the <see cref="IMigrationHost"/> implementation in a migration
/// assembly. Per ADR-0024: each migration assembly exposes exactly one
/// public, non-abstract, default-constructible <see cref="IMigrationHost"/>
/// implementer. Refuses with an actionable error if zero or multiple
/// candidates are found.
/// </summary>
/// <remarks>
/// <para>
/// This is the <b>only</b> reflection site in the squash CLI's interaction
/// with user migration code. After discovery, all downstream interaction
/// goes through the <see cref="IMigrationHost"/> contract and the typed
/// services its <see cref="IServiceProvider"/> resolves.
/// </para>
/// </remarks>
public static class MigrationHostDiscovery
{
    /// <summary>
    /// Locate the single <see cref="IMigrationHost"/> implementation in
    /// <paramref name="migrationAssembly"/>. Throws with operator-actionable
    /// detail when zero or multiple candidates are found.
    /// </summary>
    /// <exception cref="ArgumentNullException">When <paramref name="migrationAssembly"/> is null.</exception>
    /// <exception cref="InvalidOperationException">When zero or multiple host candidates exist in the assembly.</exception>
    public static IMigrationHost Discover( Assembly migrationAssembly )
    {
        ArgumentNullException.ThrowIfNull( migrationAssembly );

        var assemblyName = migrationAssembly.GetName().Name ?? "<unknown>";

        var candidates = FindCandidates( migrationAssembly ).ToArray();

        return candidates.Length switch
        {
            1 => (IMigrationHost) Activator.CreateInstance( candidates[0] )!,
            0 => throw new InvalidOperationException(
                $"Migration assembly `{assemblyName}` does not contain a public, non-abstract, " +
                $"default-constructible type implementing `{nameof( IMigrationHost )}`. " +
                "Add a class like " +
                "`public class YourMigrationsHost : IMigrationHost { public Task<IServiceProvider> ConfigureAsync(...) {...} }` " +
                "exposing your migration project's DI setup. See `docs/site/cli.md` and ADR-0024." ),
            _ => throw new InvalidOperationException(
                $"Migration assembly `{assemblyName}` contains multiple `{nameof( IMigrationHost )}` implementations: " +
                $"{string.Join( ", ", candidates.Select( t => t.FullName ) )}. " +
                "Each migration assembly must expose exactly one host. " +
                "Combine them into a single host that wires every provider your project uses, " +
                "or move the extras to a separate assembly." )
        };
    }

    /// <summary>
    /// Returns true when <paramref name="migrationAssembly"/> contains
    /// exactly one valid <see cref="IMigrationHost"/> candidate. Useful for
    /// pre-flight checks (e.g., the CLI's `providers list` verb) that want
    /// to enumerate without throwing.
    /// </summary>
    public static bool TryDiscover( Assembly migrationAssembly, out IMigrationHost host )
    {
        ArgumentNullException.ThrowIfNull( migrationAssembly );

        var candidates = FindCandidates( migrationAssembly ).ToArray();
        if ( candidates.Length == 1 )
        {
            host = (IMigrationHost) Activator.CreateInstance( candidates[0] )!;
            return true;
        }

        host = null!;
        return false;
    }

    /// <summary>
    /// Enumerates the candidate types without activating them. Exposed for
    /// the CLI's diagnostic verbs (e.g., `providers list --assembly ...`)
    /// that want to report what was found rather than throw.
    /// </summary>
    public static IReadOnlyList<Type> EnumerateCandidates( Assembly migrationAssembly )
    {
        ArgumentNullException.ThrowIfNull( migrationAssembly );
        return FindCandidates( migrationAssembly ).ToArray();
    }

    private static IEnumerable<Type> FindCandidates( Assembly assembly )
    {
        Type[] types;
        try
        {
            types = assembly.GetTypes();
        }
        catch ( ReflectionTypeLoadException ex )
        {
            // Partial-load surfaces from missing transitive deps. Filter
            // out null entries and continue with what we have.
            types = ex.Types.Where( t => t != null ).ToArray()!;
        }

        return types.Where( IsHostCandidate );
    }

    private static bool IsHostCandidate( Type type )
    {
        return type is { IsPublic: true, IsAbstract: false, IsInterface: false }
               && typeof( IMigrationHost ).IsAssignableFrom( type )
               && type.GetConstructor( Type.EmptyTypes ) != null;
    }
}
