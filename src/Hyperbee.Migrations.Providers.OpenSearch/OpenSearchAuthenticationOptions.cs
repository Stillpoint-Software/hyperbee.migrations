#nullable enable
using System.Security.Cryptography.X509Certificates;

namespace Hyperbee.Migrations.Providers.OpenSearch;

// R-21 — auth modes the core package supports.
//
// SigV4 is intentionally NOT in this enum: it ships in the optional
// OpenSearch.Net.Auth.AwsSigV4 package and is registered via a separate
// opt-in extension (plan task 3.2). The core package stays free of the
// AWS-SDK transitive dependency tree for users who don't deploy on AWS.

public enum OpenSearchAuthenticationMode
{
    /// <summary>No authentication. Acceptable for local dev clusters with the security plugin disabled.</summary>
    Anonymous,

    /// <summary>HTTP Basic auth — username + password.</summary>
    Basic,

    /// <summary>OpenSearch security-plugin API key — id + key pair.</summary>
    ApiKey,

    /// <summary>Mutual TLS — client certificate (path or X509 instance).</summary>
    ClientCertificate
}

// Auth configuration for the OpenSearch client. Populate the mode-relevant
// fields and call AddOpenSearchClient; field validation is performed at
// client-build time so missing required fields fail at startup with a clear
// error naming the mode and the missing field.
//
// Mode = Anonymous is acceptable but logged as WARN — production deployments
// should never be anonymous, and a startup warning is the cheapest forcing
// function.

public sealed class OpenSearchAuthenticationOptions
{
    public OpenSearchAuthenticationMode Mode { get; set; } = OpenSearchAuthenticationMode.Anonymous;

    // --- Basic ---
    public string? UserName { get; set; }
    public string? Password { get; set; }

    // --- ApiKey ---
    /// <summary>API key id; resolves to the username component of the Authorization header.</summary>
    public string? ApiKeyId { get; set; }
    /// <summary>API key secret value.</summary>
    public string? ApiKey { get; set; }

    // --- ClientCertificate (mTLS) ---
    /// <summary>Path to a PFX/PKCS12 client certificate file. Mutually exclusive with ClientCertificate.</summary>
    public string? ClientCertificatePath { get; set; }

    /// <summary>Password protecting the PFX, if any.</summary>
    public string? ClientCertificatePassword { get; set; }

    /// <summary>Pre-loaded X509Certificate instance. Mutually exclusive with ClientCertificatePath.</summary>
    public X509Certificate? ClientCertificate { get; set; }

    /// <summary>
    /// Validates that the populated fields are coherent for the selected mode.
    /// Throws OpenSearchProviderException with a remediation message on the
    /// first violation. Designed to be called once at client-build time so
    /// startup is the failure surface, not the first wire request.
    /// </summary>
    public void Validate()
    {
        switch ( Mode )
        {
            case OpenSearchAuthenticationMode.Anonymous:
                // Anonymous is valid; the AddOpenSearchClient extension logs WARN.
                break;

            case OpenSearchAuthenticationMode.Basic:
                if ( string.IsNullOrEmpty( UserName ) )
                    throw new OpenSearchProviderException(
                        "Authentication.Mode = Basic requires Authentication.UserName. Set OpenSearch:Authentication:UserName in configuration." );
                // Allow empty password: some test fixtures use empty-password setups.
                break;

            case OpenSearchAuthenticationMode.ApiKey:
                if ( string.IsNullOrEmpty( ApiKeyId ) )
                    throw new OpenSearchProviderException(
                        "Authentication.Mode = ApiKey requires Authentication.ApiKeyId. Set OpenSearch:Authentication:ApiKeyId in configuration." );
                if ( string.IsNullOrEmpty( ApiKey ) )
                    throw new OpenSearchProviderException(
                        "Authentication.Mode = ApiKey requires Authentication.ApiKey. Set OpenSearch:Authentication:ApiKey in configuration (prefer user-secrets / env vars in production)." );
                break;

            case OpenSearchAuthenticationMode.ClientCertificate:
                var hasPath = !string.IsNullOrEmpty( ClientCertificatePath );
                var hasInstance = ClientCertificate is not null;
                if ( !hasPath && !hasInstance )
                    throw new OpenSearchProviderException(
                        "Authentication.Mode = ClientCertificate requires either Authentication.ClientCertificatePath OR Authentication.ClientCertificate. Set OpenSearch:Authentication:ClientCertificatePath in configuration." );
                if ( hasPath && hasInstance )
                    throw new OpenSearchProviderException(
                        "Authentication.Mode = ClientCertificate has BOTH ClientCertificatePath and ClientCertificate set. Provide exactly one." );
                if ( hasPath && !File.Exists( ClientCertificatePath ) )
                    throw new OpenSearchProviderException(
                        $"Authentication.ClientCertificatePath `{ClientCertificatePath}` does not exist or is not readable. Verify the path is absolute or relative to the runner's working directory." );
                break;

            default:
                throw new OpenSearchProviderException(
                    $"Authentication.Mode `{Mode}` is not recognized. Valid modes: Anonymous, Basic, ApiKey, ClientCertificate." );
        }
    }
}
