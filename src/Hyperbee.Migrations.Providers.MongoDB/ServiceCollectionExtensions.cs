using System.Reflection;
using System.Runtime.Loader;
using Hyperbee.Migrations.Providers.MongoDB.Resources;
using Hyperbee.Migrations.Providers.MongoDB.Squash;
using Hyperbee.Migrations.Squash;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;
using IMongoClient = MongoDB.Driver.IMongoClient;

namespace Hyperbee.Migrations.Providers.MongoDB;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddMongoDBMigrations( this IServiceCollection services )
    {
        return AddMongoDBMigrations( services, null, Assembly.GetCallingAssembly() );
    }

    public static IServiceCollection AddMongoDBMigrations( this IServiceCollection services, Action<MongoDBMigrationOptions> configuration )
    {
        return AddMongoDBMigrations( services, configuration, Assembly.GetCallingAssembly() );
    }

    private static IServiceCollection AddMongoDBMigrations( IServiceCollection services, Action<MongoDBMigrationOptions> configuration, Assembly defaultAssembly )
    {
        MongoDBMigrationOptions PostgresMigrationOptionsFactory( IServiceProvider provider )
        {
            var options = new MongoDBMigrationOptions( new DefaultMigrationActivator( provider ) );

            // invoke the configuration

            configuration?.Invoke( options );

            // concat any options.Assemblies with IConfiguration `FromAssemblies` and

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

        // Concrete provider-typed registrations (per ADR-0023). Stored via
        // TryAddSingleton + factory delegate so duplicate AddMongoDBMigrations
        // calls are idempotent and the internal record-store type stays
        // internal.
        services.TryAddSingleton( PostgresMigrationOptionsFactory );
        services.TryAddSingleton<MongoDBRecordStore>( provider => new MongoDBRecordStore(
            provider.GetRequiredService<IMongoClient>(),
            provider.GetRequiredService<MongoDBMigrationOptions>(),
            provider.GetRequiredService<ILogger<MongoDBRecordStore>>() ) );
        services.TryAddSingleton( provider => new MongoDBMigrationRunner(
            provider.GetRequiredService<MongoDBRecordStore>(),
            provider.GetRequiredService<MongoDBMigrationOptions>(),
            provider.GetRequiredService<ILoggerFactory>() ) );

        // Legacy single-provider aliases (per ADR-0023 amendment F1).
        services.RegisterBaseAliases(
            "MongoDB",
            provider => provider.GetRequiredService<MongoDBMigrationOptions>(),
            provider => provider.GetRequiredService<MongoDBRecordStore>(),
            provider => provider.GetRequiredService<MongoDBMigrationRunner>() );

        services.AddTransient( typeof( MongoDBResourceRunner<> ) );// technically singleton works because of the nature of migrations, but even so ..

        // Squash codegen wiring (per ADR-0019). Components are stateless and
        // idempotent; safe singletons.
        services.TryAddSingleton<MongoDBSnapshotCanonicalizer>();
        services.TryAddSingleton<MongoDBDataOpClassifier>();
        services.TryAddSingleton( provider => new IntrospectionSnapshotStrategy(
            provider.GetRequiredService<MongoDBSnapshotCanonicalizer>(),
            provider.GetRequiredService<MongoDBDataOpClassifier>(),
            provider.GetService<ILogger<IntrospectionSnapshotStrategy>>() ) );
        services.TryAddSingleton<MongoDBSquashVerifier>();
        services.TryAddSingleton( provider =>
        {
            // Default topology instance for descriptor composition;
            // live CaptureAsync overrides at runtime.
            ITopologySignature topology = new MongoDBTopologySignature();
            var descriptor = new SquashStrategyDescriptor(
                TopologySignature: topology,
                DataOpClassifier: provider.GetRequiredService<MongoDBDataOpClassifier>(),
                Generator: provider.GetRequiredService<IntrospectionSnapshotStrategy>(),
                Verifier: provider.GetRequiredService<MongoDBSquashVerifier>(),
                Canonicalizer: provider.GetRequiredService<MongoDBSnapshotCanonicalizer>() );
            descriptor.EnsureValid();
            return descriptor;
        } );

        return services;
    }

    private static IEnumerable<T> GetEnumerable<T>( this IConfiguration config, string key )
        => config.GetSection( key ).Get<IEnumerable<T>>() ?? [];
}
