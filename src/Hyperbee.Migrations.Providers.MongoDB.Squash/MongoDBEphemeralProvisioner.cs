using Hyperbee.Migrations.Squash;
using Testcontainers.MongoDb;

namespace Hyperbee.Migrations.Providers.MongoDB.Squash;

/// <summary>
/// Default MongoDB <see cref="IEphemeralProvisioner"/>: spins an ephemeral
/// <c>mongo</c> container via <see cref="MongoDbBuilder"/>. Reads
/// <c>image</c> from hints; defaults to <c>mongo:7</c>.
/// </summary>
public sealed class MongoDBEphemeralProvisioner : IEphemeralProvisioner
{
    public async Task<IEphemeralFixture> ProvisionAsync(
        IReadOnlyDictionary<string, string> hints,
        CancellationToken cancellationToken )
    {
        var image = hints != null && hints.TryGetValue( "image", out var img ) && !string.IsNullOrWhiteSpace( img )
            ? img
            : "mongo:7";

        var container = new MongoDbBuilder( image )
            .WithCleanUp( true )
            .Build();

        await container.StartAsync( cancellationToken ).ConfigureAwait( false );
        return new MongoDBEphemeralFixture( container );
    }
}

/// <summary>
/// MongoDB-specific fixture exposing the underlying
/// <see cref="MongoDbContainer"/>. Disposal tears down the container.
/// </summary>
public sealed class MongoDBEphemeralFixture : IEphemeralFixture
{
    public MongoDbContainer Container { get; }
    public string ConnectionString { get; }
    public IReadOnlyDictionary<string, string> Metadata { get; }

    internal MongoDBEphemeralFixture( MongoDbContainer container )
    {
        Container = container;
        ConnectionString = container.GetConnectionString();
        Metadata = new Dictionary<string, string>( StringComparer.OrdinalIgnoreCase );
    }

    public ValueTask DisposeAsync() => Container.DisposeAsync();
}
