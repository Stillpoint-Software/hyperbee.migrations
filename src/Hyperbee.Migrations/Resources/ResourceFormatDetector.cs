namespace Hyperbee.Migrations.Resources;

/// <summary>
/// Maps a resource path to its <see cref="ResourceFormat"/> per ADR-0022.
/// Per-provider <c>*ResourceRunner</c> types use this to branch between the
/// JSON-array loader (legacy) and the script loader (new).
/// </summary>
/// <remarks>
/// Extension precedence: <c>.statements.json</c> takes priority over
/// <c>.statements</c> so the longer-suffix match wins (e.g., a hypothetical
/// <c>foo.statements.json</c> classifies as JsonArray, not Script).
/// </remarks>
public static class ResourceFormatDetector
{
    public static ResourceFormat Classify( string resourcePath )
    {
        if ( string.IsNullOrEmpty( resourcePath ) )
            throw new ArgumentException( "Resource path cannot be null or empty.", nameof( resourcePath ) );

        // .statements.json is a compound extension; check the inner one first so a
        // bare `statements.json` (no leading dot) classifies correctly.
        var ext = Path.GetExtension( resourcePath );

        if ( ext.Equals( ".json", StringComparison.OrdinalIgnoreCase ) )
        {
            var inner = Path.GetExtension( Path.GetFileNameWithoutExtension( resourcePath ) );
            if ( inner.Equals( ".statements", StringComparison.OrdinalIgnoreCase )
                 || Path.GetFileNameWithoutExtension( resourcePath ).Equals( "statements", StringComparison.OrdinalIgnoreCase ) )
            {
                return ResourceFormat.JsonArray;
            }
        }

        if ( ext.Equals( ".statements", StringComparison.OrdinalIgnoreCase )
             || ext.Equals( ".sql", StringComparison.OrdinalIgnoreCase ) )
        {
            return ResourceFormat.Script;
        }

        throw new MigrationException(
            $"Unrecognized resource extension on `{resourcePath}`. " +
            "Expected one of: .statements.json (legacy JSON-array), .statements (script form), .sql (Postgres native)." );
    }

    /// <summary>True if <paramref name="path"/> ends with <paramref name="suffix"/>, case-insensitively.</summary>
    public static bool EndsWith( string path, string suffix ) =>
        path.EndsWith( suffix, StringComparison.OrdinalIgnoreCase );
}
