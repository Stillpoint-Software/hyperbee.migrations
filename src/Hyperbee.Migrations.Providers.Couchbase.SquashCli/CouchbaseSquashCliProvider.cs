using Couchbase;
using Couchbase.Query;
using Hyperbee.Migrations.Providers.Couchbase.Services;
using Hyperbee.Migrations.Providers.Couchbase.Squash;
using Hyperbee.Migrations.Squash;
using Hyperbee.Migrations.Squash.Cli;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;
// Alias to avoid the namespace clash: Hyperbee.Migrations.Providers.Couchbase
// shadows the SDK's `Couchbase.*` when unqualified `Couchbase` is resolved
// from the SquashCli namespace.
using CouchbaseSdkException = Couchbase.CouchbaseException;

namespace Hyperbee.Migrations.Providers.Couchbase.SquashCli;

/// <summary>
/// Couchbase implementation of <see cref="ISquashCliProvider"/>. Discovered
/// by the CLI via the migration assembly's reference closure (per ADR-0024).
/// </summary>
public sealed class CouchbaseSquashCliProvider : ISquashCliProvider
{
    private readonly IEphemeralProvisioner _provisioner;

    public string ProviderId => "couchbase";
    public string SquashFileExtension => ".statements";

    public CouchbaseSquashCliProvider()
        : this( provisioner: null )
    {
    }

    public CouchbaseSquashCliProvider( IEphemeralProvisioner provisioner )
    {
        _provisioner = provisioner ?? new CouchbaseEphemeralProvisioner();
    }

    public async Task<SquashGenerationResult> GenerateAsync(
        SquashCliContext context,
        CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( context );

        // Provider option: target bucket. Required for capture (the snapshot
        // scope is the bucket).
        var bucketName = context.ProviderOptions != null
            && context.ProviderOptions.TryGetValue( "bucket-name", out var bn )
            && !string.IsNullOrWhiteSpace( bn )
            ? bn
            : throw new InvalidOperationException(
                "Couchbase squash codegen requires the target bucket name. Supply " +
                "--provider-option bucket-name=<name>." );

        var capture = new CouchbaseEphemeralCapture(
            applyMigrations: async ( connStr, upTo, ct ) =>
                await ApplyMigrationsViaHostAsync(
                    context.MigrationHost,
                    connStr,
                    upTo,
                    ct ).ConfigureAwait( false ),
            provisioner: _provisioner );

        // Live cluster handle for the operator's cluster (used by the
        // generation context for topology capture). Couchbase requires
        // username + password set on ClusterOptions; pull them from
        // provider options or default to the test-cluster defaults
        // (Administrator/password) which match `Testcontainers.Couchbase`.
        var username = context.ProviderOptions != null
            && context.ProviderOptions.TryGetValue( "username", out var u )
            && !string.IsNullOrWhiteSpace( u )
            ? u
            : "Administrator";
        var password = context.ProviderOptions != null
            && context.ProviderOptions.TryGetValue( "password", out var pw )
            && !string.IsNullOrWhiteSpace( pw )
            ? pw
            : "password";

        var liveOptions = new ClusterOptions
        {
            ConnectionString = context.ConnectionString,
            UserName = username,
            Password = password
        };
        var liveCluster = await Cluster.ConnectAsync( liveOptions ).ConfigureAwait( false );
        try
        {
            await liveCluster.WaitUntilReadyAsync( TimeSpan.FromMinutes( 1 ) ).ConfigureAwait( false );

            // CouchbaseRestApiService talks to /pools/default/... via REST.
            // Couchbase REST endpoints require basic auth even when the SDK
            // already has cluster credentials. Wire the auth header explicitly.
            using var http = new HttpClient();
            http.DefaultRequestHeaders.Authorization =
                new System.Net.Http.Headers.AuthenticationHeaderValue(
                    "Basic",
                    Convert.ToBase64String( System.Text.Encoding.ASCII.GetBytes( $"{username}:{password}" ) ) );
            var liveRestApi = new CouchbaseRestApiService(
                http,
                new OptionsWrapper<ClusterOptions>( liveOptions ),
                NullLogger<CouchbaseRestApiService>.Instance );

            var ctx = new CouchbaseSquashGenerationContext(
                squashName: context.SquashName,
                squashVersion: context.ToVersion,
                cluster: liveCluster,
                restApi: liveRestApi,
                bucketName: bucketName,
                captureSnapshotAsync: ( req, ct ) => capture.CaptureAsync( req, bucketName, ct ) );

            var canonicalizer = new CouchbaseSnapshotCanonicalizer();
            var dataOpClassifier = new CouchbaseDataOpClassifier();
            var strategy = new HybridStrategy( canonicalizer, dataOpClassifier );

            return await strategy.GenerateAsync( ctx, context.Descriptors, context.Options )
                .ConfigureAwait( false );
        }
        finally
        {
            await liveCluster.DisposeAsync().ConfigureAwait( false );
        }
    }

    public IReadOnlyList<MigrationSourceVerdict> ScanSource( string sourceDirectory )
    {
        if ( string.IsNullOrWhiteSpace( sourceDirectory ) )
            return Array.Empty<MigrationSourceVerdict>();

        var verdicts = CouchbaseMigrationSourceScanner.Scan( sourceDirectory );
        return verdicts
            .Select( v => new MigrationSourceVerdict
            {
                FilePath = v.FilePath,
                ClassName = v.ClassName,
                RequiresAnnotation = v.RequiresAnnotation,
                DataOpHits = v.DataOpHits,
                NonDeterminismHits = v.NonDeterminismHits
            } )
            .ToArray();
    }

    public async Task<long> ProbeLastAppliedVersionAsync(
        string liveConnectionString,
        IReadOnlyDictionary<string, string> providerOptions,
        CancellationToken cancellationToken )
    {
        // RB-3: read bucket/scope/collection from per-environment overrides.
        var bucketName = providerOptions != null
            && providerOptions.TryGetValue( "bucket-name", out var bn )
            && !string.IsNullOrWhiteSpace( bn )
            ? bn
            : throw new InvalidOperationException(
                "Couchbase fleet probe requires the target bucket name. Supply " +
                "`bucket-name` in the fleet manifest topology overrides." );
        var scopeName = providerOptions != null
            && providerOptions.TryGetValue( "scope-name", out var sn )
            && !string.IsNullOrWhiteSpace( sn )
            ? sn
            : "_default";
        var collectionName = providerOptions != null
            && providerOptions.TryGetValue( "collection-name", out var cn )
            && !string.IsNullOrWhiteSpace( cn )
            ? cn
            : "_default";

        var options = new ClusterOptions { ConnectionString = liveConnectionString };
        var cluster = await Cluster.ConnectAsync( options ).ConfigureAwait( false );
        try
        {
            await cluster.WaitUntilReadyAsync( TimeSpan.FromMinutes( 1 ) ).ConfigureAwait( false );

            var keyspace = $"`{bucketName}`.`{scopeName}`.`{collectionName}`";
            var statement =
                $"SELECT RAW MAX(TONUMBER(SPLIT(META(d).id, '.')[1])) " +
                $"FROM {keyspace} d " +
                $"WHERE REGEXP_MATCHES(META(d).id, '^record\\\\.\\\\d+\\\\.')";

            try
            {
                var result = await cluster.QueryAsync<long?>(
                    statement,
                    new QueryOptions().CancellationToken( cancellationToken ) ).ConfigureAwait( false );

                await foreach ( var row in result.WithCancellation( cancellationToken ).ConfigureAwait( false ) )
                {
                    if ( row.HasValue ) return row.Value;
                }
                return 0;
            }
            catch ( CouchbaseSdkException )
            {
                // Index / bucket / namespace not found: treat as empty
                // ledger. Operators running fleet probe against a fresh
                // cluster have no ledger rows yet.
                return 0;
            }
        }
        finally
        {
            await cluster.DisposeAsync().ConfigureAwait( false );
        }
    }

    private static async Task ApplyMigrationsViaHostAsync(
        IMigrationHost host,
        string connectionString,
        long upToVersion,
        CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( host );

        var ctx = new MigrationHostContext( connectionString )
        {
            OverrideOptions = opts => opts.ToVersion = upToVersion
        };

        var serviceProvider = await host.ConfigureAsync( ctx, cancellationToken ).ConfigureAwait( false );
        try
        {
            var runner = serviceProvider.GetRequiredService<CouchbaseMigrationRunner>();
            await runner.RunAsync( cancellationToken ).ConfigureAwait( false );
        }
        finally
        {
            if ( serviceProvider is IAsyncDisposable iad ) await iad.DisposeAsync().ConfigureAwait( false );
            else if ( serviceProvider is IDisposable d ) d.Dispose();
        }
    }
}
