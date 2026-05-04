# Hyperbee Migrations OpenSearch Provider — AWS SigV4 Extension

Optional opt-in AWS authentication for [Hyperbee.Migrations.Providers.OpenSearch](../Hyperbee.Migrations.Providers.OpenSearch/README.md). Adds SigV4 request signing for AWS Managed OpenSearch Service domains and OpenSearch Serverless collections (R-21).

The core provider package stays free of the AWSSDK transitive dependency tree; consumers running on AWS reference this extension explicitly. Non-AWS deployments use core only.

## Installation

```xml
<PackageReference Include="Hyperbee.Migrations.Providers.OpenSearch.Aws" Version="..." />
```

This brings in `OpenSearch.Net.Auth.AwsSigV4`, which transitively brings AWSSDK.Core.

## Usage

```csharp
services.AddOpenSearchAwsClient( new Uri( "https://my-domain.us-east-1.es.amazonaws.com" ), opts =>
{
    opts.Region = "us-east-1";
    opts.Service = "es";    // "aoss" for OpenSearch Serverless collections
} );

services.AddOpenSearchMigrations( /* migration options */ );
```

Or from `IConfiguration`:

```csharp
services.AddOpenSearchAwsClient( configuration );
```

```jsonc
{
  "OpenSearch": {
    "ConnectionString": "https://my-domain.us-east-1.es.amazonaws.com",
    "Authentication": {
      "Region": "us-east-1",
      "Service": "es"
    }
  }
}
```

## Mutual exclusion with the core client

`AddOpenSearchAwsClient` (this package) and `AddOpenSearchClient` (core package) are **mutually exclusive** — call exactly one. Both check whether an `IOpenSearchClient` is already registered and throw a clear error if so. There is no implicit override and no marker dance; calling both is a misconfiguration that surfaces loudly at startup.

The boundary tracks an actual technical seam: header-based auth (Basic, ApiKey, mTLS, Anonymous in core) configures `ConnectionSettings`; SigV4 (this extension) replaces the HTTP transport layer (`AwsSigV4HttpConnection`). Putting them in different packages respects that seam.

## Credential resolution (R-21 #4)

By default, this extension uses the standard AWS credential chain via `Amazon.Runtime.FallbackCredentialsFactory.GetCredentials()`. Resolution order: explicit profile, environment variables, ECS task role, EC2 instance profile, IAM Identity Center / SSO, IRSA.

Per R-21 #4, credentials are resolved **per request** — `AwsSigV4HttpConnection` calls `AWSCredentials.GetCredentials()` on every signing operation. IRSA and instance-profile rotation work without a runner restart. There is no client-construction-time caching of credentials.

To use credentials other than the ambient chain (typically for tests or assume-role + STS scenarios), set `Options.Credentials` to an explicit `AWSCredentials` instance:

```csharp
services.AddOpenSearchAwsClient( endpoint, opts =>
{
    opts.Region = "us-east-1";
    opts.Credentials = new BasicAWSCredentials( accessKey, secretKey );  // tests only
} );
```

## AWS endpoint loud-fail (R-21 #2)

If the configured endpoint hostname ends with `.amazonaws.com` and the operator forgot to reference this package, core's `AddOpenSearchClient` throws `AwsSigV4NotConfiguredException` at startup with the exact `services.AddOpenSearchAwsClient(...)` snippet to add. Detection is a pure URL string check — no DI introspection across packages, no runtime probing.

The inverse case (this extension wired against a non-AWS endpoint) emits a WARN at client-build time so the misconfiguration class "forgot to point at the AWS host" surfaces visibly without blocking the legitimate edge case (custom domains, sigv4-compatible proxies).

## Service codes

| Cluster type | `Service` |
|---|---|
| Amazon OpenSearch Service domain | `"es"` (default) |
| OpenSearch Serverless collection | `"aoss"` |

## Region

`Region` is required and validated against the AWSSDK's recognized region list — typos like `us-east1` (missing dash) fail at registration time rather than at first wire request.
