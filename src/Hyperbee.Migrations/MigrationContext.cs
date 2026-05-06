namespace Hyperbee.Migrations;

/// <summary>
/// Ambient context surfaced to a migration's <c>UpAsync</c>/<c>DownAsync</c>
/// while the runner is executing it. Carries the
/// <see cref="MigrationApplyMode"/> classification (per ADR-0019) so squash
/// migrations and seed-style code paths can branch on whether the run is a
/// fresh install or a partial catch-up.
/// </summary>
/// <remarks>
/// <para>
/// Access via <see cref="Current"/> from inside a migration's body. The runner
/// sets <see cref="Current"/> before calling <c>UpAsync</c> and clears it
/// afterward. <see cref="Current"/> is null when no migration is running.
/// </para>
/// <para>
/// Stored in an <see cref="AsyncLocal{T}"/> so that it flows across
/// <c>await</c> boundaries within a migration's body without leaking to
/// concurrent runner instances.
/// </para>
/// </remarks>
public sealed class MigrationContext
{
    private static readonly AsyncLocal<MigrationContext> _current = new();

    /// <summary>
    /// The current migration context, or null when no migration is running.
    /// </summary>
    public static MigrationContext Current => _current.Value;

    public MigrationApplyMode ApplyMode { get; init; }

    /// <summary>
    /// Convenience: returns true when <see cref="ApplyMode"/> is
    /// <see cref="MigrationApplyMode.Fresh"/>. Mirrors the historic
    /// <c>IsFreshInstall</c> idiom from other migration frameworks.
    /// </summary>
    public bool IsFreshInstall => ApplyMode == MigrationApplyMode.Fresh;

    internal static IDisposable Push( MigrationContext context )
    {
        var previous = _current.Value;
        _current.Value = context;
        return new Scope( previous );
    }

    private sealed class Scope : IDisposable
    {
        private readonly MigrationContext _previous;
        private int _disposed;

        public Scope( MigrationContext previous ) { _previous = previous; }

        public void Dispose()
        {
            if ( Interlocked.CompareExchange( ref _disposed, 1, 0 ) == 0 )
                _current.Value = _previous;
        }
    }
}
