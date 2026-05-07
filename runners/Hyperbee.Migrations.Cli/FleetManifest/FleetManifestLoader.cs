using System.Text.RegularExpressions;
using Hyperbee.Migrations.Squash;
using YamlDotNet.Serialization;
using YamlDotNet.Serialization.NamingConventions;

namespace Hyperbee.Migrations.Cli.FleetManifest;

/// <summary>
/// Loads and validates a fleet manifest YAML file (per ADR-0019 A9). Performs:
/// <list type="bullet">
///   <item>YAML deserialization with kebab-case property mapping
///         (<c>ticket-id</c> → <c>TicketId</c> etc.).</item>
///   <item>Environment-variable substitution on connection strings —
///         <c>${NAME}</c> tokens are replaced with values from the process
///         environment; missing variables raise a clear error.</item>
///   <item>Stranding-entry validation per A9 / A15:
///         ticket-id regex, owner non-empty, reason ≥ 20 chars,
///         expires ≤ 90 days from now (default 30 days when omitted).</item>
/// </list>
/// </summary>
public static class FleetManifestLoader
{
    private static readonly Regex EnvVarReference = new( @"\$\{(?<name>[A-Z_][A-Z0-9_]*)\}", RegexOptions.Compiled );
    private static readonly Regex TicketIdPattern = new( @"^[A-Za-z0-9_\-]{3,64}$", RegexOptions.Compiled );

    private static readonly TimeSpan MaxExpiryWindow = TimeSpan.FromDays( 90 );
    private static readonly TimeSpan DefaultExpiryWindow = TimeSpan.FromDays( 30 );

    public static FleetManifestModel LoadFromFile( string path )
    {
        if ( string.IsNullOrWhiteSpace( path ) )
            throw new ArgumentException( "fleet manifest path is required.", nameof( path ) );
        if ( !File.Exists( path ) )
            throw new FileNotFoundException( $"fleet manifest not found: {path}", path );

        var yaml = File.ReadAllText( path );
        return LoadFromString( yaml );
    }

    public static FleetManifestModel LoadFromString( string yaml )
    {
        if ( string.IsNullOrWhiteSpace( yaml ) )
            throw new ArgumentException( "fleet manifest yaml is empty.", nameof( yaml ) );

        var deserializer = new DeserializerBuilder()
            .WithNamingConvention( HyphenatedNamingConvention.Instance )
            .IgnoreUnmatchedProperties()
            .Build();

        FleetManifestModel? model;
        try
        {
            model = deserializer.Deserialize<FleetManifestModel>( yaml );
        }
        catch ( Exception ex )
        {
            throw new MigrationException( $"fleet manifest YAML is malformed: {ex.Message}", ex );
        }

        if ( model == null )
            throw new MigrationException( "fleet manifest deserialized to null." );

        ValidateAndNormalize( model );
        return model;
    }

    /// <summary>
    /// Project the parsed manifest's <c>squash-overrides.accept-stranding</c>
    /// entries into the <see cref="SquashOverrideEntry"/> shape that the
    /// runtime fleet gate consumes.
    /// </summary>
    public static IReadOnlyList<SquashOverrideEntry> BuildOverrideEntries( FleetManifestModel model, DateTimeOffset now )
    {
        var entries = new List<SquashOverrideEntry>();
        if ( model.SquashOverrides?.AcceptStranding == null )
            return entries;

        foreach ( var raw in model.SquashOverrides.AcceptStranding )
        {
            var expires = ParseExpiry( raw.Expires, now );
            entries.Add( new SquashOverrideEntry
            {
                EnvironmentName = raw.Environment,
                TicketId = raw.TicketId,
                Owner = raw.Owner,
                Reason = raw.Reason,
                Expires = expires
            } );
        }

        return entries;
    }

    private static void ValidateAndNormalize( FleetManifestModel model )
    {
        if ( model.Fleet.Count == 0 )
            throw new MigrationException( "fleet manifest has no `fleet:` entries." );

        var seenNames = new HashSet<string>( StringComparer.OrdinalIgnoreCase );
        foreach ( var env in model.Fleet )
        {
            if ( string.IsNullOrWhiteSpace( env.Name ) )
                throw new MigrationException( "fleet entry is missing `name`." );
            if ( !seenNames.Add( env.Name ) )
                throw new MigrationException( $"duplicate fleet entry name: `{env.Name}`." );
            if ( string.IsNullOrWhiteSpace( env.Connection ) )
                throw new MigrationException( $"fleet entry `{env.Name}` is missing `connection`." );

            env.Connection = SubstituteEnvironmentVariables( env.Connection, env.Name );
        }

        if ( model.SquashOverrides?.AcceptStranding == null )
            return;

        var now = DateTimeOffset.UtcNow;
        foreach ( var entry in model.SquashOverrides.AcceptStranding )
        {
            if ( string.IsNullOrWhiteSpace( entry.Environment ) )
                throw new MigrationException( "accept-stranding entry is missing `environment`." );
            if ( !seenNames.Contains( entry.Environment ) )
                throw new MigrationException(
                    $"accept-stranding entry references unknown environment `{entry.Environment}` " +
                    $"(known: [{string.Join( ", ", seenNames )}])." );

            if ( !TicketIdPattern.IsMatch( entry.TicketId ?? "" ) )
                throw new MigrationException(
                    $"accept-stranding[{entry.Environment}].ticket-id `{entry.TicketId}` is invalid " +
                    "(expected 3-64 alphanumeric / dash / underscore characters)." );

            if ( string.IsNullOrWhiteSpace( entry.Owner ) )
                throw new MigrationException( $"accept-stranding[{entry.Environment}].owner is required." );

            var trimmedReason = (entry.Reason ?? "").Trim();
            if ( trimmedReason.Length < 20 )
                throw new MigrationException(
                    $"accept-stranding[{entry.Environment}].reason must be >= 20 characters." );

            // Validate expires (parse + cap), normalize to ISO-8601 in the model.
            var expiry = ParseExpiry( entry.Expires, now );
            entry.Expires = expiry.ToString( "O" );
        }
    }

    private static DateTimeOffset ParseExpiry( string? raw, DateTimeOffset now )
    {
        DateTimeOffset expires;
        if ( string.IsNullOrWhiteSpace( raw ) )
        {
            expires = now + DefaultExpiryWindow;
        }
        else if ( !DateTimeOffset.TryParse( raw, System.Globalization.CultureInfo.InvariantCulture,
                    System.Globalization.DateTimeStyles.AssumeUniversal | System.Globalization.DateTimeStyles.AdjustToUniversal,
                    out expires ) )
        {
            throw new MigrationException(
                $"accept-stranding entry has invalid `expires` value `{raw}` (expected ISO-8601, e.g. 2026-06-01)." );
        }

        var window = expires - now;
        if ( window > MaxExpiryWindow )
            throw new MigrationException(
                $"accept-stranding entry expires {window.TotalDays:F1} days from now; " +
                $"max-expiry-window per ADR-0019 A15 is {MaxExpiryWindow.TotalDays:F0} days." );

        if ( window < TimeSpan.Zero )
            throw new MigrationException(
                $"accept-stranding entry already expired ({raw}); review and renew." );

        return expires;
    }

    private static string SubstituteEnvironmentVariables( string template, string contextLabel )
    {
        var missing = new List<string>();

        var result = EnvVarReference.Replace( template, m =>
        {
            var name = m.Groups["name"].Value;
            var value = Environment.GetEnvironmentVariable( name );
            if ( string.IsNullOrEmpty( value ) )
            {
                missing.Add( name );
                return m.Value; // preserve token; we'll fail below
            }
            return value;
        } );

        if ( missing.Count > 0 )
            throw new MigrationException(
                $"fleet entry `{contextLabel}` connection references unset environment variable(s): " +
                $"[{string.Join( ", ", missing.Distinct() )}]. Set them in your shell or CI secrets and retry." );

        return result;
    }
}
