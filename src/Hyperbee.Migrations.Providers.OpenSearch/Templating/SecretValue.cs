using System.Security.Cryptography;
using System.Text;

namespace Hyperbee.Migrations.Providers.OpenSearch.Templating;

// Phase 0 scaffolding for R-10/R-25 secret-aware rendering.
// Carries the secret value plus an interned content hash that the Phase 6
// SecretScrubber log sink wrapper will use to redact matches in log/exception
// output regardless of which scope the secret originated from.
public readonly struct SecretValue : IEquatable<SecretValue>
{
    public string Value { get; }
    public string ContentHash { get; }

    public SecretValue( string value )
    {
        Value = value ?? string.Empty;
        ContentHash = ComputeHash( Value );
    }

    private static string ComputeHash( string value )
    {
        if ( string.IsNullOrEmpty( value ) )
            return string.Empty;

        var bytes = Encoding.UTF8.GetBytes( value );
        var hash = SHA256.HashData( bytes );
        return string.Intern( Convert.ToHexString( hash ) );
    }

    public bool Equals( SecretValue other )
        => string.Equals( ContentHash, other.ContentHash, StringComparison.Ordinal )
           && string.Equals( Value, other.Value, StringComparison.Ordinal );

    public override bool Equals( object obj )
        => obj is SecretValue other && Equals( other );

    public override int GetHashCode()
        => ContentHash?.GetHashCode( StringComparison.Ordinal ) ?? 0;

    public static bool operator ==( SecretValue left, SecretValue right ) => left.Equals( right );
    public static bool operator !=( SecretValue left, SecretValue right ) => !left.Equals( right );

    // Per R-25, callers should not use ToString() for log output. Phase 6
    // SecretScrubber will scrub by content hash if a secret value escapes anyway.
    public override string ToString() => "***SECRET***";
}
