using System.Globalization;
using Hyperbee.Migrations.Squash;
using MongoDB.Bson;
using MongoDB.Driver;

namespace Hyperbee.Migrations.Providers.MongoDB.Squash;

/// <summary>
/// MongoDB topology signature per ADR-0019. Captures the deployment axes that
/// affect squash codegen determinism: server major/minor version, feature
/// compatibility version (FCV), deployment topology (standalone vs replica
/// set vs sharded), database name, default read/write concerns, and storage
/// engine.
/// </summary>
/// <remarks>
/// <para>
/// FCV is the Edition-axis analogue for MongoDB -- simpler than OpenSearch's
/// plugin matrix because FCV is a single scalar value, but with similar
/// load-bearing semantics: FCV gates index features (wildcard indexes,
/// time-series, etc.), aggregation operators, and BSON shape details that
/// affect snapshot output. An Enterprise-source-FCV squash applied to a
/// target running an older FCV silently passes structural compare then
/// fails at runtime when an FCV-gated operator runs.
/// </para>
/// <para>
/// <see cref="IsCompatibleWith"/> rules:
/// <list type="bullet">
///   <item>Server major must match exactly. Major-version differences can
///         change index defaults, aggregation operator availability, and
///         BSON serialization.</item>
///   <item>FCV must match exactly. FCV gates feature shape and affects
///         what BSON documents the server will accept.</item>
///   <item>Deployment topology must match exactly. Standalone vs replica
///         set vs sharded changes the available write concerns, index
///         build mechanics, and a number of operators.</item>
///   <item>Database name must match exactly (squash scope identifier).</item>
///   <item>Storage engine must match exactly. WiredTiger vs InMemory vs
///         legacy engines differ in supported features.</item>
/// </list>
/// Server minor differences are tolerated -- index defaults and BSON shape
/// are stable across minors within a major.
/// </para>
/// </remarks>
public sealed record MongoDBTopologySignature : ITopologySignature
{
    public const string ProviderIdValue = "mongodb";

    public int SchemaVersion => 1;
    public string ProviderId => ProviderIdValue;

    public int ServerMajor { get; init; }
    public int ServerMinor { get; init; }

    /// <summary>
    /// Feature compatibility version reported by <c>{getParameter: 1,
    /// featureCompatibilityVersion: 1}</c>. Typically a "Major.Minor" string
    /// (e.g., "7.0"). Empty when the cluster does not expose FCV or the
    /// caller lacks admin privileges.
    /// </summary>
    public string FeatureCompatibilityVersion { get; init; } = "";

    /// <summary>
    /// Deployment shape: <c>Standalone</c>, <c>ReplicaSet</c>, or
    /// <c>Sharded</c>. Inferred from the <c>hello</c> command response
    /// (<c>setName</c> present -> ReplicaSet, <c>msg == "isdbgrid"</c>
    /// -> Sharded, otherwise Standalone).
    /// </summary>
    public string DeploymentTopology { get; init; } = "";

    /// <summary>
    /// Replica set name when the deployment is a replica set, empty
    /// otherwise. Captured for operational diagnostics; not compared by
    /// <see cref="IsCompatibleWith"/> since the same logical replica set
    /// can have different names across environments.
    /// </summary>
    public string ReplicaSetName { get; init; } = "";

    public string DatabaseName { get; init; } = "";

    /// <summary>
    /// Default read concern level on the client (<c>local</c> /
    /// <c>majority</c> / <c>snapshot</c> / etc.). Compared because operator
    /// code paths may depend on the concern matching for read-after-write
    /// semantics.
    /// </summary>
    public string DefaultReadConcern { get; init; } = "";

    /// <summary>
    /// Default write concern w-value rendered as a string
    /// (<c>1</c> / <c>majority</c> / <c>2</c>). Empty when no client default
    /// is set.
    /// </summary>
    public string DefaultWriteConcern { get; init; } = "";

    /// <summary>
    /// Storage engine name (typically <c>wiredTiger</c>; legacy deployments
    /// may report <c>inMemory</c> or other engines). Empty when the operator
    /// lacks <c>serverStatus</c> privileges.
    /// </summary>
    public string StorageEngine { get; init; } = "";

    public IReadOnlyDictionary<string, string> Properties => new Dictionary<string, string>
    {
        ["server_major"] = ServerMajor.ToString( CultureInfo.InvariantCulture ),
        ["server_minor"] = ServerMinor.ToString( CultureInfo.InvariantCulture ),
        ["fcv"] = FeatureCompatibilityVersion,
        ["deployment_topology"] = DeploymentTopology,
        ["replica_set_name"] = ReplicaSetName,
        ["database_name"] = DatabaseName,
        ["default_read_concern"] = DefaultReadConcern,
        ["default_write_concern"] = DefaultWriteConcern,
        ["storage_engine"] = StorageEngine
    };

    public bool IsCompatibleWith( ITopologySignature other, out string reason )
    {
        if ( other is not MongoDBTopologySignature mo )
        {
            reason = $"signature is `{other?.ProviderId ?? "<null>"}`, not `{ProviderIdValue}`";
            return false;
        }

        if ( mo.ServerMajor != ServerMajor )
        {
            reason = $"server_major differs (this={ServerMajor}, other={mo.ServerMajor})";
            return false;
        }

        if ( !string.Equals( mo.FeatureCompatibilityVersion, FeatureCompatibilityVersion, StringComparison.Ordinal ) )
        {
            reason = $"fcv differs (this='{FeatureCompatibilityVersion}', other='{mo.FeatureCompatibilityVersion}')";
            return false;
        }

        if ( !string.Equals( mo.DeploymentTopology, DeploymentTopology, StringComparison.OrdinalIgnoreCase ) )
        {
            reason = $"deployment_topology differs (this='{DeploymentTopology}', other='{mo.DeploymentTopology}')";
            return false;
        }

        if ( !string.Equals( mo.DatabaseName, DatabaseName, StringComparison.Ordinal ) )
        {
            reason = $"database_name differs (this='{DatabaseName}', other='{mo.DatabaseName}')";
            return false;
        }

        if ( !string.Equals( mo.StorageEngine, StorageEngine, StringComparison.Ordinal ) )
        {
            reason = $"storage_engine differs (this='{StorageEngine}', other='{mo.StorageEngine}')";
            return false;
        }

        if ( !string.Equals( mo.DefaultReadConcern, DefaultReadConcern, StringComparison.Ordinal ) )
        {
            reason = $"default_read_concern differs (this='{DefaultReadConcern}', other='{mo.DefaultReadConcern}')";
            return false;
        }

        if ( !string.Equals( mo.DefaultWriteConcern, DefaultWriteConcern, StringComparison.Ordinal ) )
        {
            reason = $"default_write_concern differs (this='{DefaultWriteConcern}', other='{mo.DefaultWriteConcern}')";
            return false;
        }

        reason = null;
        return true;
    }

    /// <summary>
    /// Captures the live MongoDB cluster's topology axes via admin commands.
    /// Required probes (failure throws): <c>buildInfo</c> + <c>hello</c>.
    /// Optional probes (graceful degradation, recorded as empty when not
    /// available): <c>featureCompatibilityVersion</c>, <c>serverStatus</c>.
    /// Default read/write concerns are read from
    /// <see cref="IMongoClient.Settings"/> with no privilege requirement.
    /// </summary>
    public static async Task<MongoDBTopologySignature> CaptureAsync(
        IMongoClient client,
        string databaseName,
        CancellationToken cancellationToken = default )
    {
        if ( client == null )
            throw new ArgumentNullException( nameof( client ) );

        if ( string.IsNullOrWhiteSpace( databaseName ) )
            throw new ArgumentException( "Database name is required.", nameof( databaseName ) );

        cancellationToken.ThrowIfCancellationRequested();

        var admin = client.GetDatabase( "admin" );

        // Required: buildInfo for version.
        var buildInfo = await RunCommandAsync( admin, new BsonDocument( "buildInfo", 1 ), cancellationToken )
            .ConfigureAwait( false )
            ?? throw new MigrationException(
                "MongoDB topology capture failed: `buildInfo` command returned null. " +
                "Verify the cluster is reachable and the connection is authorized." );

        var (major, minor) = ParseVersionFromBuildInfo( buildInfo );

        // Required: hello for deployment topology.
        var hello = await RunCommandAsync( admin, new BsonDocument( "hello", 1 ), cancellationToken )
            .ConfigureAwait( false )
            ?? throw new MigrationException(
                "MongoDB topology capture failed: `hello` command returned null." );

        var (deploymentTopology, replicaSetName) = ParseDeploymentFromHello( hello );

        // Optional: FCV (requires admin privileges; some hosted clusters
        // restrict it).
        var fcv = "";
        try
        {
            var fcvDoc = await RunCommandAsync(
                admin,
                new BsonDocument
                {
                    { "getParameter", 1 },
                    { "featureCompatibilityVersion", 1 }
                },
                cancellationToken ).ConfigureAwait( false );
            fcv = ParseFcv( fcvDoc );
        }
        catch ( MongoCommandException )
        {
            // Lack of privilege; record empty FCV.
        }

        // Optional: serverStatus for storage engine.
        var storageEngine = "";
        try
        {
            var statusDoc = await RunCommandAsync(
                admin,
                new BsonDocument( "serverStatus", 1 ),
                cancellationToken ).ConfigureAwait( false );
            storageEngine = ParseStorageEngine( statusDoc );
        }
        catch ( MongoCommandException )
        {
            // Lack of privilege; record empty storage engine.
        }

        // Default concerns from client settings (no privilege required).
        var readConcern = client.Settings.ReadConcern?.Level?.ToString().ToLowerInvariant() ?? "";
        var writeConcern = FormatWriteConcern( client.Settings.WriteConcern );

        return new MongoDBTopologySignature
        {
            ServerMajor = major,
            ServerMinor = minor,
            FeatureCompatibilityVersion = fcv,
            DeploymentTopology = deploymentTopology,
            ReplicaSetName = replicaSetName,
            DatabaseName = databaseName,
            DefaultReadConcern = readConcern,
            DefaultWriteConcern = writeConcern,
            StorageEngine = storageEngine
        };
    }

    private static async Task<BsonDocument> RunCommandAsync(
        IMongoDatabase database,
        BsonDocument command,
        CancellationToken cancellationToken )
    {
        var commandObj = new BsonDocumentCommand<BsonDocument>( command );
        return await database.RunCommandAsync( commandObj, cancellationToken: cancellationToken )
            .ConfigureAwait( false );
    }

    // buildInfo returns:
    //   { "version": "7.0.5", "versionArray": [7, 0, 5, 0], ... }
    // versionArray is the authoritative numeric form. Prefer it over the
    // version string so pre-release suffixes ("-rc0") don't trip the parser.
    internal static (int major, int minor) ParseVersionFromBuildInfo( BsonDocument buildInfo )
    {
        if ( buildInfo.TryGetValue( "versionArray", out var versionArray )
             && versionArray is BsonArray array
             && array.Count >= 2 )
        {
            try
            {
                return (array[0].ToInt32(), array[1].ToInt32());
            }
            catch ( FormatException )
            {
                // Fall through to versionString parsing.
            }
            catch ( InvalidCastException )
            {
                // Fall through to versionString parsing.
            }
        }

        if ( buildInfo.TryGetValue( "version", out var versionString )
             && versionString.IsString )
        {
            return ParseVersionString( versionString.AsString );
        }

        throw new MigrationException( "MongoDB `buildInfo` response missing both `versionArray` and `version` fields." );
    }

    internal static (int major, int minor) ParseVersionString( string raw )
    {
        var parts = raw.Split( new[] { '.', '-', '+' }, 4 );
        if ( parts.Length < 2 ||
             !int.TryParse( parts[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out var major ) ||
             !int.TryParse( parts[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out var minor ) )
        {
            throw new MigrationException( $"MongoDB version string is not a recognized format: '{raw}'." );
        }
        return (major, minor);
    }

    // hello response shape:
    //   ReplicaSet:  { isWritablePrimary: true, setName: "rs0", ... }
    //   Sharded:     { isWritablePrimary: true, msg: "isdbgrid", ... }
    //   Standalone:  { isWritablePrimary: true, ... } (no setName, no msg=isdbgrid)
    internal static (string deploymentTopology, string replicaSetName) ParseDeploymentFromHello( BsonDocument hello )
    {
        if ( hello.TryGetValue( "msg", out var msg ) && msg.IsString
             && string.Equals( msg.AsString, "isdbgrid", StringComparison.Ordinal ) )
        {
            return ("Sharded", "");
        }

        if ( hello.TryGetValue( "setName", out var setName ) && setName.IsString && !string.IsNullOrEmpty( setName.AsString ) )
        {
            return ("ReplicaSet", setName.AsString);
        }

        return ("Standalone", "");
    }

    // FCV response shape:
    //   { featureCompatibilityVersion: { version: "7.0" }, ok: 1 }
    internal static string ParseFcv( BsonDocument fcvDoc )
    {
        if ( fcvDoc == null )
            return "";
        if ( !fcvDoc.TryGetValue( "featureCompatibilityVersion", out var fcv )
             || fcv is not BsonDocument fcvObj )
        {
            return "";
        }
        if ( !fcvObj.TryGetValue( "version", out var version ) || !version.IsString )
            return "";
        return version.AsString;
    }

    // serverStatus has a deeply-nested storageEngine.name field. The probe
    // can fail (lack of privilege); the caller catches and records empty.
    internal static string ParseStorageEngine( BsonDocument statusDoc )
    {
        if ( statusDoc == null )
            return "";
        if ( !statusDoc.TryGetValue( "storageEngine", out var engine )
             || engine is not BsonDocument engineObj )
        {
            return "";
        }
        if ( !engineObj.TryGetValue( "name", out var name ) || !name.IsString )
            return "";
        return name.AsString;
    }

    // WriteConcern can be configured as W=N (int), W="majority", or unset.
    // Format to a deterministic string for byte-stable signature.
    internal static string FormatWriteConcern( WriteConcern wc )
    {
        if ( wc == null )
            return "";
        if ( wc.W is WriteConcern.WValue wValue )
            return wValue.ToString() ?? "";
        return "";
    }
}
