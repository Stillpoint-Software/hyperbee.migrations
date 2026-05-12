using Microsoft.Extensions.DependencyInjection;

namespace Hyperbee.Migrations;

/// <summary>
/// Internal marker registered the first time any <c>Add{Provider}Migrations</c>
/// extension method is called on an <see cref="IServiceCollection"/>.
/// The <see cref="RegistrationExtensions.RegisterBaseAliases"/> helper inspects
/// this marker to decide whether to register the legacy single-provider aliases
/// (<see cref="MigrationRunner"/>, <see cref="MigrationOptions"/>, <see cref="IMigrationRecordStore"/>)
/// pointing at the first provider, or replace them with fail-loud throwing
/// factories naming both providers (per ADR-0023 + assessment F1).
/// </summary>
internal sealed class MultiProviderRegistrationMarker
{
    /// <summary>Name of the first provider registered (e.g. "Postgres").</summary>
    public required string FirstProvider { get; init; }

    /// <summary>
    /// Names of every provider that has registered. Populated as additional
    /// <c>Add{Provider}Migrations</c> calls occur on the same service
    /// collection so the throwing factory messages name every offending
    /// provider, not just two.
    /// </summary>
    public required List<string> AllProviders { get; init; }

    /// <summary>
    /// Identity of the <see cref="MigrationOptions"/> alias descriptor the
    /// helper installed on the first provider registration, captured by
    /// reference so the second-provider flip can remove only the helper-owned
    /// descriptor and leave user-supplied registrations alone (R-9 per ADR-0024
    /// audit follow-up).
    /// </summary>
    public ServiceDescriptor InstalledOptionsAlias { get; set; }

    /// <summary>Helper-owned <see cref="IMigrationRecordStore"/> alias descriptor.</summary>
    public ServiceDescriptor InstalledStoreAlias { get; set; }

    /// <summary>Helper-owned <see cref="MigrationRunner"/> alias descriptor.</summary>
    public ServiceDescriptor InstalledRunnerAlias { get; set; }
}
