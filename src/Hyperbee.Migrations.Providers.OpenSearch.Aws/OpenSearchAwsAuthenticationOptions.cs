#nullable enable
using Amazon.Runtime;

namespace Hyperbee.Migrations.Providers.OpenSearch.Aws;

// R-21 — AWS SigV4 auth options for OpenSearch on AWS Managed Service.
//
// The signer obtains credentials per request via AWSCredentials.GetCredentials(),
// so any AWSCredentials implementation that resolves fresh credentials per call
// (InstanceProfileAWSCredentials, EnvironmentVariablesAWSCredentials,
// FallbackCredentialsFactory.GetCredentials() — the default chain) automatically
// satisfies R-21 #4 (per-request credential resolution for IRSA / instance-profile
// rotation). No extra plumbing required at the provider layer.

public sealed class OpenSearchAwsAuthenticationOptions
{
    /// <summary>
    /// AWS region the cluster is deployed in (e.g., <c>"us-east-1"</c>). Required.
    /// </summary>
    public string? Region { get; set; }

    /// <summary>
    /// AWS service code used for the SigV4 signature.
    /// Default <c>"es"</c> for Amazon OpenSearch Service domains.
    /// Use <c>"aoss"</c> for OpenSearch Serverless collections.
    /// </summary>
    public string Service { get; set; } = "es";

    /// <summary>
    /// AWS credentials provider. When null (the default), the standard
    /// <see cref="FallbackCredentialsFactory"/> chain is used — which resolves
    /// in this order: explicit profile, environment variables, ECS task role,
    /// EC2 instance profile, IAM Identity Center / SSO, IRSA. Production
    /// deployments typically leave this null and rely on instance-profile or
    /// IRSA credentials supplied by the runtime environment.
    ///
    /// Set explicitly for tests or for unusual setups where the host needs
    /// to use credentials other than the ambient AWS chain (e.g., assume-role
    /// + STS session credentials passed as <see cref="SessionAWSCredentials"/>).
    /// </summary>
    public AWSCredentials? Credentials { get; set; }
}
