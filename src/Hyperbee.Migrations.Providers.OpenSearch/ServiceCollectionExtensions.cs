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
using Hyperbee.Migrations.Providers.OpenSearch.Squash;
using Hyperbee.Migrations.Squash;
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

            // ADR-0012 — apply production defaults BEFORE user configuration so
            // explicit per-option settings still win. The marker is registered
            // by WithProductionDefaults(); when present, flip the four defaults
            // documented in the ADR consequences section.
            if ( provider.GetService<UseProductionDefaultsMarker>() is not null )
            {
                options.ClusterHealthThreshold = ClusterHealthThreshold.Green;
                options.WaitMode = WaitMode.PerMigration;
                options.RequireUnsafeJustification = true;
                options.ContextResolutionPolicy = ContextResolutionPolicy.RequireExplicit;
            }

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

        services.TryAddSingleton( TimeProvider.System );

        // Concrete provider-typed registrations (per ADR-0023). TryAddSingleton
        // + factory delegate -- idempotent registration; record-store stays
        // internal.
        services.TryAddSingleton( OpenSearchMigrationOptionsFactory );
        services.TryAddSingleton<OpenSearchRecordStore>( provider => new OpenSearchRecordStore(
            provider.GetRequiredService<IOpenSearchClient>(),
            provider.GetRequiredService<OpenSearchBootstrapper>(),
            provider.GetRequiredService<OpenSearchMigrationOptions>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<OpenSearchRecordStore>>() ) );
        services.TryAddSingleton( provider => new OpenSearchMigrationRunner(
            provider.GetRequiredService<OpenSearchRecordStore>(),
            provider.GetRequiredService<OpenSearchMigrationOptions>(),
            provider.GetRequiredService<ILoggerFactory>() ) );

        // Legacy single-provider aliases (per ADR-0023 amendment F1).
        services.RegisterBaseAliases(
            "OpenSearch",
            provider => provider.GetRequiredService<OpenSearchMigrationOptions>(),
            provider => provider.GetRequiredService<OpenSearchRecordStore>(),
            provider => provider.GetRequiredService<OpenSearchMigrationRunner>() );

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

        // Squash codegen wiring (per ADR-0019).
        //
        // Component instances are stateless and idempotent; safe to register
        // as singletons. The strategy receives an optional ILogger if one is
        // present in the container (consumers without logging configured fall
        // back to NullLogger via the strategy's constructor default).
        services.TryAddSingleton<OpenSearchSnapshotCanonicalizer>();
        services.TryAddSingleton<OpenSearchDataOpClassifier>();
        services.TryAddSingleton( provider => new RestStateDiffStrategy(
            provider.GetRequiredService<OpenSearchSnapshotCanonicalizer>(),
            provider.GetRequiredService<OpenSearchDataOpClassifier>(),
            provider.GetService<ILogger<RestStateDiffStrategy>>() ) );
        services.TryAddSingleton<OpenSearchSquashVerifier>();
        services.TryAddSingleton( provider =>
        {
            // Topology signature ships a default instance ONLY for descriptor
            // composition; live CaptureAsync overrides the Properties bag
            // when the strategy actually runs against a cluster.
            ITopologySignature topology = new OpenSearchTopologySignature();
            var descriptor = new SquashStrategyDescriptor(
                TopologySignature: topology,
                DataOpClassifier: provider.GetRequiredService<OpenSearchDataOpClassifier>(),
                Generator: provider.GetRequiredService<RestStateDiffStrategy>(),
                Verifier: provider.GetRequiredService<OpenSearchSquashVerifier>(),
                Canonicalizer: provider.GetRequiredService<OpenSearchSnapshotCanonicalizer>() );
            descriptor.EnsureValid();
            return descriptor;
        } );

        return services;
    }

    /// <summary>
    /// Applies production-safe defaults to the OpenSearch migration options:
    /// <list type="bullet">
    ///   <item><description><c>ClusterHealthThreshold = Green</c> — bootstrap waits for full shard allocation, not just primaries.</description></item>
    ///   <item><description><c>WaitMode = PerMigration</c> — implicit waits coalesce to the end of each migration instead of after each statement.</description></item>
    ///   <item><description><c>RequireUnsafeJustification = true</c> — bare <c>UNSAFE</c> / <c>NO WAIT</c> without a justification string fails at parse time.</description></item>
    ///   <item><description><c>ContextResolutionPolicy = RequireExplicit</c> — context-scoped resources without an <c>ActiveContext</c> set are a loud error rather than silently skipped.</description></item>
    /// </list>
    /// Per ADR-0012 — explicit forcing function over hidden environment-profile coupling. Defaults are applied by the options factory BEFORE user configuration runs, so any per-option setting in the <c>configuration</c> callback wins.
    /// </summary>
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

    // ADR-0030 note on overload shape.
    //
    // configureSettings is added as a SEPARATE overload rather than as another
    // optional parameter on the existing methods. Appending an optional parameter
    // is source-compatible but NOT binary-compatible: it changes the method's
    // signature, so the 3.1.x signature stops existing and any assembly compiled
    // against it throws MissingMethodException until recompiled. This library
    // states that it follows Semantic Versioning, and a minor release is not
    // allowed to do that.
    //
    // The pre-existing overloads below keep their exact 3.1.x parameter lists and
    // simply forward. Their `= null` defaults are dropped, which is NOT a signature
    // change -- a default is parameter metadata, and a 3.1.x caller that omitted the
    // argument already baked the null into its own call site. Dropping it is what
    // keeps the pair unambiguous when the new overload supplies defaults instead:
    //
    //   AddOpenSearchClient( services, uri )            -> new overload (both default)
    //   AddOpenSearchClient( services, uri, auth )      -> 3.1.x overload (fewer params wins)
    //   AddOpenSearchClient( services, uri, auth, cfg ) -> new overload (only candidate)
    //   AddOpenSearchClient( services, uri, configureSettings: cfg ) -> new overload

    /// <summary>
    /// Registers an <see cref="IOpenSearchClient"/> in the service collection
    /// using the supplied endpoint and authentication options. Basic, API key,
    /// and mTLS are supported (R-21).
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="endpoint">The OpenSearch endpoint.</param>
    /// <param name="configure">Authentication configuration.</param>
    public static IServiceCollection AddOpenSearchClient(
        this IServiceCollection services,
        Uri endpoint,
        Action<OpenSearchAuthenticationOptions>? configure )
        => AddOpenSearchClient( services, endpoint, configure, configureSettings: null );

    /// <summary>
    /// Registers an <see cref="IOpenSearchClient"/> in the service collection
    /// using the supplied endpoint and authentication options, with direct access
    /// to the underlying <see cref="ConnectionSettings"/>.
    /// </summary>
    /// <param name="services">The service collection.</param>
    /// <param name="endpoint">The OpenSearch endpoint.</param>
    /// <param name="configure">Authentication configuration.</param>
    /// <param name="configureSettings">
    /// Optional escape hatch over the underlying <see cref="ConnectionSettings"/>, per
    /// ADR-0030. Runs <em>last</em> — after the endpoint and authentication wiring — so
    /// anything this library sets can be overridden. Use it for transport concerns the
    /// typed options do not cover: <c>RequestTimeout</c>, <c>MaximumRetries</c>,
    /// <c>EnableHttpCompression</c>, a proxy, <c>ServerCertificateValidationCallback</c>
    /// for a self-signed development cluster, <c>DisableDirectStreaming</c> while
    /// debugging, or a <c>DefaultMappingFor</c> covering <em>your own</em> document types
    /// when the client is shared with application code.
    /// <para>
    /// You do <b>not</b> need this to make migrations work. The ledger never relies on
    /// consumer-configured type inference (ADR-0029), so no mapping for
    /// <c>OpenSearchMigrationRecord</c> is required or expected.
    /// </para>
    /// <para>
    /// One caveat: the ledger index ships a <c>strict</c> mapping whose fields are
    /// camelCase, matching this client's default field-name inference. Replacing the
    /// serializer or setting a non-camelCase <c>DefaultFieldNameInferrer</c> will make
    /// ledger writes fail against that mapping. Registration validates this and fails
    /// with a pointed message rather than letting it surface as a
    /// <c>strict_dynamic_mapping_exception</c> at run time.
    /// </para>
    /// </param>
    public static IServiceCollection AddOpenSearchClient(
        this IServiceCollection services,
        Uri endpoint,
        Action<OpenSearchAuthenticationOptions>? configure = null,
        Action<ConnectionSettings>? configureSettings = null )
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

            // ADR-0030 — consumer escape hatch, applied LAST so it can override
            // anything set above. Without it the only way to reach ConnectionSettings
            // was to stop calling this method and hand-roll the registration, which
            // meant forking the auth wiring and the AWS-endpoint guard along with it.
            return BuildClient( settings, configureSettings );
        } );

        return services;
    }

    // The ledger index carries a `strict` mapping with camelCase fields, which
    // matches this client's default field-name inference. A consumer hook that
    // replaces the serializer or sets a non-camelCase DefaultFieldNameInferrer
    // makes every ledger write fail against that mapping. That failure IS loud
    // (strict_dynamic_mapping_exception) but it surfaces at first write, names
    // fields rather than the cause, and reads like a schema problem.
    //
    // Catch it where it was introduced instead. Probing one known ledger property
    // is enough: field-name inference is a single setting, so one sample proves
    // the convention.
    internal static OpenSearchClient BuildClient(
        ConnectionSettings settings,
        Action<ConnectionSettings>? configureSettings )
    {
        configureSettings?.Invoke( settings );

        var client = new OpenSearchClient( settings );

        if ( configureSettings is not null )
            ValidateLedgerFieldNaming( client.ConnectionSettings );

        return client;
    }

    private static void ValidateLedgerFieldNaming( IConnectionSettingsValues settings )
    {
        var probe = typeof( OpenSearchMigrationRecord ).GetProperty( nameof( OpenSearchMigrationRecord.AppliedBy ) )!;
        var inferred = settings.Inferrer.PropertyName( probe );

        if ( string.Equals( inferred, "appliedBy", StringComparison.Ordinal ) )
            return;

        throw new OpenSearchProviderException(
            $"The configureSettings callback changed field-name inference: the ledger property " +
            $"`{probe.Name}` now serializes as `{inferred}` instead of `appliedBy`. The migration " +
            "ledger index is created with a strict mapping using camelCase field names, so every " +
            "ledger write would be rejected with strict_dynamic_mapping_exception." + Environment.NewLine +
            Environment.NewLine +
            "This is usually caused by replacing the serializer or by calling " +
            "DefaultFieldNameInferrer(...) with a non-camelCase convention. Both are supported for " +
            "your own document types -- scope them with DefaultMappingFor<TDocument>() instead of " +
            "changing the client-wide default, or register a separate IOpenSearchClient for " +
            "application use and leave the migration client stock." );
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
        => AddOpenSearchClient( services, configuration, configureSettings: null );

    /// <summary>
    /// Convenience overload that reads endpoint + auth from <see cref="IConfiguration"/>,
    /// with direct access to the underlying <see cref="ConnectionSettings"/>. See the
    /// endpoint overload for what <paramref name="configureSettings"/> is and is not for.
    /// </summary>
    public static IServiceCollection AddOpenSearchClient(
        this IServiceCollection services,
        IConfiguration configuration,
        Action<ConnectionSettings>? configureSettings = null )
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
        }, configureSettings );
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
