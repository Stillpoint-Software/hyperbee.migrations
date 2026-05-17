namespace Hyperbee.Migrations.Resources;

/// <summary>
/// Maps a resource path to its <see cref="ResourceFormat"/>.
/// Per-provider <c>*ResourceRunner</c> types use this to branch between the
/// JSON-array loader (legacy) and the script loader (the recommended form).
/// </summary>
/// <remarks>
/// <para>
/// Recognized forms:
/// <list type="bullet">
///   <item><c>.pql</c> — the recommended multi-statement script form for all
///         providers (Provider Query Language; the grammar itself is
///         provider-specific).</item>
///   <item><c>.sql</c> — Postgres native; equivalent to <c>.pql</c> for the
///         Postgres provider.</item>
///   <item><c>.statements.json</c> (and a bare <c>statements.json</c>) — the
///         legacy v2 JSON-array container, retained for backward
///         compatibility only.</item>
/// </list>
/// </para>
/// <para>
/// The <c>.json</c> branch is evaluated first so a compound
/// <c>*.statements.json</c> (or a bare <c>statements.json</c>) classifies as
/// <see cref="ResourceFormat.JsonArray"/> and never falls through.
/// </para>
/// </remarks>
public static class ResourceFormatDetector
{
    public static ResourceFormat Classify( string resourcePath )
    {
        if ( string.IsNullOrEmpty( resourcePath ) )
            throw new ArgumentException( "Resource path cannot be null or empty.", nameof( resourcePath ) );

        var ext = Path.GetExtension( resourcePath );

        // Legacy JSON-array container. `.statements.json` is a compound
        // extension; check the inner one (or a bare `statements.json`) so it
        // classifies before the script branch.
        if ( ext.Equals( ".json", StringComparison.OrdinalIgnoreCase ) )
        {
            var inner = Path.GetExtension( Path.GetFileNameWithoutExtension( resourcePath ) );
            if ( inner.Equals( ".statements", StringComparison.OrdinalIgnoreCase )
                 || Path.GetFileNameWithoutExtension( resourcePath ).Equals( "statements", StringComparison.OrdinalIgnoreCase ) )
            {
                return ResourceFormat.JsonArray;
            }
        }

        // Recommended script form (.pql, universal) and Postgres-native .sql.
        if ( ext.Equals( ".pql", StringComparison.OrdinalIgnoreCase )
             || ext.Equals( ".sql", StringComparison.OrdinalIgnoreCase ) )
        {
            return ResourceFormat.Script;
        }

        throw new MigrationException(
            $"Unrecognized resource extension on `{resourcePath}`. " +
            "Expected one of: .pql (recommended script form), .sql (Postgres native), " +
            "or .statements.json (legacy JSON-array)." );
    }

    /// <summary>True if <paramref name="path"/> ends with <paramref name="suffix"/>, case-insensitively.</summary>
    public static bool EndsWith( string path, string suffix ) =>
        path.EndsWith( suffix, StringComparison.OrdinalIgnoreCase );
}
