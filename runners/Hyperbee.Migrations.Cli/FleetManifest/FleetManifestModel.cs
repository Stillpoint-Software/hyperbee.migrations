namespace Hyperbee.Migrations.Cli.FleetManifest;

/// <summary>
/// Parsed fleet manifest schema (per ADR-0019 A2 + A9 + Phase 7 Task 7.2).
/// Loaded from YAML by <see cref="FleetManifestLoader"/>; environment-variable
/// substitution is performed during load. The CLI uses the parsed manifest
/// to drive the fleet readiness check and to populate
/// <see cref="Hyperbee.Migrations.Squash.SquashMetadata.ExpectedFleetVersions"/>.
/// </summary>
public sealed class FleetManifestModel
{
    public List<FleetEnvironment> Fleet { get; set; } = new();
    public SquashOverridesModel? SquashOverrides { get; set; }
    public Dictionary<string, Dictionary<string, string>> ProviderOptions { get; set; } = new();
}

public sealed class FleetEnvironment
{
    public string Name { get; set; } = "";
    public string Connection { get; set; } = "";
    public Dictionary<string, string> Topology { get; set; } = new();
}

public sealed class SquashOverridesModel
{
    public List<AcceptStrandingEntry> AcceptStranding { get; set; } = new();
}

public sealed class AcceptStrandingEntry
{
    public string Environment { get; set; } = "";
    public string TicketId { get; set; } = "";
    public string Owner { get; set; } = "";
    public string Reason { get; set; } = "";

    /// <summary>
    /// ISO-8601 expiry. Default 30 days from manifest load if omitted (per
    /// ADR-0019 A15). Hard cap 90 days enforced at validation time.
    /// </summary>
    public string? Expires { get; set; }
}
