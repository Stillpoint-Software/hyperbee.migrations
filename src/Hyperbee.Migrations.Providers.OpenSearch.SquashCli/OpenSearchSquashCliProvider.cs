using Hyperbee.Migrations.Providers.OpenSearch.Squash;
using Hyperbee.Migrations.Squash;
using Hyperbee.Migrations.Squash.Cli;
using Microsoft.Extensions.DependencyInjection;
using OpenSearch.Client;
using OpenSearch.Net;

namespace Hyperbee.Migrations.Providers.OpenSearch.SquashCli;

/// <summary>
/// OpenSearch implementation of <see cref="ISquashCliProvider"/>. Discovered
/// by the CLI via the migration assembly's reference closure (per ADR-0024).
/// </summary>
public sealed class OpenSearchSquashCliProvider : ISquashCliProvider
{
    private readonly IEphemeralProvisioner _provisioner;

    public string ProviderId => "opensearch";
    public string SquashFileExtension => ".statements";

    public OpenSearchSquashCliProvider()
        : this( provisioner: null )
    {
    }

    public OpenSearchSquashCliProvider( IEphemeralProvisioner provisioner )
    {
        _provisioner = provisioner ?? new OpenSearchEphemeralProvisioner();
    }

    public async Task<SquashGenerationResult> GenerateAsync(
        SquashCliContext context,
        CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( context );

        // Provider option: container image override (operator can pin a
        // version that matches their target topology).
        var image = context.ProviderOptions != null
            && context.ProviderOptions.TryGetValue( "image", out var imgOpt )
            ? imgOpt
            : null;

        var capture = new OpenSearchEphemeralCapture(
            applyMigrations: async ( endpoint, upTo, ct ) =>
                await ApplyMigrationsViaHostAsync(
                    context.MigrationHost,
                    endpoint,
                    upTo,
                    ct ).ConfigureAwait( false ),
            provisioner: _provisioner );

        // DisableDirectStreaming matches the test container's ConnectionSettings
        // shape so the strategy + topology probe see byte-for-byte identical
        // SDK behavior whether invoked from the test container or the CLI
        // provider. DefaultIndex avoids the "Index name is null for the given
        // type" SDK error path on the typed surface; the squash codegen only
        // walks the migration ledger from the typed surface so "migrations"
        // is a reasonable default.
        var liveSettings = new ConnectionSettings( new Uri( context.ConnectionString ) )
            .DisableDirectStreaming()
            .DefaultIndex( "migrations" );
        var liveClient = new OpenSearchClient( liveSettings );

        var ctx = new OpenSearchSquashGenerationContext(
            squashName: context.SquashName,
            squashVersion: context.ToVersion,
            client: liveClient,
            captureSnapshotAsync: ( req, ct ) => capture.CaptureAsync( req, image, ct ) );

        var canonicalizer = new OpenSearchSnapshotCanonicalizer();
        var dataOpClassifier = new OpenSearchDataOpClassifier();
        var strategy = new RestStateDiffStrategy( canonicalizer, dataOpClassifier );

        return await strategy.GenerateAsync( ctx, context.Descriptors, context.Options )
            .ConfigureAwait( false );
    }

    public IReadOnlyList<MigrationSourceVerdict> ScanSource( string sourceDirectory )
    {
        if ( string.IsNullOrWhiteSpace( sourceDirectory ) )
            return Array.Empty<MigrationSourceVerdict>();

        var verdicts = OpenSearchMigrationSourceScanner.Scan( sourceDirectory );
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
        // RB-3: read ledger index name from per-environment overrides.
        // Default matches OpenSearchMigrationOptions: `migrations`.
        var ledgerIndex = providerOptions != null
            && providerOptions.TryGetValue( "ledger-index", out var idx )
            && !string.IsNullOrWhiteSpace( idx )
            ? idx
            : "migrations";

        var settings = new ConnectionSettings( new Uri( liveConnectionString ) );
        var client = new OpenSearchClient( settings );

        // Probe via search: aggregate the max version embedded in record_id
        // (`record.<version>.<name>`). Use a script-aggregated max over the
        // parsed version. Simpler approach: page through all docs sorted by
        // the leading numeric token. For migration ledgers (typically << 1000
        // entries) a single search with size=10000 is sufficient; if that
        // ceiling matters we'd switch to scroll. v3.0 ships size=10000.
        var response = await client.SearchAsync<MigrationLedgerDoc>( s => s
            .Index( ledgerIndex )
            .Size( 10000 )
            .Source( false )
            .StoredFields( new[] { "_id" } ),
            cancellationToken ).ConfigureAwait( false );

        if ( !response.IsValid )
        {
            // Index not found = empty ledger.
            if ( response.ApiCall?.HttpStatusCode == 404 )
                return 0;
            // Any other failure: bubble up with a clearer message.
            throw new InvalidOperationException(
                $"OpenSearch fleet probe failed: HTTP {response.ApiCall?.HttpStatusCode}, " +
                $"{response.OriginalException?.Message ?? response.DebugInformation}" );
        }

        long highest = 0;
        foreach ( var hit in response.Hits )
        {
            var id = hit.Id;
            if ( string.IsNullOrEmpty( id ) ) continue;
            if ( !id.StartsWith( "record.", StringComparison.Ordinal ) ) continue;
            var afterPrefix = id.Substring( "record.".Length );
            var dot = afterPrefix.IndexOf( '.' );
            if ( dot <= 0 ) continue;
            if ( !long.TryParse( afterPrefix.Substring( 0, dot ), out var version ) ) continue;
            if ( version > highest ) highest = version;
        }
        return highest;
    }

    // Stub document type for hit deserialization; we only read _id.
    private sealed class MigrationLedgerDoc { }

    private static async Task ApplyMigrationsViaHostAsync(
        IMigrationHost host,
        Uri endpoint,
        long upToVersion,
        CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( host );

        var ctx = new MigrationHostContext( endpoint.ToString() )
        {
            OverrideOptions = opts => opts.ToVersion = upToVersion
        };

        var serviceProvider = await host.ConfigureAsync( ctx, cancellationToken ).ConfigureAwait( false );
        try
        {
            var runner = serviceProvider.GetRequiredService<OpenSearchMigrationRunner>();
            await runner.RunAsync( cancellationToken ).ConfigureAwait( false );
        }
        finally
        {
            if ( serviceProvider is IAsyncDisposable iad ) await iad.DisposeAsync().ConfigureAwait( false );
            else if ( serviceProvider is IDisposable d ) d.Dispose();
        }
    }
}
