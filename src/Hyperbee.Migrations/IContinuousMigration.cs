namespace Hyperbee.Migrations;

public interface IContinuousMigration
{
    Task<bool> StartAsync( CancellationToken cancellationToken = default );
    Task<bool> StopAsync( CancellationToken cancellationToken = default );
}
