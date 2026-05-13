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
        _context = new CollectibleLoadContext( _baseDirectory );
        Assembly = _context.LoadFromAssemblyPath( fullPath );
    }

    public void Dispose()
    {
        _context.Unload();
    }

    private sealed class CollectibleLoadContext : AssemblyLoadContext
    {
        private readonly string _baseDirectory;

        public CollectibleLoadContext( string baseDirectory )
            : base( name: Path.GetFileNameWithoutExtension( baseDirectory ) + ":migration-assembly", isCollectible: true )
        {
            _baseDirectory = baseDirectory;
        }

        protected override Assembly Load( AssemblyName assemblyName )
        {
            // Probe the migration project's output directory for the
            // requested assembly. If it sits next to the migration DLL
            // (typical -- provider packages copy alongside the main
            // assembly during `dotnet build`), load it into this ALC.
            // Otherwise fall back to the Default context so framework /
            // SDK assemblies resolve normally.
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
    }
}
