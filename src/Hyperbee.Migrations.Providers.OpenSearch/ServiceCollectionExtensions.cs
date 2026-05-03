#nullable enable
using System.Reflection;
using System.Runtime.Loader;
using System.Security.Cryptography.X509Certificates;
using Hyperbee.Migrations.Providers.OpenSearch.Internal;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Bootstrap;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Bootstrap.Steps;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Dispatch;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Grammar;
using Hyperbee.Migrations.Providers.OpenSearch.Internal.Middleware;
using Hyperbee.Migrations.Providers.OpenSearch.Resources;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using OpenSearch.Client;

namespace Hyperbee.Migrations.Providers.OpenSearch;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddOpenSearchMigrations( this IServiceCollection services )
        => AddOpenSearchMigrations( services, null, Assembly.GetCallingAssembly() );

    public static IServiceCollection AddOpenSearchMigrations( this IServiceCollection services, Action<OpenSearchMigrationOptions>? configuration )
        => AddOpenSearchMigrations( services, configuration, Assembly.GetCallingAssembly() );

    private static IServiceCollection AddOpenSearchMigrations( IServiceCollection services, Action<OpenSearchMigrationOptions>? configuration, Assembly defaultAssembly )
    {
        OpenSearchMigrationOptions OpenSearchMigrationOptionsFactory( IServiceProvider provider )
        {
            var options = new OpenSearchMigrationOptions( new DefaultMigrationActivator( provider ) );

            configuration?.Invoke( options );

            // concat options.Assemblies with IConfiguration `FromAssemblies` and `FromPaths`

            var config = provider.GetRequiredService<IConfiguration>();

            var nameAssemblies = config
                .GetEnumerable<string>( "Migrations:FromAssemblies" )
                .Select( name => Assembly.Load( new AssemblyName( name ) ) );

            var pathAssemblies = config
                .GetEnumerable<string>( "Migrations:FromPaths" )
                .Select( name => AssemblyLoadContext.Default.LoadFromAssemblyPath( Path.GetFullPath( name ) ) );

            options.Assemblies = options.Assemblies
                .Concat( nameAssemblies )
                .Concat( pathAssemblies )
                .Distinct()
                .DefaultIfEmpty( defaultAssembly )
                .ToList();

            return options;
        }

        services.AddSingleton( OpenSearchMigrationOptionsFactory );
        services.AddSingleton<MigrationOptions>( provider => provider.GetRequiredService<OpenSearchMigrationOptions>() );

        services.AddSingleton<IMigrationRecordStore, OpenSearchRecordStore>();
        services.AddSingleton<MigrationRunner>();

        services.TryAddSingleton( TimeProvider.System );

        // Bootstrapper pipeline (ADR-0014). Default steps registered in execution order.
        // Consumers extend by registering additional IBootstrapStep implementations BEFORE
        // calling AddOpenSearchMigrations (DI resolves singletons in registration order).
        services.AddSingleton<IBootstrapStep, RestPingStep>();
        services.AddSingleton<IBootstrapStep, ClusterHealthStep>();
        services.AddSingleton<IBootstrapStep, LedgerIndexInitStep>();
        services.AddSingleton<IBootstrapStep, LockIndexInitStep>();
        // R-21 #3 — ISM endpoint capability detection. Singleton so the
        // resolved prefix is shared across the dispatcher's lifetime;
        // detection runs once at bootstrap.
        services.AddSingleton<IsmEndpointCapability>();
        services.AddSingleton<IBootstrapStep, IsmEndpointDetectStep>();
        services.AddSingleton<OpenSearchBootstrapper>();

        // Statement pipeline (ADR-0011 hybrid). The parser is offline-pure (ADR-0015);
        // the dispatcher applies SafeDefaultMergeMiddleware then dispatches.
        services.AddSingleton<OpenSearchStatementParser>();
        services.AddSingleton<SafeDefaultMergeMiddleware>();
        services.AddSingleton<StatementDispatcher>();

        // Resource runner (ADR-0002). Generic over the migration type for resource
        // path resolution. Transient because each migration instance gets its own logger.
        services.AddTransient( typeof( OpenSearchResourceRunner<> ) );

        return services;
    }

    /// <summary>
    /// Marks the registration to apply production-safe defaults: Green health threshold,
    /// PerMigration waits, UNSAFE/NO WAIT justification required, RequireExplicit context
    /// resolution. Per ADR-0012 — explicit forcing function over hidden environment-profile
    /// coupling. Per-option settings chained after this win (handled by the options factory
    /// applying user configuration after defaults).
    /// </summary>
    /// <remarks>
    /// Phase 0 scaffolding registers the marker only. Phase 6 lands the options-factory
    /// integration that applies the four defaults before user configuration runs.
    /// </remarks>
    public static IServiceCollection WithProductionDefaults( this IServiceCollection services )
    {
        services.TryAddSingleton<UseProductionDefaultsMarker>();
        return services;
    }

    // R-21 — auth-aware client registration. Builds an IOpenSearchClient with
    // mode-appropriate authentication wired into the ConnectionSettings,
    // validates the auth fields, and registers the client as a singleton.
    //
    // The provider package owns the auth-wiring logic so the runner project
    // (and any library consumer) gets a uniform surface. SigV4 is NOT here —
    // it ships in a separate opt-in extension (plan task 3.2 / R-21 #2-#4)
    // so this package stays free of the AWS-SDK transitive dependency tree.

    /// <summary>
    /// Registers an <see cref="IOpenSearchClient"/> in the service collection
    /// using the supplied endpoint and authentication options. Basic, API key,
    /// and mTLS are supported (R-21).
    /// </summary>
    public static IServiceCollection AddOpenSearchClient(
        this IServiceCollection services,
        Uri endpoint,
        Action<OpenSearchAuthenticationOptions>? configure = null )
    {
        ArgumentNullException.ThrowIfNull( services );
        ArgumentNullException.ThrowIfNull( endpoint );

        // R-21 #2 — AWS endpoint loud-fail. AWS Managed OpenSearch domains
        // and OpenSearch Serverless collections both live under the
        // *.amazonaws.com namespace and require SigV4. The core package
        // doesn't carry the AWSSDK transitive dependency tree, so it
        // can't sign requests; loud-fail at startup with the exact
        // alternative API that does.
        //
        // Pure URL string check — no DI introspection, no marker dance,
        // no cross-package conditional flow. The check fires regardless
        // of which auth mode the operator configured (Basic, ApiKey, mTLS,
        // Anonymous all hit it equally) because the cluster will reject
        // anything but SigV4.
        ThrowIfAwsEndpoint( endpoint );

        // Mutual exclusion guard — only one OpenSearch client registration
        // path may be used per service collection. AddOpenSearchAwsClient
        // (in the .Aws extension package) carries the equivalent guard
        // pointed in the opposite direction.
        ThrowIfClientAlreadyRegistered( services );

        var auth = new OpenSearchAuthenticationOptions();
        configure?.Invoke( auth );
        auth.Validate();

        services.AddSingleton<IOpenSearchClient>( sp =>
        {
            var loggerFactory = sp.GetService<ILoggerFactory>();
            var log = loggerFactory?.CreateLogger( "Hyperbee.Migrations.Providers.OpenSearch.Client" );

            var settings = new ConnectionSettings( endpoint );

            switch ( auth.Mode )
            {
                case OpenSearchAuthenticationMode.Anonymous:
                    log?.LogWarning(
                        "OpenSearch client registered with Authentication.Mode = Anonymous. " +
                        "Production deployments should use Basic, ApiKey, or ClientCertificate auth." );
                    break;

                case OpenSearchAuthenticationMode.Basic:
                    settings = settings.BasicAuthentication( auth.UserName, auth.Password ?? string.Empty );
                    log?.LogInformation( "OpenSearch client: Basic auth as `{user}`", auth.UserName );
                    break;

                case OpenSearchAuthenticationMode.ApiKey:
                    settings = settings.ApiKeyAuthentication( auth.ApiKeyId, auth.ApiKey );
                    log?.LogInformation( "OpenSearch client: API key auth (id `{id}`)", auth.ApiKeyId );
                    break;

                case OpenSearchAuthenticationMode.ClientCertificate:
                    var cert = ResolveClientCertificate( auth );
                    settings = settings.ClientCertificate( cert );
                    log?.LogInformation( "OpenSearch client: mTLS via client certificate `{subject}`", cert.Subject );
                    break;
            }

            return new OpenSearchClient( settings );
        } );

        return services;
    }

    /// <summary>
    /// Convenience overload that reads endpoint + auth from <see cref="IConfiguration"/>
    /// using the standard layout under the <c>OpenSearch</c> section:
    /// <c>OpenSearch:ConnectionString</c>, <c>OpenSearch:Authentication:*</c>.
    /// Used by the runner project; library consumers can call the explicit
    /// overload instead.
    /// </summary>
    public static IServiceCollection AddOpenSearchClient(
        this IServiceCollection services,
        IConfiguration configuration )
    {
        ArgumentNullException.ThrowIfNull( services );
        ArgumentNullException.ThrowIfNull( configuration );

        var connectionString = configuration["OpenSearch:ConnectionString"]
            ?? "http://localhost:9200";

        var endpoint = new Uri( connectionString );

        return services.AddOpenSearchClient( endpoint, opts =>
        {
            // Bind the Authentication subsection. Modes are case-insensitive
            // ("basic", "Basic", "BASIC" all parse).
            var modeStr = configuration["OpenSearch:Authentication:Mode"];

            // Back-compat: if the legacy flat OpenSearch:UserName / Password
            // are set without an explicit Mode, treat that as Basic.
            if ( string.IsNullOrEmpty( modeStr ) )
            {
                var legacyUser = configuration["OpenSearch:UserName"];
                if ( !string.IsNullOrEmpty( legacyUser ) )
                {
                    opts.Mode = OpenSearchAuthenticationMode.Basic;
                    opts.UserName = legacyUser;
                    opts.Password = configuration["OpenSearch:Password"];
                    return;
                }

                opts.Mode = OpenSearchAuthenticationMode.Anonymous;
                return;
            }

            if ( !Enum.TryParse<OpenSearchAuthenticationMode>( modeStr, ignoreCase: true, out var mode ) )
            {
                throw new OpenSearchProviderException(
                    $"OpenSearch:Authentication:Mode `{modeStr}` is not recognized. Valid: Anonymous, Basic, ApiKey, ClientCertificate." );
            }

            opts.Mode = mode;
            opts.UserName = configuration["OpenSearch:Authentication:UserName"];
            opts.Password = configuration["OpenSearch:Authentication:Password"];
            opts.ApiKeyId = configuration["OpenSearch:Authentication:ApiKeyId"];
            opts.ApiKey = configuration["OpenSearch:Authentication:ApiKey"];
            opts.ClientCertificatePath = configuration["OpenSearch:Authentication:ClientCertificatePath"];
            opts.ClientCertificatePassword = configuration["OpenSearch:Authentication:ClientCertificatePassword"];
        } );
    }

    private static void ThrowIfAwsEndpoint( Uri endpoint )
    {
        if ( !endpoint.Host.EndsWith( ".amazonaws.com", StringComparison.OrdinalIgnoreCase ) )
            return;

        throw new AwsSigV4NotConfiguredException(
            $"OpenSearch endpoint `{endpoint}` is an AWS Managed OpenSearch domain or OpenSearch Serverless " +
            "collection (host ends with .amazonaws.com), which requires AWS SigV4 request signing. " +
            "The core Hyperbee.Migrations.Providers.OpenSearch package does not include AWS SDK support. " +
            "Add a reference to Hyperbee.Migrations.Providers.OpenSearch.Aws and call:" + Environment.NewLine +
            Environment.NewLine +
            "    services.AddOpenSearchAwsClient( new Uri( connectionString ), opts =>" + Environment.NewLine +
            "    {" + Environment.NewLine +
            "        opts.Region = \"us-east-1\";   // your region" + Environment.NewLine +
            "        opts.Service = \"es\";        // \"aoss\" for OpenSearch Serverless" + Environment.NewLine +
            "    } );" + Environment.NewLine +
            Environment.NewLine +
            "instead of AddOpenSearchClient(...). The runner project's --auth-mode flag is " +
            "Basic / ApiKey / ClientCertificate-only; SigV4 wires through the .Aws extension." );
    }

    private static void ThrowIfClientAlreadyRegistered( IServiceCollection services )
    {
        if ( services.Any( d => d.ServiceType == typeof( IOpenSearchClient ) ) )
        {
            throw new OpenSearchProviderException(
                "AddOpenSearchClient cannot be called when an OpenSearch client has already been registered. " +
                "Call exactly one of: AddOpenSearchClient (for Basic / ApiKey / mTLS / Anonymous) " +
                "OR AddOpenSearchAwsClient (for AWS SigV4) — they are mutually exclusive." );
        }
    }

    private static X509Certificate ResolveClientCertificate( OpenSearchAuthenticationOptions auth )
    {
        if ( auth.ClientCertificate is not null )
            return auth.ClientCertificate;

        // Validate already confirmed the path exists. Multi-target net8.0
        // (no X509CertificateLoader) and net9.0+ (constructor deprecated)
        // by reading bytes and using the appropriate API per TFM.
        var path = auth.ClientCertificatePath!;
        var password = auth.ClientCertificatePassword;
#if NET9_0_OR_GREATER
        var bytes = File.ReadAllBytes( path );
        return string.IsNullOrEmpty( password )
            ? X509CertificateLoader.LoadPkcs12( bytes, null )
            : X509CertificateLoader.LoadPkcs12( bytes, password );
#else
#pragma warning disable SYSLIB0057 // Type or member is obsolete (X509Certificate2 ctor) — fallback for net8.0
        return string.IsNullOrEmpty( password )
            ? new X509Certificate2( path )
            : new X509Certificate2( path, password );
#pragma warning restore SYSLIB0057
#endif
    }

    private static IEnumerable<T> GetEnumerable<T>( this IConfiguration config, string key )
        => config.GetSection( key ).Get<IEnumerable<T>>() ?? [];
}

internal sealed class UseProductionDefaultsMarker { }
