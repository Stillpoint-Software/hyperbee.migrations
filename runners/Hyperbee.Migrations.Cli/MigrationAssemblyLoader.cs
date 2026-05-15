using System.Reflection;
using System.Runtime.Loader;

namespace Hyperbee.Migrations.Cli;

/// <summary>
/// Loads the operator-supplied migration assembly into a collectible
/// <see cref="AssemblyLoadContext"/> so the CLI can unload it after the verb
/// completes. Resolves transitively-referenced assemblies from the same
/// directory as the entry assembly (the migration project's build output
/// typically holds all referenced provider packages + IMigrationHost
/// implementations).
/// </summary>
/// <remarks>
/// Per ADR-0024 audit follow-up (F-3): the previous CLI used
/// <see cref="Assembly.LoadFrom"/>, which loads the assembly into the
/// default <see cref="AssemblyLoadContext"/> and prevents unload. That was
/// invisible in the one-shot CLI tool but blocks embedding the CLI in a
/// long-running host. Collectible ALCs unload when no references remain.
/// </remarks>
internal sealed class MigrationAssemblyLoader : IDisposable
{
    private readonly CollectibleLoadContext _context;
    private readonly string _baseDirectory;

    public Assembly Assembly { get; }

    public MigrationAssemblyLoader( string assemblyPath )
    {
        if ( string.IsNullOrWhiteSpace( assemblyPath ) )
            throw new ArgumentException( "assemblyPath is required.", nameof( assemblyPath ) );

        var fullPath = Path.GetFullPath( assemblyPath );
        if ( !File.Exists( fullPath ) )
            throw new FileNotFoundException( $"migration assembly not found: {fullPath}", fullPath );

        _baseDirectory = Path.GetDirectoryName( fullPath )!;
        _context = new CollectibleLoadContext( fullPath );
        Assembly = _context.LoadFromAssemblyPath( fullPath );
    }

    public void Dispose()
    {
        _context.Unload();
    }

    private sealed class CollectibleLoadContext : AssemblyLoadContext
    {
        private readonly string _baseDirectory;
        private readonly AssemblyDependencyResolver _resolver;
        private readonly NuGetCacheProbe _nugetCache;

        public CollectibleLoadContext( string assemblyPath )
            : base( name: Path.GetFileNameWithoutExtension( assemblyPath ) + ":migration-assembly", isCollectible: true )
        {
            _baseDirectory = Path.GetDirectoryName( assemblyPath )!;
            _nugetCache = new NuGetCacheProbe( assemblyPath );
            // Honors the migration assembly's .deps.json so transitive
            // NuGet packages (Npgsql, MongoDB.Driver, Couchbase.NetClient,
            // etc.) resolve from the NuGet cache. Without this, packages
            // that aren't copy-local in the migration project's bin
            // directory fail to load when the discovery scan probes the
            // provider DLLs.
            _resolver = new AssemblyDependencyResolver( assemblyPath );
        }

        // Diagnostic-only tracing. Set HYPERBEE_CLI_ALC_TRACE=1 to surface
        // every plugin-ALC resolution step on stderr; useful when an
        // operator's migration assembly fails to load a transitive
        // dependency. Production runs leave this off.
        private static readonly bool TraceEnabled =
            Environment.GetEnvironmentVariable( "HYPERBEE_CLI_ALC_TRACE" ) == "1";

        private static void Trace( string message )
        {
            if ( TraceEnabled )
                Console.Error.WriteLine( "[alc] " + message );
        }

        protected override Assembly Load( AssemblyName assemblyName )
        {
            // Defer SHARED types to the Default ALC. The CLI binary's own
            // load context already holds `Hyperbee.Migrations` (which
            // defines `ISquashProvider`, `IMigrationHost`, etc.) and any
            // framework assemblies it references. If we loaded a second
            // copy into this collectible ALC, plugin types would implement
            // a different *type identity* of those interfaces and
            // SquashProviderRegistry.Discover would silently report zero
            // providers despite the DLLs being present on disk.
            //
            // Probe the migration project's output directory only for
            // assemblies the Default ALC does NOT already have loaded.
            // Provider packages (Hyperbee.Migrations.Providers.*.Squash,
            // operator-specific helpers, etc.) live here and DO load into
            // the collectible ALC so they can unload cleanly.
            Trace( $"Load({assemblyName.Name})" );

            var alreadyLoaded = Default.Assemblies
                .FirstOrDefault( a => string.Equals(
                    a.GetName().Name, assemblyName.Name, StringComparison.Ordinal ) );
            if ( alreadyLoaded != null )
            {
                Trace( $"{assemblyName.Name}: defaulted (already loaded)" );
                return alreadyLoaded;
            }

            // Try the Default ALC's resolution (the CLI binary's own
            // deps.json + shared framework). When the CLI itself
            // transitively references the requested assembly, Default
            // can load it -- and that's exactly what we want, because a
            // single type identity for shared infrastructure types
            // (Microsoft.Extensions.DependencyInjection.Abstractions,
            // Microsoft.Extensions.Logging.Abstractions, etc.) is
            // required for the migration host's
            // RegisterBaseAliases / IServiceCollection contracts to
            // bind correctly against the host's loaded types. If we
            // duplicated these into the custom ALC, the provider DLL
            // would see a different IServiceCollection type than the
            // host expects and the binder throws MissingMethodException
            // at the first cross-ALC method call.
            try
            {
                var fromDefault = Default.LoadFromAssemblyName( assemblyName );
                Trace( $"{assemblyName.Name}: defaulted (LoadFromAssemblyName)" );
                return fromDefault;
            }
            catch
            {
                // Default ALC can't resolve it (the CLI binary doesn't
                // reference this assembly transitively). Fall through
                // to migration-side resolution.
            }

            // Resolver probes the migration assembly's .deps.json first;
            // this is the path that finds NuGet packages outside of bin
            // (Npgsql, MongoDB.Driver, etc.).
            var resolved = _resolver.ResolveAssemblyToPath( assemblyName );
            Trace( $"{assemblyName.Name}: resolver -> {resolved ?? "<null>"}" );

            // Resolver fallback: probe the NuGet cache directly using
            // the deps.json package metadata. Library projects don't ship
            // a runtimeconfig.json, so AssemblyDependencyResolver returns
            // null for NuGet packages; NuGetCacheProbe parses the
            // deps.json directly and composes the absolute NuGet cache
            // path so those transitives still resolve.
            if ( resolved == null )
            {
                resolved = _nugetCache.Resolve( assemblyName );
                Trace( $"{assemblyName.Name}: nuget-cache -> {resolved ?? "<null>"}" );
            }
            if ( resolved != null && File.Exists( resolved ) )
            {
                try
                {
                    return LoadFromAssemblyPath( resolved );
                }
                catch
                {
                    // Fall through to directory probe.
                }
            }

            var candidate = Path.Combine( _baseDirectory, assemblyName.Name + ".dll" );
            if ( File.Exists( candidate ) )
            {
                try
                {
                    return LoadFromAssemblyPath( candidate );
                }
                catch
                {
                    // Fall through to default context.
                }
            }
            return null!; // signals fallback to default ALC
        }

        protected override IntPtr LoadUnmanagedDll( string unmanagedDllName )
        {
            // Unmanaged DLL probe via the resolver -- some providers
            // (e.g., MongoDB native runtime) carry platform-specific
            // .so/.dll files in their runtimes/<rid>/native/ folders.
            var path = _resolver.ResolveUnmanagedDllToPath( unmanagedDllName );
            if ( path != null )
                return LoadUnmanagedDllFromPath( path );
            return IntPtr.Zero;
        }
    }

    // Direct NuGet-cache probe. AssemblyDependencyResolver returns null
    // for NuGet packages when the component lacks a runtimeconfig
    // (library projects). This class parses the .deps.json's `libraries`
    // section to find each package's NuGet path (`<name>/<version>`) and
    // the `targets` section to find the runtime-relative DLL path
    // (`lib/<tfm>/<name>.dll`), then composes the absolute path under
    // the global NuGet cache root (~/.nuget/packages by default, or
    // NUGET_PACKAGES if set).
    private sealed class NuGetCacheProbe
    {
        private readonly Dictionary<string, string> _assemblyToPath
            = new( StringComparer.OrdinalIgnoreCase );

        public NuGetCacheProbe( string assemblyPath )
        {
            var depsPath = Path.ChangeExtension( assemblyPath, ".deps.json" );
            if ( !File.Exists( depsPath ) )
                return;

            var nugetRoot = Environment.GetEnvironmentVariable( "NUGET_PACKAGES" );
            if ( string.IsNullOrWhiteSpace( nugetRoot ) )
            {
                var home = Environment.GetFolderPath( Environment.SpecialFolder.UserProfile );
                nugetRoot = Path.Combine( home, ".nuget", "packages" );
            }

            try
            {
                using var fs = File.OpenRead( depsPath );
                using var doc = System.Text.Json.JsonDocument.Parse( fs );
                var root = doc.RootElement;

                // libraries: <pkg>/<ver> -> { type, path }
                var libPaths = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
                if ( root.TryGetProperty( "libraries", out var libraries ) )
                {
                    foreach ( var lib in libraries.EnumerateObject() )
                    {
                        if ( !lib.Value.TryGetProperty( "type", out var typeProp )
                            || typeProp.GetString() != "package" )
                            continue;
                        if ( !lib.Value.TryGetProperty( "path", out var pathProp ) )
                            continue;
                        libPaths[lib.Name] = pathProp.GetString() ?? "";
                    }
                }

                // targets: <tfm> -> <pkg>/<ver> -> { runtime: { <rel-dll>: {...} } }
                if ( !root.TryGetProperty( "targets", out var targets ) )
                    return;

                foreach ( var targetFw in targets.EnumerateObject() )
                {
                    foreach ( var pkg in targetFw.Value.EnumerateObject() )
                    {
                        if ( !libPaths.TryGetValue( pkg.Name, out var pkgPath ) )
                            continue;
                        if ( !pkg.Value.TryGetProperty( "runtime", out var runtime ) )
                            continue;

                        foreach ( var rel in runtime.EnumerateObject() )
                        {
                            var relDllPath = rel.Name; // e.g. "lib/net10.0/Npgsql.dll"
                            var asmName = Path.GetFileNameWithoutExtension( relDllPath );
                            if ( string.IsNullOrEmpty( asmName ) )
                                continue;

                            var absolute = Path.Combine(
                                nugetRoot, pkgPath.Replace( '/', Path.DirectorySeparatorChar ),
                                relDllPath.Replace( '/', Path.DirectorySeparatorChar ) );

                            // First match wins; the .deps.json's first listed
                            // target framework is the runtime target.
                            if ( !_assemblyToPath.ContainsKey( asmName ) )
                                _assemblyToPath[asmName] = absolute;
                        }
                    }

                    // Only process the FIRST target framework (the runtime
                    // target). The deps.json may list multiple.
                    break;
                }
            }
            catch
            {
                // Malformed deps.json or IO failure: probe disabled.
            }
        }

        public string? Resolve( AssemblyName name )
        {
            if ( name.Name == null )
                return null;
            return _assemblyToPath.TryGetValue( name.Name, out var path ) && File.Exists( path )
                ? path
                : null;
        }
    }
}
