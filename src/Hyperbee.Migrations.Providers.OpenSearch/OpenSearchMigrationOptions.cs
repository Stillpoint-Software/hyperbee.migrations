namespace Hyperbee.Migrations.Providers.OpenSearch;

public enum ClusterHealthThreshold
{
    Yellow,
    Green
}

public enum WaitMode
{
    PerStatement,
    PerMigration,
    Off
}

public enum ContextResolutionPolicy
{
    SkipIfUnset,
    RequireExplicit
}

public class OpenSearchMigrationOptions : MigrationOptions
{
    public const string DefaultLedgerIndex = ".migrations";
    public const string DefaultLockIndex = ".migrations-lock";
    public const string DefaultLockName = "migration_lock";

    public string LedgerIndex { get; set; } = DefaultLedgerIndex;
    public string LockIndex { get; set; } = DefaultLockIndex;
    public string LockName { get; set; } = DefaultLockName;

    public ClusterHealthThreshold ClusterHealthThreshold { get; set; } = ClusterHealthThreshold.Yellow;
    public WaitMode WaitMode { get; set; } = WaitMode.PerStatement;
    public bool RequireUnsafeJustification { get; set; } = false;
    public ContextResolutionPolicy ContextResolutionPolicy { get; set; } = ContextResolutionPolicy.SkipIfUnset;

    public string ActiveContext { get; set; }
    public bool AssumeIndicesExist { get; set; } = false;

    public TimeSpan ImplicitWaitTimeout { get; set; } = TimeSpan.FromSeconds( 30 );

    // Heartbeat renewal interval. Must be shorter than LockStaleAfter so a healthy
    // runner refreshes the lock before takeover candidates would consider it stale.
    public TimeSpan LockRenewInterval { get; set; } = TimeSpan.FromSeconds( 30 );

    // After this duration without renewal, the lock is considered stale and another
    // runner may take it over. Validation enforces LockStaleAfter >= 2 * LockRenewInterval
    // and LockStaleAfter < LockMaxLifetime.
    public TimeSpan LockStaleAfter { get; set; } = TimeSpan.FromSeconds( 60 );

    // Hard ceiling on total lock lifetime. When reached, in-flight migration is
    // cancelled (CancellationToken signaled) and surfaces MigrationLockExpiredException.
    public TimeSpan LockMaxLifetime { get; set; } = TimeSpan.FromHours( 1 );

    public OpenSearchMigrationOptions()
        : this( null )
    {
    }

    public OpenSearchMigrationOptions( IMigrationActivator migrationActivator )
        : base( migrationActivator )
    {
    }
}
