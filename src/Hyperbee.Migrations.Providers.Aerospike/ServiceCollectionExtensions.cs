using System.Reflection;
using System.Runtime.Loader;
using Hyperbee.Migrations.Providers.Aerospike.Resources;
using Hyperbee.Migrations.Providers.Aerospike.Squash;
using Hyperbee.Migrations.Squash;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using IAsyncClient = Aerospike.Client.IAsyncClient;

namespace Hyperbee.Migrations.Providers.Aerospike;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddAerospikeMigrations( this IServiceCollection services )
    {
        return AddAerospikeMigrations( services, null, Assembly.GetCallingAssembly() );
    }

    public static IServiceCollection AddAerospikeMigrations( this IServiceCollection services, Action<AerospikeMigrationOptions> configuration )
    {
        return AddAerospikeMigrations( services, configuration, Assembly.GetCallingAssembly() );
    }

    private static IServiceCollection AddAerospikeMigrations( IServiceCollection services, Action<AerospikeMigrationOptions> configuration, Assembly defaultAssembly )
    {
        AerospikeMigrationOptions AerospikeMigrationOptionsFactory( IServiceProvider provider )
        {
            var options = new AerospikeMigrationOptions( new DefaultMigrationActivator( provider ) );

            // invoke the configuration

            configuration?.Invoke( options );

            // concat any options.Assemblies with IConfiguration `FromAssemblies` and `FromPaths`

            var config = provider.GetRequiredService<IConfiguration>();

            var nameAssemblies = config
                .GetEnumerable<string>( "Migrations:FromAssemblies" )
                .Select( name => Assembly.Load( new AssemblyName( name ) ) );

            var pathAssemblies = config
                .GetEnumerable<string>( "Migrations:FromPaths" )
                .Select( name => AssemblyLoadContext.Default.LoadFromAssemblyPath( Path.GetFullPath( name ) ) );

            options.Assemblies = options.Assemblies
                .Concat( nameAssemblies )
                .Concat( pathAssemblies )
                .Distinct()
                .DefaultIfEmpty( defaultAssembly )
                .ToList();

            return options;
        }

        services.TryAddSingleton( TimeProvider.System );

        // Concrete provider-typed registrations (per ADR-0023). TryAddSingleton
        // + factory delegate -- idempotent registration; record-store stays
        // internal.
        services.TryAddSingleton( AerospikeMigrationOptionsFactory );
        services.TryAddSingleton<AerospikeRecordStore>( provider => new AerospikeRecordStore(
            provider.GetRequiredService<IAsyncClient>(),
            provider.GetRequiredService<AerospikeMigrationOptions>(),
            provider.GetRequiredService<TimeProvider>(),
            provider.GetRequiredService<ILogger<AerospikeRecordStore>>() ) );
        services.TryAddSingleton( provider => new AerospikeMigrationRunner(
            provider.GetRequiredService<AerospikeRecordStore>(),
            provider.GetRequiredService<AerospikeMigrationOptions>(),
            provider.GetRequiredService<ILoggerFactory>() ) );

        // Legacy single-provider aliases (per ADR-0023 amendment F1).
        services.RegisterBaseAliases(
            "Aerospike",
            provider => provider.GetRequiredService<AerospikeMigrationOptions>(),
            provider => provider.GetRequiredService<AerospikeRecordStore>(),
            provider => provider.GetRequiredService<AerospikeMigrationRunner>() );

        services.AddTransient( typeof( AerospikeResourceRunner<> ) );

        // Squash codegen wiring (per ADR-0019).
        //
        // Component instances are stateless and idempotent; safe to register
        // as singletons. The strategy receives an optional ILogger if one is
        // present in the container (consumers without logging configured fall
        // back to NullLogger via the strategy's constructor default).
        services.TryAddSingleton<AerospikeSnapshotCanonicalizer>();
        services.TryAddSingleton<AerospikeDataOpClassifier>();
        services.TryAddSingleton( provider => new InfoSnapshotStrategy(
            provider.GetRequiredService<AerospikeSnapshotCanonicalizer>(),
            provider.GetRequiredService<AerospikeDataOpClassifier>(),
            provider.GetService<ILogger<InfoSnapshotStrategy>>() ) );
        services.TryAddSingleton<AerospikeSquashVerifier>();
        services.TryAddSingleton( provider =>
        {
            // Topology signature ships a default instance ONLY for descriptor
            // composition; live CaptureAsync overrides the Properties bag
            // when the strategy actually runs against a cluster.
            ITopologySignature topology = new AerospikeTopologySignature();
            var descriptor = new SquashStrategyDescriptor(
                TopologySignature: topology,
                DataOpClassifier: provider.GetRequiredService<AerospikeDataOpClassifier>(),
                Generator: provider.GetRequiredService<InfoSnapshotStrategy>(),
                Verifier: provider.GetRequiredService<AerospikeSquashVerifier>(),
                Canonicalizer: provider.GetRequiredService<AerospikeSnapshotCanonicalizer>() );
            descriptor.EnsureValid();
            return descriptor;
        } );

        return services;
    }

    private static IEnumerable<T> GetEnumerable<T>( this IConfiguration config, string key )
        => config.GetSection( key ).Get<IEnumerable<T>>() ?? [];
}
