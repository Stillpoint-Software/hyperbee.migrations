namespace Hyperbee.Migrations.Providers.OpenSearch;

/// <summary>
/// Cluster-health gate threshold the runner waits for before executing migrations.
/// </summary>
public enum ClusterHealthThreshold
{
    /// <summary>All primary shards active; some replicas may still be initializing.</summary>
    Yellow,
    /// <summary>All primary and replica shards active.</summary>
    Green
}

/// <summary>
/// Controls when the runner blocks on cluster wait-conditions (health, task
/// completion) emitted by migration statements.
/// </summary>
public enum WaitMode
{
    /// <summary>Wait after every statement that emits a wait-condition. Safest; default.</summary>
    PerStatement,
    /// <summary>Wait once at the end of each migration. Faster; assumes statements within a migration commute.</summary>
    PerMigration,
    /// <summary>Never wait. Author owns synchronization; suitable for tests and fast-path scripts.</summary>
    Off
}

/// <summary>
/// Behavior when a migration references <c>$context</c> but no
/// <see cref="OpenSearchMigrationOptions.ActiveContext"/> is configured.
/// </summary>
public enum ContextResolutionPolicy
{
    /// <summary>Skip the migration silently when context is unset.</summary>
    SkipIfUnset,
    /// <summary>Fail the run with a clear error when context is unset.</summary>
    RequireExplicit
}

/// <summary>
/// OpenSearch-provider configuration for the migration runner. Controls ledger and
/// lock-index naming, cluster wait behavior, lock heartbeat cadence, and runner-level
/// safety knobs (UNSAFE justification, partial-rollback resume).
/// </summary>
public class OpenSearchMigrationOptions : MigrationOptions
{
    /// <summary>Default name for the migration ledger index.</summary>
    public const string DefaultLedgerIndex = ".migrations";

    /// <summary>Default name for the migration lock index.</summary>
    public const string DefaultLockIndex = ".migrations-lock";

    /// <summary>Default <c>_id</c> of the singleton lock document inside the lock index.</summary>
    public const string DefaultLockName = "migration_lock";

    /// <summary>
    /// Index storing applied-migration records. Created on first run with a strict
    /// mapping; re-verified on subsequent runs.
    /// </summary>
    public string LedgerIndex { get; set; } = DefaultLedgerIndex;

    /// <summary>
    /// Index storing the singleton lock document (<c>number_of_replicas: 0</c> per
    /// PA-2 to eliminate replica-write coupling on the lock primary shard).
    /// </summary>
    public string LockIndex { get; set; } = DefaultLockIndex;

    /// <summary>
    /// <c>_id</c> of the lock document inside <see cref="LockIndex"/>. Override to run
    /// multiple independent migration scopes against the same cluster.
    /// </summary>
    public string LockName { get; set; } = DefaultLockName;

    /// <summary>Cluster-health threshold the runner waits for at startup and after schema-mutating statements.</summary>
    public ClusterHealthThreshold ClusterHealthThreshold { get; set; } = ClusterHealthThreshold.Yellow;

    /// <summary>When the runner blocks on cluster wait-conditions; see <see cref="WaitMode"/>.</summary>
    public WaitMode WaitMode { get; set; } = WaitMode.PerStatement;

    /// <summary>
    /// When true, <c>UNSAFE</c>-modified statements must include a non-empty justification
    /// string. Recommended for production; off by default to keep test scripts terse.
    /// </summary>
    public bool RequireUnsafeJustification { get; set; } = false;

    /// <summary>How the runner reacts when a migration references <c>$context</c> but <see cref="ActiveContext"/> is unset.</summary>
    public ContextResolutionPolicy ContextResolutionPolicy { get; set; } = ContextResolutionPolicy.SkipIfUnset;

    /// <summary>
    /// Active context label substituted for <c>$context</c> in migrations. Typically set
    /// from environment (dev/staging/prod) so the same migration set targets the right cluster shape.
    /// </summary>
    public string ActiveContext { get; set; }

    /// <summary>
    /// When true, the bootstrapper verifies the ledger and lock indices exist with the
    /// required mapping but does not create them. Use in tightly-scoped IAM contexts
    /// (e.g., AWS Managed) where the deploy role lacks <c>indices:admin/create</c>.
    /// </summary>
    public bool AssumeIndicesExist { get; set; } = false;

    // ForceResume is inherited from MigrationOptions (promoted to the base per
    // ADR-0027 so the core up-path interruption lockout and the OpenSearch
    // down-path partial-rollback lockout share one operator opt-in flag). The
    // down-path R-19 lockout reads the same inherited property.

    /// <summary>Timeout for implicit cluster waits emitted by migration statements (e.g., wait-for-status, wait-for-task).</summary>
    public TimeSpan ImplicitWaitTimeout { get; set; } = TimeSpan.FromSeconds( 30 );

    /// <summary>
    /// Heartbeat renewal interval. Must be shorter than <see cref="LockStaleAfter"/>
    /// so a healthy runner refreshes the lock before takeover candidates would
    /// consider it stale.
    /// </summary>
    public TimeSpan LockRenewInterval { get; set; } = TimeSpan.FromSeconds( 30 );

    /// <summary>
    /// After this duration without renewal, the lock is considered stale and another
    /// runner may take it over. Validation enforces
    /// <c>LockStaleAfter &gt;= 2 * LockRenewInterval</c> and
    /// <c>LockStaleAfter &lt; LockMaxLifetime</c>.
    /// </summary>
    public TimeSpan LockStaleAfter { get; set; } = TimeSpan.FromSeconds( 60 );

    /// <summary>
    /// Hard ceiling on total lock lifetime. When reached, the in-flight migration is
    /// cancelled (its <see cref="System.Threading.CancellationToken"/> is signaled) and
    /// the runner surfaces <c>MigrationLockExpiredException</c>.
    /// </summary>
    public TimeSpan LockMaxLifetime { get; set; } = TimeSpan.FromHours( 1 );

    /// <summary>Initializes a new instance with no migration activator.</summary>
    public OpenSearchMigrationOptions()
        : this( null )
    {
    }

    /// <summary>Initializes a new instance with the supplied <paramref name="migrationActivator"/>.</summary>
    public OpenSearchMigrationOptions( IMigrationActivator migrationActivator )
        : base( migrationActivator )
    {
    }
}
