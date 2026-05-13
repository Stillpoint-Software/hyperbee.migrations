using Aerospike.Client;
using Hyperbee.Migrations.Providers.Aerospike.Squash;
using Hyperbee.Migrations.Squash;
using Hyperbee.Migrations.Squash.Cli;
using Microsoft.Extensions.DependencyInjection;

namespace Hyperbee.Migrations.Providers.Aerospike.SquashCli;

/// <summary>
/// Aerospike implementation of <see cref="ISquashCliProvider"/>. Discovered
/// by the CLI via the migration assembly's reference closure (per ADR-0024).
/// </summary>
public sealed class AerospikeSquashCliProvider : ISquashCliProvider
{
    private readonly IEphemeralProvisioner _provisioner;

    public string ProviderId => "aerospike";
    public string SquashFileExtension => ".statements";

    public AerospikeSquashCliProvider()
        : this( provisioner: null )
    {
    }

    public AerospikeSquashCliProvider( IEphemeralProvisioner provisioner )
    {
        _provisioner = provisioner ?? new AerospikeEphemeralProvisioner();
    }

    public async Task<SquashGenerationResult> GenerateAsync(
        SquashCliContext context,
        CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( context );

        // Read provider-specific options for the namespace. Falls back to the
        // Aerospike provider's default ("test") matching AerospikeMigrationOptions.
        var @namespace = context.ProviderOptions != null
            && context.ProviderOptions.TryGetValue( "namespace", out var ns )
            && !string.IsNullOrWhiteSpace( ns )
            ? ns
            : "test";

        // Parse the live connection string `host:port` for the operator's
        // cluster. Used for the squash generation context (which needs an
        // IAerospikeClient handle even though most of the work happens
        // against the ephemeral container).
        ParseHostPort( context.ConnectionString, defaultPort: 3000, out var liveHost, out var livePort );

        var capture = new AerospikeEphemeralCapture(
            applyMigrations: async ( host, port, upTo, ct ) =>
                await ApplyMigrationsViaHostAsync(
                    context.MigrationHost,
                    host, port,
                    upTo,
                    ct ).ConfigureAwait( false ),
            provisioner: _provisioner );

        // Open the live client with a brief retry loop. AsyncClient (not the
        // synchronous AerospikeClient) is used here because the daemon's
        // cluster-map advertises its internal address; AsyncClient's lazier
        // discovery tolerates the first-connect race against single-node
        // test fixtures where AerospikeClient's blocking ctor reports
        // "connection forcibly closed".
        IAerospikeClient liveClient = null;
        AsyncClientPolicy policy = new AsyncClientPolicy
        {
            timeout = 30_000,
            failIfNotConnected = false
        };
        for ( var attempt = 0; attempt < 10 && liveClient == null; attempt++ )
        {
            try
            {
                liveClient = new AsyncClient( policy, liveHost, livePort );
            }
            catch ( AerospikeException )
            {
                if ( attempt == 9 ) throw;
                await Task.Delay( TimeSpan.FromMilliseconds( 500 ), cancellationToken ).ConfigureAwait( false );
            }
        }
        using var liveClientScope = (IDisposable) liveClient;

        var ctx = new AerospikeSquashGenerationContext(
            squashName: context.SquashName,
            squashVersion: context.ToVersion,
            client: liveClient,
            @namespace: @namespace,
            captureSnapshotAsync: ( req, ct ) => capture.CaptureAsync( req, @namespace, ct ) );

        var canonicalizer = new AerospikeSnapshotCanonicalizer();
        var dataOpClassifier = new AerospikeDataOpClassifier();
        var strategy = new InfoSnapshotStrategy( canonicalizer, dataOpClassifier );

        return await strategy.GenerateAsync( ctx, context.Descriptors, context.Options )
            .ConfigureAwait( false );
    }

    public IReadOnlyList<MigrationSourceVerdict> ScanSource( string sourceDirectory )
    {
        if ( string.IsNullOrWhiteSpace( sourceDirectory ) )
            return Array.Empty<MigrationSourceVerdict>();

        var verdicts = AerospikeMigrationSourceScanner.Scan( sourceDirectory );
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
        // RB-3: read namespace + migration-set from per-environment overrides
        // rather than hardcoding `test` / `SchemaMigrations`. Operators with
        // non-default namespaces or set names supply these in the fleet
        // manifest's `topology:` section.
        var @namespace = providerOptions != null
            && providerOptions.TryGetValue( "namespace", out var ns )
            && !string.IsNullOrWhiteSpace( ns )
            ? ns
            : AerospikeMigrationOptions.DefaultNamespace;
        var migrationSet = providerOptions != null
            && providerOptions.TryGetValue( "migration-set", out var setName )
            && !string.IsNullOrWhiteSpace( setName )
            ? setName
            : AerospikeMigrationOptions.DefaultMigrationSet;

        ParseHostPort( liveConnectionString, defaultPort: 3000, out var host, out var port );

        using var client = new AerospikeClient( host, port );

        // Aerospike: scan the migration set and extract the highest version
        // encoded in the record id. The record id convention is
        // `record.<version>.<name>` per DefaultMigrationConventions; we
        // parse the leading numeric token after "record.".
        var scanPolicy = new ScanPolicy
        {
            includeBinData = false  // record id is the digest's PK; we only need keys
        };

        long highest = 0;

        // Aerospike's ScanAll is listener-shaped (no native CT). Bridge via
        // a TaskCompletionSource the same way AerospikeRecordStore.IntersectWithSquashedAsync does.
        var tcs = new TaskCompletionSource<bool>( TaskCreationOptions.RunContinuationsAsynchronously );
        var maxLock = new object();

        try
        {
            client.ScanAll( scanPolicy, @namespace, migrationSet, ( key, record ) =>
            {
                var userKey = key.userKey?.ToString();
                if ( string.IsNullOrEmpty( userKey ) ) return;
                if ( !userKey.StartsWith( "record.", StringComparison.Ordinal ) ) return;
                var afterPrefix = userKey.Substring( "record.".Length );
                var dot = afterPrefix.IndexOf( '.' );
                if ( dot <= 0 ) return;
                if ( !long.TryParse( afterPrefix.Substring( 0, dot ), out var version ) ) return;

                lock ( maxLock )
                {
                    if ( version > highest ) highest = version;
                }
            } );
            tcs.TrySetResult( true );
        }
        catch ( AerospikeException ex ) when ( ex.Result == ResultCode.INVALID_NAMESPACE )
        {
            // namespace doesn't exist on this cluster -- treat as empty
            return 0;
        }
        catch ( Exception ex )
        {
            tcs.TrySetException( ex );
        }

        await tcs.Task.ConfigureAwait( false );
        return highest;
    }

    private static void ParseHostPort( string connection, int defaultPort, out string host, out int port )
    {
        if ( string.IsNullOrWhiteSpace( connection ) )
            throw new ArgumentException( "connection string is required.", nameof( connection ) );

        var colon = connection.IndexOf( ':' );
        if ( colon < 0 )
        {
            host = connection;
            port = defaultPort;
            return;
        }

        host = connection.Substring( 0, colon );
        if ( !int.TryParse( connection.Substring( colon + 1 ), out port ) )
            throw new ArgumentException(
                $"connection string `{connection}` is not in host:port form.", nameof( connection ) );
    }

    private static async Task ApplyMigrationsViaHostAsync(
        IMigrationHost host,
        string aerospikeHost,
        int aerospikePort,
        long upToVersion,
        CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( host );

        var ctx = new MigrationHostContext( $"{aerospikeHost}:{aerospikePort}" )
        {
            OverrideOptions = opts => opts.ToVersion = upToVersion
        };

        var serviceProvider = await host.ConfigureAsync( ctx, cancellationToken ).ConfigureAwait( false );
        try
        {
            var runner = serviceProvider.GetRequiredService<AerospikeMigrationRunner>();
            await runner.RunAsync( cancellationToken ).ConfigureAwait( false );
        }
        finally
        {
            if ( serviceProvider is IAsyncDisposable iad ) await iad.DisposeAsync().ConfigureAwait( false );
            else if ( serviceProvider is IDisposable d ) d.Dispose();
        }
    }
}
