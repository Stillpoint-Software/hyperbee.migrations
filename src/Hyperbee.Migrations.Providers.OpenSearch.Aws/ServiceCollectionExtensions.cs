#nullable enable
using Amazon;
using Amazon.Runtime;
using Hyperbee.Migrations.Providers.OpenSearch;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;
using OpenSearch.Client;
using OpenSearch.Net;
using OpenSearch.Net.Auth.AwsSigV4;

namespace Hyperbee.Migrations.Providers.OpenSearch.Aws;

// R-21 — opt-in AWS SigV4 client extension. Mutually exclusive with the
// core package's AddOpenSearchClient — call exactly one. Each extension
// throws if an IOpenSearchClient is already registered, so accidental
// double-registration surfaces as a loud error at startup naming the
// alternative API to use.
//
// Why a separate package (option E from the design discussion):
//
//   - Core stays free of the AWSSDK transitive dependency tree. Non-AWS
//     deployments don't pay the package size or runtime overhead.
//   - SigV4 isn't a peer of Basic/ApiKey/mTLS — it REPLACES the HTTP
//     transport (AwsSigV4HttpConnection signs every request). The
//     boundary between "header-based auth" and "transport-replacing
//     auth" is the natural seam, and putting them in different packages
//     respects it.
//   - Consumers self-select: AWS Managed → reference this package.
//     Anywhere else → use core only. Simple matrix; no marker dance,
//     no DI introspection across package boundaries.

public static class ServiceCollectionExtensions
{
    /// <summary>
    /// Registers an <see cref="IOpenSearchClient"/> configured to authenticate
    /// against AWS Managed OpenSearch Service (or OpenSearch Serverless) via
    /// SigV4 request signing (R-21).
    /// </summary>
    public static IServiceCollection AddOpenSearchAwsClient(
        this IServiceCollection services,
        Uri endpoint,
        Action<OpenSearchAwsAuthenticationOptions> configure )
    {
        ArgumentNullException.ThrowIfNull( services );
        ArgumentNullException.ThrowIfNull( endpoint );
        ArgumentNullException.ThrowIfNull( configure );

        ThrowIfClientAlreadyRegistered( services );

        var options = new OpenSearchAwsAuthenticationOptions();
        configure( options );

        if ( string.IsNullOrEmpty( options.Region ) )
        {
            throw new OpenSearchProviderException(
                "AddOpenSearchAwsClient requires Authentication.Region (e.g., \"us-east-1\"). " +
                "Set OpenSearch:Authentication:Region in configuration." );
        }

        if ( !RegionEndpoint.EnumerableAllRegions.Any( r =>
                 string.Equals( r.SystemName, options.Region, StringComparison.OrdinalIgnoreCase ) ) )
        {
            throw new OpenSearchProviderException(
                $"AddOpenSearchAwsClient: Region `{options.Region}` is not a recognized AWS region system name. " +
                "Examples: us-east-1, us-west-2, eu-west-1." );
        }

        // Inverse mismatch (option E): SigV4 configured against a non-AWS
        // endpoint is unusual but not invalid. Some VPC endpoints front
        // OpenSearch Service via custom domain names; some on-prem
        // sigv4-compatible proxies exist. WARN at registration so the
        // misconfiguration class ("forgot to point at the AWS host") is
        // surfaced visibly without blocking the legitimate edge case.
        services.AddSingleton<IOpenSearchClient>( sp =>
        {
            var loggerFactory = sp.GetService<ILoggerFactory>();
            var log = loggerFactory?.CreateLogger( "Hyperbee.Migrations.Providers.OpenSearch.Aws" );

            if ( !IsAwsEndpoint( endpoint.Host ) )
            {
                log?.LogWarning(
                    "OpenSearch AWS SigV4 client registered against a non-AWS endpoint `{host}`. " +
                    "If this is intentional (custom domain fronting AWS Managed, or sigv4-compatible proxy), " +
                    "you can ignore this warning. Otherwise verify the endpoint matches *.amazonaws.com.",
                    endpoint.Host );
            }

            var region = RegionEndpoint.GetBySystemName( options.Region );
            var credentials = options.Credentials ?? FallbackCredentialsFactory.GetCredentials();

            // R-21 #4: AwsSigV4HttpConnection calls AWSCredentials.GetCredentials()
            // per request internally. With FallbackCredentialsFactory or any of
            // the standard AWSCredentials implementations (InstanceProfile,
            // ECS, IRSA), credentials are re-resolved per request — IRSA
            // rotation and instance-profile rotation work without restart.
            var connection = new AwsSigV4HttpConnection(
                credentials, region, options.Service, dateTimeProvider: null );

            var settings = new ConnectionSettings( endpoint, connection );
            log?.LogInformation(
                "OpenSearch client: SigV4 auth (region {region}, service {service})",
                options.Region, options.Service );

            return new OpenSearchClient( settings );
        } );

        return services;
    }

    /// <summary>
    /// Convenience overload that reads endpoint + AWS auth from <see cref="IConfiguration"/>:
    /// <c>OpenSearch:ConnectionString</c>, <c>OpenSearch:Authentication:Region</c>,
    /// <c>OpenSearch:Authentication:Service</c>.
    /// </summary>
    public static IServiceCollection AddOpenSearchAwsClient(
        this IServiceCollection services,
        IConfiguration configuration )
    {
        ArgumentNullException.ThrowIfNull( services );
        ArgumentNullException.ThrowIfNull( configuration );

        var connectionString = configuration["OpenSearch:ConnectionString"]
            ?? throw new OpenSearchProviderException(
                "AddOpenSearchAwsClient requires OpenSearch:ConnectionString in configuration." );

        var endpoint = new Uri( connectionString );

        return services.AddOpenSearchAwsClient( endpoint, opts =>
        {
            opts.Region = configuration["OpenSearch:Authentication:Region"];
            opts.Service = configuration["OpenSearch:Authentication:Service"] ?? "es";
            // Credentials are NOT readable from configuration — by design.
            // Operators wire the AWS credential chain via environment
            // variables, instance profiles, IRSA, etc. (the resolution
            // path AWSCredentials.GetCredentials() walks per request).
        } );
    }

    private static void ThrowIfClientAlreadyRegistered( IServiceCollection services )
    {
        if ( services.Any( d => d.ServiceType == typeof( IOpenSearchClient ) ) )
        {
            throw new OpenSearchProviderException(
                "AddOpenSearchAwsClient cannot be called when an OpenSearch client has already been registered. " +
                "Call exactly one of: AddOpenSearchClient (for Basic / ApiKey / mTLS / Anonymous) " +
                "OR AddOpenSearchAwsClient (for AWS SigV4) — they are mutually exclusive." );
        }
    }

    internal static bool IsAwsEndpoint( string host )
        => host.EndsWith( ".amazonaws.com", StringComparison.OrdinalIgnoreCase );
}
