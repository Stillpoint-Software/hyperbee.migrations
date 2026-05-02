using Hyperbee.Templating.Configure;
using Hyperbee.Templating.Text;

namespace Hyperbee.Migrations.Providers.OpenSearch.Templating;

// Phase 0 spike (Task 0.4): Wraps Hyperbee.Templating's `Template.Render`
// entry point and exposes the four R-10 scopes (`env`, `config`, `runtime`,
// `secrets`) as templating variables prefixed by scope name (e.g.
// `{{config.indexPrefix}}` resolves `indexPrefix` from the `config` dict).
//
// Per ADR-0015, this runs BEFORE the parser. Rendering is offline-pure and
// performs no network I/O. Per R-10, secret values are wrapped in
// `SecretMarker` so the Phase 6 `SecretScrubber` log-sink wrapper can
// identify them by content hash regardless of source scope.
public sealed class OpenSearchResourceTemplateRenderer
{
    private const string EnvScope = "env";
    private const string ConfigScope = "config";
    private const string RuntimeScope = "runtime";
    private const string SecretsScope = "secrets";

    private readonly IReadOnlyDictionary<string, string> _env;
    private readonly IReadOnlyDictionary<string, string> _config;
    private readonly IReadOnlyDictionary<string, string> _runtime;
    private readonly IReadOnlyDictionary<string, SecretValue> _secrets;
    private readonly IReadOnlyDictionary<string, SecretMarker> _secretMarkers;

    public OpenSearchResourceTemplateRenderer(
        IReadOnlyDictionary<string, string> env,
        IReadOnlyDictionary<string, string> config,
        IReadOnlyDictionary<string, string> runtime,
        IReadOnlyDictionary<string, SecretValue> secrets )
    {
        _env = env ?? new Dictionary<string, string>();
        _config = config ?? new Dictionary<string, string>();
        _runtime = runtime ?? new Dictionary<string, string>();
        _secrets = secrets ?? new Dictionary<string, SecretValue>();

        var markers = new Dictionary<string, SecretMarker>( StringComparer.OrdinalIgnoreCase );
        foreach ( var kvp in _secrets )
            markers[kvp.Key] = new SecretMarker( kvp.Value );

        _secretMarkers = markers;
    }

    // Returns SecretMarker instances for the registered secrets so callers can
    // inspect the wrapped values once Phase 6 lands the scrubber pipeline.
    public IReadOnlyDictionary<string, SecretMarker> SecretMarkers => _secretMarkers;

    public string Render( string template )
    {
        ArgumentNullException.ThrowIfNull( template );

        var options = BuildOptions();
        return Template.Render( template, options );
    }

    private TemplateOptions BuildOptions()
    {
        var variables = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );

        Merge( variables, EnvScope, _env, value => value );
        Merge( variables, ConfigScope, _config, value => value );
        Merge( variables, RuntimeScope, _runtime, value => value );

        // Phase 0: secrets render their literal value into the output. Phase 6
        // will introduce the SecretScrubber log-sink wrapper that redacts these
        // values from logs and exception messages by content hash.
        foreach ( var kvp in _secrets )
            variables[$"{SecretsScope}.{kvp.Key}"] = kvp.Value.Value ?? string.Empty;

        var options = new TemplateOptions( variables )
        {
            // The default validator forbids '.' in identifiers. Override so
            // dotted scope-prefixed keys (`config.indexPrefix`) round-trip.
            Validator = IsValidScopedKey,
        };

        return options;
    }

    private static void Merge<TValue>(
        IDictionary<string, string> target,
        string scope,
        IReadOnlyDictionary<string, TValue> source,
        Func<TValue, string> select )
    {
        if ( source == null )
            return;

        foreach ( var kvp in source )
            target[$"{scope}.{kvp.Key}"] = select( kvp.Value ) ?? string.Empty;
    }

    // Allows scope-prefixed identifiers like `config.indexPrefix` and the
    // bracket-suffix form `runtime.nodes[0]` used for ordered collections.
    // Mirrors Hyperbee.Templating's default rules but admits a single '.' that
    // joins two valid sub-identifiers.
    internal static bool IsValidScopedKey( ReadOnlySpan<char> key )
    {
        if ( key.IsEmpty || !char.IsLetter( key[0] ) )
            return false;

        var sawBracket = false;

        for ( var i = 1; i < key.Length; i++ )
        {
            var c = key[i];

            if ( c == '.' )
            {
                // dot must be followed by a letter that begins the next segment
                if ( sawBracket )
                    return false;
                if ( i + 1 >= key.Length || !char.IsLetter( key[i + 1] ) )
                    return false;
                continue;
            }

            if ( c == '[' )
            {
                if ( ++i >= key.Length || !char.IsDigit( key[i] ) )
                    return false;

                while ( i < key.Length && char.IsDigit( key[i] ) )
                    i++;

                if ( i >= key.Length || key[i] != ']' )
                    return false;

                if ( i != key.Length - 1 )
                    return false;

                sawBracket = true;
                continue;
            }

            if ( !char.IsLetterOrDigit( c ) && c != '_' )
                return false;
        }

        return true;
    }
}
