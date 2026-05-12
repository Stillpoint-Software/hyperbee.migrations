using Hyperbee.Migrations.Providers.MongoDB.Squash;
using Hyperbee.Migrations.Squash;
using MongoDB.Driver;
using Testcontainers.MongoDb;

namespace Hyperbee.Migrations.Providers.MongoDB.SquashCli;

/// <summary>
/// MongoDB snapshot capture concrete: spins an ephemeral mongo:7 container,
/// applies migrations through the requested upper bound via the
/// caller-supplied delegate, then captures the section-headered snapshot via
/// <see cref="MongoDBSnapshotCapture"/>. Per ADR-0019 A18 the container is
/// torn down after each capture.
/// </summary>
public sealed class MongoDBEphemeralCapture : IAsyncDisposable
{
    private readonly Func<string, long, CancellationToken, Task> _applyMigrations;

    /// <param name="applyMigrations">
    /// Caller-supplied callback that applies the operator's migration
    /// assembly through the requested upper version. Takes (connectionString,
    /// upToVersion, ct). Typically wraps the discovered IMigrationHost's
    /// ConfigureAsync against a MongoDB client bound to the ephemeral
    /// container.
    /// </param>
    public MongoDBEphemeralCapture(
        Func<string, long, CancellationToken, Task> applyMigrations )
    {
        _applyMigrations = applyMigrations ?? throw new ArgumentNullException( nameof( applyMigrations ) );
    }

    public async Task<SnapshotCaptureResult> CaptureAsync(
        SnapshotCaptureRequest request,
        string databaseName,
        string image,
        CancellationToken cancellationToken )
    {
        var resolvedImage = string.IsNullOrWhiteSpace( image )
            ? "mongo:7"
            : image;

        var container = new MongoDbBuilder( resolvedImage )
            .WithCleanUp( true )
            .Build();

        try
        {
            await container.StartAsync( cancellationToken ).ConfigureAwait( false );

            var connectionString = container.GetConnectionString();

            await _applyMigrations( connectionString, request.UpToVersion, cancellationToken ).ConfigureAwait( false );

            var client = new MongoClient( connectionString );
            var blob = await MongoDBSnapshotCapture.CaptureAsync( client, databaseName, cancellationToken )
                .ConfigureAwait( false );

            return new SnapshotCaptureResult( blob );
        }
        finally
        {
            await container.DisposeAsync().ConfigureAwait( false );
        }
    }

    public ValueTask DisposeAsync() => ValueTask.CompletedTask;
}
