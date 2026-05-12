using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hyperbee.Migrations;

/// <summary>
/// Internal DI plumbing shared by every <c>Add{Provider}Migrations</c>
/// extension. Per ADR-0023 amendment F1: the first provider to register
/// installs the legacy single-provider aliases (<see cref="MigrationRunner"/>,
/// <see cref="MigrationOptions"/>, <see cref="IMigrationRecordStore"/>); the
/// second provider replaces those aliases with throwing factories that name
/// all registered providers so a multi-provider host that resolves the base
/// types fails loudly with an actionable message rather than silently
/// shadowing one provider.
/// </summary>
internal static class RegistrationExtensions
{
    /// <summary>
    /// Wires the legacy single-provider aliases for <paramref name="providerName"/>.
    /// First call: registers <see cref="MultiProviderRegistrationMarker"/> +
    /// the three aliases. Subsequent calls: removes the aliases and replaces
    /// them with throwing factories that name every registered provider.
    /// </summary>
    public static void RegisterBaseAliases(
        this IServiceCollection services,
        string providerName,
        Func<IServiceProvider, MigrationOptions> optionsFactory,
        Func<IServiceProvider, IMigrationRecordStore> storeFactory,
        Func<IServiceProvider, MigrationRunner> runnerFactory )
    {
        ArgumentNullException.ThrowIfNull( services );
        ArgumentException.ThrowIfNullOrWhiteSpace( providerName );
        ArgumentNullException.ThrowIfNull( optionsFactory );
        ArgumentNullException.ThrowIfNull( storeFactory );
        ArgumentNullException.ThrowIfNull( runnerFactory );

        var existingMarker = FindMarkerDescriptor( services );

        if ( existingMarker == null )
        {
            // First provider on this service collection: install marker +
            // legacy aliases pointing at this provider. Capture each installed
            // descriptor by reference so the second-provider flip can remove
            // only the helper-owned descriptors (R-9). Any user-supplied
            // MigrationOptions / IMigrationRecordStore / MigrationRunner
            // registrations made before this call are preserved untouched.
            var marker = new MultiProviderRegistrationMarker
            {
                FirstProvider = providerName,
                AllProviders = new List<string> { providerName }
            };
            services.AddSingleton( marker );

            var optionsDesc = ServiceDescriptor.Singleton<MigrationOptions>( optionsFactory );
            var storeDesc = ServiceDescriptor.Singleton<IMigrationRecordStore>( storeFactory );
            var runnerDesc = ServiceDescriptor.Singleton<MigrationRunner>( runnerFactory );
            services.Add( optionsDesc );
            services.Add( storeDesc );
            services.Add( runnerDesc );

            marker.InstalledOptionsAlias = optionsDesc;
            marker.InstalledStoreAlias = storeDesc;
            marker.InstalledRunnerAlias = runnerDesc;
            return;
        }

        // Subsequent provider: replace the legacy aliases with throwing
        // factories naming all providers so multi-provider hosts cannot
        // silently shadow.
        var markerInstance = (MultiProviderRegistrationMarker) existingMarker.ImplementationInstance!;

        // Idempotent: if the same provider re-registers (e.g., two helper
        // methods both call AddPostgresMigrations) we should not flip into
        // multi-provider mode.
        if ( markerInstance.AllProviders.Contains( providerName, StringComparer.Ordinal ) )
            return;

        markerInstance.AllProviders.Add( providerName );

        // R-9: remove only the descriptors WE installed on the first
        // registration. Previously this was `RemoveAll<MigrationOptions>`,
        // which also destroyed any user-supplied MigrationOptions /
        // IMigrationRecordStore / MigrationRunner registrations made before
        // the first AddXxxMigrations call -- a test-harness footgun where a
        // bespoke fake store registered first vanished as soon as a real
        // provider was added.
        if ( markerInstance.InstalledOptionsAlias != null )
            services.Remove( markerInstance.InstalledOptionsAlias );
        if ( markerInstance.InstalledStoreAlias != null )
            services.Remove( markerInstance.InstalledStoreAlias );
        if ( markerInstance.InstalledRunnerAlias != null )
            services.Remove( markerInstance.InstalledRunnerAlias );

        markerInstance.InstalledOptionsAlias = null;
        markerInstance.InstalledStoreAlias = null;
        markerInstance.InstalledRunnerAlias = null;

        var providers = markerInstance.AllProviders.ToArray();
        services.AddSingleton<MigrationOptions>( _ => ThrowMultiProvider<MigrationOptions>( providers ) );
        services.AddSingleton<IMigrationRecordStore>( _ => ThrowMultiProvider<IMigrationRecordStore>( providers ) );
        services.AddSingleton<MigrationRunner>( _ => ThrowMultiProvider<MigrationRunner>( providers ) );
    }

    private static ServiceDescriptor FindMarkerDescriptor( IServiceCollection services )
    {
        for ( var i = 0; i < services.Count; i++ )
        {
            var d = services[i];
            if ( d.ServiceType == typeof( MultiProviderRegistrationMarker ) )
                return d;
        }
        return null;
    }

    private static T ThrowMultiProvider<T>( string[] providers )
    {
        var typeName = typeof( T ).Name;
        var providerList = string.Join( ", ", providers );
        throw new InvalidOperationException(
            $"Multiple migration providers ({providerList}) are registered on this service collection. " +
            $"The base type `{typeName}` cannot be resolved unambiguously. " +
            $"Resolve the typed runner instead -- e.g. `provider.GetRequiredService<{providers[0]}MigrationRunner>()`. " +
            "See ADR-0023 and docs/site/multi-provider-hosts.md for the multi-provider host pattern." );
    }
}
