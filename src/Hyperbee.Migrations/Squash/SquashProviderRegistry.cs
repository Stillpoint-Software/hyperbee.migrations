using System.Reflection;
using System.Runtime.Loader;

namespace Hyperbee.Migrations.Squash;

/// <summary>
/// Discovers <see cref="ISquashProvider"/> implementations in the migration
/// assembly's reference closure (per ADR-0024). NuGet package presence IS the
/// registration: the migration project references e.g.
/// <c>Hyperbee.Migrations.Providers.Postgres</c>, which contains a
/// <c>PostgresSquashProvider</c>, and the CLI discovers it through the
/// transitive load context.
/// </summary>
/// <remarks>
/// <para>
/// Single-shot reflection scan at CLI startup. The CLI assembly itself
/// references NO provider packages -- this lookup is what makes "all 5
/// providers" work without hardcoding a list. Third-party providers
/// (future Cassandra, DynamoDB, etc.) plug in via the same surface.
/// </para>
/// <para>
/// Discovery walks every loaded assembly that's transitively referenced from
/// <paramref name="migrationAssembly"/>, including the migration assembly
/// itself. Each <see cref="ISquashProvider"/> implementation must be
/// public, non-abstract, and have a public parameterless constructor.
/// Multiple registrations for the same <see cref="ISquashProvider.ProviderId"/>
/// are an error.
/// </para>
/// </remarks>
public static class SquashProviderRegistry
{
    /// <summary>
    /// Loads every transitively-referenced assembly from <paramref name="migrationAssembly"/>
    /// and returns a map of <see cref="ISquashProvider.ProviderId"/> to
    /// the instantiated provider. Throws <see cref="InvalidOperationException"/>
    /// when two providers report the same id or when a discovered type lacks
    /// a public parameterless constructor.
    /// </summary>
    public static IReadOnlyDictionary<string, ISquashProvider> Discover( Assembly migrationAssembly )
    {
        ArgumentNullException.ThrowIfNull( migrationAssembly );

        // Provider discovery walks two surfaces:
        //
        //   (1) The metadata reference closure of `migrationAssembly` --
        //       finds types the migration project transitively uses.
        //   (2) Every DLL in the migration project's output directory --
        //       catches provider packages that are referenced ONLY for
        //       build-side-effect (e.g., `<ProjectReference>` with no
        //       actual type use in user code). The C# compiler strips
        //       unused references from assembly metadata, so a sample
        //       that has the Squash package as a `<ProjectReference>`
        //       but never uses an `Hyperbee.Migrations.Providers.*.Squash`
        //       type directly drops out of (1). Discovery via directory
        //       scan is the production-correct fallback.
        //
        // Loading routes through the migration assembly's ALC so type
        // identity for shared types (`ISquashProvider`,
        // `IMigrationHost`, etc.) is preserved across the host/plugin
        // boundary.
        var alc = AssemblyLoadContext.GetLoadContext( migrationAssembly )
            ?? AssemblyLoadContext.Default;

        var visited = new HashSet<string>( StringComparer.Ordinal ) { migrationAssembly.GetName().Name! };
        var pending = new Queue<Assembly>();
        pending.Enqueue( migrationAssembly );

        var assemblies = new List<Assembly> { migrationAssembly };
        while ( pending.Count > 0 )
        {
            var current = pending.Dequeue();
            foreach ( var referenced in current.GetReferencedAssemblies() )
            {
                if ( !visited.Add( referenced.Name! ) )
                    continue;
                try
                {
                    var loaded = alc.LoadFromAssemblyName( referenced );
                    assemblies.Add( loaded );
                    pending.Enqueue( loaded );
                }
                catch
                {
                    // Indirect reference that isn't in the load path; skip.
                    // Provider packages a user actually consumes will resolve
                    // because the migration project references them directly.
                }
            }
        }

        // Directory scan for the build-side-effect case (surface 2).
        // Walks DLLs in the migration assembly's directory; loads each
        // via the migration ALC so the providers' types share identity
        // with the host's `ISquashProvider` interface. Skips already-
        // visited assemblies and assemblies that can't be inspected.
        var migrationDir = Path.GetDirectoryName( migrationAssembly.Location );
        if ( !string.IsNullOrEmpty( migrationDir ) && Directory.Exists( migrationDir ) )
        {
            foreach ( var dllPath in Directory.EnumerateFiles( migrationDir, "*.dll" ) )
            {
                AssemblyName name;
                try
                {
                    name = AssemblyName.GetAssemblyName( dllPath );
                }
                catch
                {
                    // Native or otherwise non-managed DLL; skip.
                    continue;
                }

                if ( !visited.Add( name.Name! ) )
                    continue;

                try
                {
                    var loaded = alc.LoadFromAssemblyName( name );
                    assemblies.Add( loaded );
                }
                catch
                {
                    // Mixed-mode, broken metadata, version mismatch, etc.
                    // Provider packages a user actually consumes will be
                    // loadable; everything else can be skipped.
                }
            }
        }

        var providers = new Dictionary<string, ISquashProvider>( StringComparer.OrdinalIgnoreCase );
        var providerInterface = typeof( ISquashProvider );

        foreach ( var assembly in assemblies )
        {
            Type[] types;
            try
            {
                types = assembly.GetTypes();
            }
            catch ( ReflectionTypeLoadException ex )
            {
                types = ex.Types.Where( t => t != null ).Cast<Type>().ToArray();
            }

            foreach ( var type in types )
            {
                if ( !providerInterface.IsAssignableFrom( type ) )
                    continue;
                if ( !type.IsPublic && !type.IsNestedPublic )
                    continue;
                if ( type.IsAbstract || type.IsInterface )
                    continue;
                if ( type.GetConstructor( Type.EmptyTypes ) == null )
                    continue;

                ISquashProvider instance;
                try
                {
                    instance = (ISquashProvider) Activator.CreateInstance( type )!;
                }
                catch ( Exception ex )
                {
                    throw new InvalidOperationException(
                        $"ISquashProvider implementation `{type.FullName}` could not be activated: {ex.Message}", ex );
                }

                if ( string.IsNullOrWhiteSpace( instance.ProviderId ) )
                {
                    throw new InvalidOperationException(
                        $"ISquashProvider implementation `{type.FullName}` returned a null or whitespace ProviderId." );
                }

                if ( providers.TryGetValue( instance.ProviderId, out var existing ) )
                {
                    throw new InvalidOperationException(
                        $"Two ISquashProvider implementations report ProviderId `{instance.ProviderId}`: " +
                        $"`{existing.GetType().FullName}` and `{type.FullName}`. " +
                        "Each provider id may have at most one implementation in the migration assembly's reference closure." );
                }

                providers[instance.ProviderId] = instance;
            }
        }

        return providers;
    }
}
