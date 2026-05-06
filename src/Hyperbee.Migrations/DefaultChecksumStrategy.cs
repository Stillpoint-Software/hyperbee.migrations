using System.Security.Cryptography;
using System.Text;

namespace Hyperbee.Migrations;

/// <summary>
/// Code-only fallback checksum: SHA-256 over <c>FullName ‖ "@" ‖ Version</c>
/// of the migration type. Documented as weaker than a resource-bytes hash —
/// any change to the migration's <c>UpAsync</c> body that doesn't change the
/// type name or version will produce the same digest. Per-provider overrides
/// add resource-bytes hashing on top for resource-based migrations
/// (see ADR-0021 "Default checksum strategy").
/// </summary>
public sealed class DefaultChecksumStrategy : IChecksumStrategy
{
    public Task<string> ComputeAsync(
        Migration migration,
        MigrationAttribute attribute,
        CancellationToken cancellationToken = default )
    {
        if ( migration == null )
            throw new ArgumentNullException( nameof( migration ) );
        if ( attribute == null )
            throw new ArgumentNullException( nameof( attribute ) );

        var typeName = migration.GetType().FullName ?? migration.GetType().Name;
        var input = Encoding.UTF8.GetBytes( $"{typeName}@{attribute.Version}" );

        var digest = SHA256.HashData( input );
        return Task.FromResult( Convert.ToHexString( digest ).ToLowerInvariant() );
    }
}
