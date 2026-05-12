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
            // legacy aliases pointing at this provider.
            var marker = new MultiProviderRegistrationMarker
            {
                FirstProvider = providerName,
                AllProviders = new List<string> { providerName }
            };
            services.AddSingleton( marker );

            services.AddSingleton<MigrationOptions>( optionsFactory );
            services.AddSingleton<IMigrationRecordStore>( storeFactory );
            services.AddSingleton<MigrationRunner>( runnerFactory );
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

        services.RemoveAll<MigrationOptions>();
        services.RemoveAll<IMigrationRecordStore>();
        services.RemoveAll<MigrationRunner>();

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
