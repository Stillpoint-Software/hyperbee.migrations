using Hyperbee.Migrations.Providers.MongoDB.Squash;
using Hyperbee.Migrations.Squash;
using Microsoft.Extensions.DependencyInjection;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Hyperbee.Migrations.Providers.MongoDB.Squash;

/// <summary>
/// MongoDB implementation of <see cref="ISquashProvider"/>. Discovered by
/// the CLI via the migration assembly's reference closure (per ADR-0024).
/// </summary>
public sealed class MongoDBSquashProvider : ISquashProvider
{
    private readonly IEphemeralProvisioner _provisioner;

    public string ProviderId => "mongodb";
    public string SquashFileExtension => ".statements";

    public MongoDBSquashProvider()
        : this( provisioner: null )
    {
    }

    public MongoDBSquashProvider( IEphemeralProvisioner provisioner )
    {
        _provisioner = provisioner ?? new MongoDBEphemeralProvisioner();
    }

    public async Task<SquashGenerationResult> GenerateAsync(
        SquashRequest context,
        CancellationToken cancellationToken )
    {
        ArgumentNullException.ThrowIfNull( context );

        var databaseName = context.ProviderOptions != null
            && context.ProviderOptions.TryGetValue( "database-name", out var db )
            && !string.IsNullOrWhiteSpace( db )
            ? db
            : ExtractDatabaseFromConnection( context.ConnectionString )
                ?? throw new InvalidOperationException(
                    "MongoDB squash codegen requires a target database. Supply " +
                    "--provider-option database-name=<name> or include the database " +
                    "name in the connection string path." );

        var image = context.ProviderOptions != null
            && context.ProviderOptions.TryGetValue( "image", out var imgOpt )
            ? imgOpt
            : null;

        var capture = new MongoDBEphemeralCapture(
            applyMigrations: async ( connStr, upTo, ct ) =>
                await ApplyMigrationsViaHostAsync(
                    context.MigrationHost,
                    connStr,
                    upTo,
                    ct ).ConfigureAwait( false ),
            provisioner: _provisioner );

        var liveClient = new MongoClient( context.ConnectionString );

        var ctx = new MongoDBSquashGenerationContext(
            squashName: context.SquashName,
            squashVersion: context.ToVersion,
            client: liveClient,
            databaseName: databaseName,
            captureSnapshotAsync: ( req, ct ) => capture.CaptureAsync( req, databaseName, image, ct ) );

        var canonicalizer = new MongoDBSnapshotCanonicalizer();
        var dataOpClassifier = new MongoDBDataOpClassifier();
        var strategy = new IntrospectionSnapshotStrategy( canonicalizer, dataOpClassifier );

        return await strategy.GenerateAsync( ctx, context.Descriptors, context.Options )
            .ConfigureAwait( false );
    }

    public IReadOnlyList<MigrationSourceVerdict> ScanSource( string sourceDirectory )
    {
        if ( string.IsNullOrWhiteSpace( sourceDirectory ) )
            return Array.Empty<MigrationSourceVerdict>();

        var verdicts = MongoDBMigrationSourceScanner.Scan( sourceDirectory );
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
        // RB-3: read database + collection from per-environment overrides.
        var databaseName = providerOptions != null
            && providerOptions.TryGetValue( "database-name", out var db )
            && !string.IsNullOrWhiteSpace( db )
            ? db
            : ExtractDatabaseFromConnection( liveConnectionString )
                ?? throw new InvalidOperationException(
                    "MongoDB fleet probe requires the target database name. Supply " +
                    "`database-name` in the fleet manifest topology overrides." );
        var collectionName = providerOptions != null
            && providerOptions.TryGetValue( "collection-name", out var coll )
            && !string.IsNullOrWhiteSpace( coll )
            ? coll
            : "migrations";

        var client = new MongoClient( liveConnectionString );
        var database = client.GetDatabase( databaseName );

        try
        {
            var collection = database.GetCollection<BsonDocument>( collectionName );

            // Aggregate the max version from `record.<version>.<name>` ids
            // by extracting the leading numeric token after "record." and
            // taking the max. MongoDB aggregation $split + $toLong + $group.
            var pipeline = new BsonDocument[]
            {
                new( "$match",
                    new BsonDocument( "_id", new BsonDocument( "$regex", "^record\\.\\d+\\." ) ) ),
                new( "$project",
                    new BsonDocument( "version",
                        new BsonDocument( "$toLong",
                            new BsonDocument( "$arrayElemAt",
                                new BsonArray
                                {
                                    new BsonDocument( "$split",
                                        new BsonArray { "$_id", "." } ),
                                    1
                                } ) ) ) ),
                new( "$group",
                    new BsonDocument
                    {
                        { "_id", BsonNull.Value },
                        { "max", new BsonDocument( "$max", "$version" ) }
                    } )
            };

            var result = await collection
                .Aggregate<BsonDocument>( pipeline )
                .FirstOrDefaultAsync( cancellationToken )
                .ConfigureAwait( false );

            if ( result == null ) return 0;
            return result.TryGetValue( "max", out var maxVal )
                ? maxVal.ToInt64()
                : 0L;
        }
        catch ( MongoCommandException ex ) when ( ex.Code == 26 /* NamespaceNotFound */ )
        {
            return 0;
        }
    }

    private static string ExtractDatabaseFromConnection( string connectionString )
    {
        // mongodb://host:port/database
        var url = new MongoUrl( connectionString );
        return string.IsNullOrEmpty( url.DatabaseName ) ? null : url.DatabaseName;
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
            var runner = serviceProvider.GetRequiredService<MongoDBMigrationRunner>();
            await runner.RunAsync( cancellationToken ).ConfigureAwait( false );
        }
        finally
        {
            if ( serviceProvider is IAsyncDisposable iad ) await iad.DisposeAsync().ConfigureAwait( false );
            else if ( serviceProvider is IDisposable d ) d.Dispose();
        }
    }
}
