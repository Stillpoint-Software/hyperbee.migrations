namespace Hyperbee.Migrations.Cli;

/// <summary>
/// Minimal long-option arg parser. Recognizes <c>--name value</c> and
/// <c>--name=value</c> patterns; bare positional args go to a residual list.
/// Repeated <c>--name</c> occurrences accumulate (use <see cref="Many"/>
/// to read all values; <see cref="Optional"/>/<see cref="Required"/> read
/// the last value to preserve historical CLI semantics).
/// Designed to keep the CLI free of System.CommandLine's beta surface.
/// </summary>
/// <remarks>
/// When a non-null <see cref="ArgSchema"/> is passed to <see cref="Parse"/>,
/// the parser enforces per-verb whitelisting (R-12 per ADR-0024 audit):
/// unknown long-options throw <see cref="ArgumentException"/> with a
/// did-you-mean suggestion against the schema, and a non-boolean flag
/// missing its value (e.g. <c>--connection --range ...</c>) throws rather
/// than silently being treated as the boolean string <c>"true"</c>.
/// </remarks>
internal sealed class ArgParser
{
    private readonly Dictionary<string, List<string>> _options = new( StringComparer.OrdinalIgnoreCase );
    private readonly List<string> _positional = new();

    private ArgParser() { }

    public static ArgParser Parse( IReadOnlyList<string> args )
        => Parse( args, schema: null );

    public static ArgParser Parse( IReadOnlyList<string> args, ArgSchema? schema )
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
            string? value = null;
            bool hasInlineValue = false;

            var eq = name.IndexOf( '=' );
            if ( eq >= 0 )
            {
                value = name.Substring( eq + 1 );
                name = name.Substring( 0, eq );
                hasInlineValue = true;
            }

            // R-12: validate against the per-verb whitelist before any
            // value-consumption decision. Unknown flags fail loudly with
            // did-you-mean suggestion.
            if ( schema != null && !schema.KnownFlags.Contains( name ) )
            {
                var suggestion = SuggestClosest( name, schema.KnownFlags );
                var known = string.Join( ", ", schema.KnownFlags.OrderBy( x => x, StringComparer.OrdinalIgnoreCase ).Select( k => "--" + k ) );
                var didYouMean = suggestion != null ? $" Did you mean --{suggestion}?" : "";
                throw new ArgumentException(
                    $"unknown flag --{name}.{didYouMean} Known flags: {known}." );
            }

            var isBoolean = schema != null && schema.BooleanFlags.Contains( name );

            if ( !hasInlineValue )
            {
                if ( isBoolean )
                {
                    // Boolean flag: do not consume the next arg even if it is
                    // a non-flag positional. `--flag positional` is two tokens.
                    value = "true";
                }
                else if ( i + 1 < args.Count && !args[i + 1].StartsWith( "--", StringComparison.Ordinal ) )
                {
                    value = args[++i];
                }
                else if ( schema != null )
                {
                    // R-12: a non-boolean flag with no value (next token is
                    // another flag or end-of-args) is an error, not a silent
                    // "true". Previously masked typos like
                    // `--connection --range 1-2` -> connection="true".
                    throw new ArgumentException(
                        $"flag --{name} requires a value." );
                }
                else
                {
                    // Schema-less back-compat path: keep the historical
                    // "treat missing value as true" semantic so test fixtures
                    // that don't supply a schema continue to compile.
                    value = "true";
                }
            }

            if ( !parser._options.TryGetValue( name, out var list ) )
            {
                list = new List<string>();
                parser._options[name] = list;
            }
            list.Add( value! );
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

    // ---- did-you-mean ------------------------------------------------------

    // Damerau-Levenshtein-lite: classic edit-distance with no transposition
    // (transposition adds complexity for negligible gain on short CLI flag
    // names). Suggest only when the closest candidate is within ~1/3 of the
    // input length -- avoids "did you mean --x?" for genuinely-unrelated input.
    private static string? SuggestClosest( string input, IReadOnlySet<string> candidates )
    {
        string? best = null;
        var bestDistance = int.MaxValue;
        foreach ( var candidate in candidates )
        {
            var d = EditDistance( input, candidate );
            if ( d < bestDistance )
            {
                bestDistance = d;
                best = candidate;
            }
        }

        // Threshold: at most ceil(len/3) edits for a useful suggestion. For
        // 3-char inputs that means distance 1; for 9-char inputs distance 3.
        // Avoids matching `--abc` to a 14-char flag.
        var threshold = Math.Max( 1, (input.Length + 2) / 3 );
        return best != null && bestDistance <= threshold ? best : null;
    }

    private static int EditDistance( string a, string b )
    {
        if ( a.Length == 0 ) return b.Length;
        if ( b.Length == 0 ) return a.Length;

        var prev = new int[b.Length + 1];
        var curr = new int[b.Length + 1];

        for ( var j = 0; j <= b.Length; j++ )
            prev[j] = j;

        for ( var i = 1; i <= a.Length; i++ )
        {
            curr[0] = i;
            for ( var j = 1; j <= b.Length; j++ )
            {
                var cost = char.ToLowerInvariant( a[i - 1] ) == char.ToLowerInvariant( b[j - 1] ) ? 0 : 1;
                curr[j] = Math.Min(
                    Math.Min( curr[j - 1] + 1, prev[j] + 1 ),
                    prev[j - 1] + cost );
            }
            (prev, curr) = (curr, prev);
        }

        return prev[b.Length];
    }
}

/// <summary>
/// Per-verb flag whitelist consumed by <see cref="ArgParser.Parse(IReadOnlyList{string},ArgSchema?)"/>.
/// Each verb declares its known flags + which are boolean (value-less)
/// so unknown flags fail loudly with a did-you-mean suggestion and a
/// non-boolean flag missing its value cannot be silently coerced to
/// <c>"true"</c>.
/// </summary>
internal sealed class ArgSchema
{
    public required HashSet<string> KnownFlags { get; init; }
    public required HashSet<string> BooleanFlags { get; init; }

    public static ArgSchema Of( IEnumerable<string> knownFlags, IEnumerable<string> booleanFlags )
        => new()
        {
            KnownFlags = new HashSet<string>( knownFlags, StringComparer.OrdinalIgnoreCase ),
            BooleanFlags = new HashSet<string>( booleanFlags, StringComparer.OrdinalIgnoreCase )
        };
}
