namespace Hyperbee.Migrations.Squash;

/// <summary>
/// Thrown by the runner at deploy time when the live environment isn't
/// listed in the squash's <see cref="SquashMetadata.ExpectedFleetVersions"/>
/// (per ADR-0019 A2). The two-phase fleet readiness gate's deploy-time half
/// requires every environment that runs the squash to have been registered
/// in the manifest at generation time.
/// </summary>
/// <remarks>
/// Operators encounter this when a new environment is provisioned after the
/// squash was generated, or when the runner's
/// <c>MigrationOptions.EnvironmentName</c> doesn't match the manifest entry
/// (typo / capitalization). Recovery: regenerate the squash with the current
/// fleet manifest (cheap when the snapshot A cache is warm per A4).
/// </remarks>
[Serializable]
public class UnregisteredEnvironmentException : MigrationException
{
    public string EnvironmentName { get; init; }
    public IReadOnlyList<string> RegisteredEnvironments { get; init; } = Array.Empty<string>();

    public UnregisteredEnvironmentException()
    : base( "Environment is not registered in the squash's fleet manifest." )
    {
    }

    public UnregisteredEnvironmentException( string message )
    : base( message )
    {
    }

    public UnregisteredEnvironmentException( string environmentName, IEnumerable<string> registered )
    : base( BuildMessage( environmentName, registered?.ToArray() ?? Array.Empty<string>() ) )
    {
        EnvironmentName = environmentName;
        RegisteredEnvironments = registered?.ToArray() ?? Array.Empty<string>();
    }

    private static string BuildMessage( string env, string[] registered )
    {
        var registeredText = registered.Length > 0
            ? string.Join( ", ", registered )
            : "<none>";
        return
            $"Environment `{env}` is not registered in the squash's fleet manifest " +
            $"(expected one of: [{registeredText}]). Per ADR-0019 the deploy is refused: " +
            "either register `" + env + "` in the manifest and regenerate the squash, " +
            "or correct `MigrationOptions.EnvironmentName` to match an existing entry.";
    }
}
