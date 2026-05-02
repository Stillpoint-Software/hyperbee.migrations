namespace Hyperbee.Migrations.Providers.OpenSearch.Templating;

// Phase 0 scaffolding for R-10/R-25 secret-aware rendering.
// Wraps a rendered string that originated from the `secrets` scope so that
// downstream pipeline code can identify the value as secret-bearing.
//
// Per the design (last-moment unwrap), ToString() returns the literal value
// for HTTP dispatch. The Phase 6 SecretScrubber log-sink wrapper uses
// ContentHash to redact occurrences in logs and exception messages.
public readonly struct SecretMarker : IEquatable<SecretMarker>
{
    public string Value { get; }
    public string ContentHash { get; }

    public SecretMarker( SecretValue secret )
    {
        Value = secret.Value;
        ContentHash = secret.ContentHash;
    }

    public SecretMarker( string value, string contentHash )
    {
        Value = value ?? string.Empty;
        ContentHash = contentHash ?? string.Empty;
    }

    public bool Equals( SecretMarker other )
        => string.Equals( ContentHash, other.ContentHash, StringComparison.Ordinal )
           && string.Equals( Value, other.Value, StringComparison.Ordinal );

    public override bool Equals( object obj )
        => obj is SecretMarker other && Equals( other );

    public override int GetHashCode()
        => ContentHash?.GetHashCode( StringComparison.Ordinal ) ?? 0;

    public static bool operator ==( SecretMarker left, SecretMarker right ) => left.Equals( right );
    public static bool operator !=( SecretMarker left, SecretMarker right ) => !left.Equals( right );

    // Last-moment unwrap for HTTP dispatch per the design.
    // The Phase 6 SecretScrubber wraps the log sink, not this type's ToString().
    public override string ToString() => Value ?? string.Empty;
}
