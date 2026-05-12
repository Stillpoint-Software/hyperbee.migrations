using System.Reflection;

namespace Hyperbee.Migrations.Squash.Cli;

/// <summary>
/// Discovers <see cref="ISquashCliProvider"/> implementations in the migration
/// assembly's reference closure (per ADR-0024). NuGet package presence IS the
/// registration: the migration project references e.g.
/// <c>Hyperbee.Migrations.Providers.Postgres</c>, which contains a
/// <c>PostgresSquashCliProvider</c>, and the CLI discovers it through the
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
/// itself. Each <see cref="ISquashCliProvider"/> implementation must be
/// public, non-abstract, and have a public parameterless constructor.
/// Multiple registrations for the same <see cref="ISquashCliProvider.ProviderId"/>
/// are an error.
/// </para>
/// </remarks>
public static class CliProviderRegistry
{
    /// <summary>
    /// Loads every transitively-referenced assembly from <paramref name="migrationAssembly"/>
    /// and returns a map of <see cref="ISquashCliProvider.ProviderId"/> to
    /// the instantiated provider. Throws <see cref="InvalidOperationException"/>
    /// when two providers report the same id or when a discovered type lacks
    /// a public parameterless constructor.
    /// </summary>
    public static IReadOnlyDictionary<string, ISquashCliProvider> Discover( Assembly migrationAssembly )
    {
        ArgumentNullException.ThrowIfNull( migrationAssembly );

        // Walk the reference closure. Each AssemblyName is loaded via
        // Assembly.Load -- if the assembly can't resolve, we ignore it
        // (it's an indirect reference whose provider type, if any, would
        // not be reachable from the migration project's actual code path).
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
                    var loaded = Assembly.Load( referenced );
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

        var providers = new Dictionary<string, ISquashCliProvider>( StringComparer.OrdinalIgnoreCase );
        var providerInterface = typeof( ISquashCliProvider );

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

                ISquashCliProvider instance;
                try
                {
                    instance = (ISquashCliProvider) Activator.CreateInstance( type )!;
                }
                catch ( Exception ex )
                {
                    throw new InvalidOperationException(
                        $"ISquashCliProvider implementation `{type.FullName}` could not be activated: {ex.Message}", ex );
                }

                if ( string.IsNullOrWhiteSpace( instance.ProviderId ) )
                {
                    throw new InvalidOperationException(
                        $"ISquashCliProvider implementation `{type.FullName}` returned a null or whitespace ProviderId." );
                }

                if ( providers.TryGetValue( instance.ProviderId, out var existing ) )
                {
                    throw new InvalidOperationException(
                        $"Two ISquashCliProvider implementations report ProviderId `{instance.ProviderId}`: " +
                        $"`{existing.GetType().FullName}` and `{type.FullName}`. " +
                        "Each provider id may have at most one implementation in the migration assembly's reference closure." );
                }

                providers[instance.ProviderId] = instance;
            }
        }

        return providers;
    }
}
