namespace Hyperbee.Migrations.Cli;

/// <summary>
/// Minimal long-option arg parser. Recognizes <c>--name value</c> and
/// <c>--name=value</c> patterns; bare positional args go to a residual list.
/// Repeated <c>--name</c> occurrences accumulate (use <see cref="Many"/>
/// to read all values; <see cref="Optional"/>/<see cref="Required"/> read
/// the last value to preserve historical CLI semantics).
/// Designed to keep the CLI free of System.CommandLine's beta surface.
/// </summary>
internal sealed class ArgParser
{
    private readonly Dictionary<string, List<string>> _options = new( StringComparer.OrdinalIgnoreCase );
    private readonly List<string> _positional = new();

    private ArgParser() { }

    public static ArgParser Parse( IReadOnlyList<string> args )
    {
        var parser = new ArgParser();

        for ( var i = 0; i < args.Count; i++ )
        {
            var arg = args[i];

            if ( !arg.StartsWith( "--", StringComparison.Ordinal ) )
            {
                parser._positional.Add( arg );
                continue;
            }

            var name = arg.Substring( 2 );
            string value;

            var eq = name.IndexOf( '=' );
            if ( eq >= 0 )
            {
                value = name.Substring( eq + 1 );
                name = name.Substring( 0, eq );
            }
            else if ( i + 1 < args.Count && !args[i + 1].StartsWith( "--", StringComparison.Ordinal ) )
            {
                value = args[++i];
            }
            else
            {
                value = "true"; // boolean flag with no value
            }

            if ( !parser._options.TryGetValue( name, out var list ) )
            {
                list = new List<string>();
                parser._options[name] = list;
            }
            list.Add( value );
        }

        return parser;
    }

    public string Required( string name )
    {
        var v = LastValue( name );
        if ( string.IsNullOrWhiteSpace( v ) )
            throw new ArgumentException( $"--{name} is required." );
        return v;
    }

    public string? Optional( string name, string? fallback = null )
    {
        var v = LastValue( name );
        return v ?? fallback;
    }

    public bool HasFlag( string name )
    {
        var v = LastValue( name );
        return v != null
            && (string.IsNullOrEmpty( v )
                || string.Equals( v, "true", StringComparison.OrdinalIgnoreCase ));
    }

    /// <summary>All values supplied for <paramref name="name"/>, in order. Empty when not set.</summary>
    public IReadOnlyList<string> Many( string name ) =>
        _options.TryGetValue( name, out var list ) ? list : Array.Empty<string>();

    public IReadOnlyList<string> Positional => _positional;

    private string? LastValue( string name ) =>
        _options.TryGetValue( name, out var list ) && list.Count > 0 ? list[^1] : null;

    public static (long FromVersion, long ToVersion) ParseRange( string range )
    {
        if ( string.IsNullOrWhiteSpace( range ) )
            throw new ArgumentException( "--range is required (format: <fromVersion>-<toVersion>)." );

        var dash = range.IndexOf( '-' );
        if ( dash <= 0 || dash >= range.Length - 1 )
            throw new ArgumentException( $"--range '{range}' is not in 'fromVersion-toVersion' format." );

        if ( !long.TryParse( range.Substring( 0, dash ), out var from )
             || !long.TryParse( range.Substring( dash + 1 ), out var to ) )
        {
            throw new ArgumentException( $"--range '{range}' endpoints must be integers." );
        }

        if ( to < from )
            throw new ArgumentException( $"--range '{range}': end ({to}) is less than start ({from})." );

        return (from, to);
    }
}
