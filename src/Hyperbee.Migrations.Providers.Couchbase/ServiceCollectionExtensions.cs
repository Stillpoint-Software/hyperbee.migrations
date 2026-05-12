using System.Reflection;
using System.Runtime.Loader;
using Hyperbee.Migrations.Providers.Couchbase.Resources;
using Hyperbee.Migrations.Providers.Couchbase.Services;
using Hyperbee.Migrations.Providers.Couchbase.Squash;
using Hyperbee.Migrations.Squash;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Logging;

namespace Hyperbee.Migrations.Providers.Couchbase;

public static class ServiceCollectionExtensions
{
    public static IServiceCollection AddCouchbaseMigrations( this IServiceCollection services )
    {
        return AddCouchbaseMigrations( services, null, Assembly.GetCallingAssembly() );
    }

    public static IServiceCollection AddCouchbaseMigrations( this IServiceCollection services, Action<CouchbaseMigrationOptions> configuration )
    {
        return AddCouchbaseMigrations( services, configuration, Assembly.GetCallingAssembly() );
    }

    private static IServiceCollection AddCouchbaseMigrations( IServiceCollection services, Action<CouchbaseMigrationOptions> configuration, Assembly defaultAssembly )
    {
        CouchbaseMigrationOptions CouchbaseMigrationOptionsFactory( IServiceProvider provider )
        {
            var options = new CouchbaseMigrationOptions( new DefaultMigrationActivator( provider ) );

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

        services.AddSingleton( CouchbaseMigrationOptionsFactory );
        services.AddSingleton<MigrationOptions>( provider => provider.GetRequiredService<CouchbaseMigrationOptions>() );
        services.AddSingleton<IMigrationRecordStore, CouchbaseRecordStore>();
        services.AddSingleton<MigrationRunner>();

        services.AddTransient( typeof( CouchbaseResourceRunner<> ) ); // technically singleton works because of the nature of migrations, but even so ..

        // add support for calling couchbase web sdk api (outside the couchbase net sdk).

        services.AddScoped<CouchbaseAuthenticationHandler>();

        services.AddHttpClient();
        services.AddHttpClient<ICouchbaseRestApiService, CouchbaseRestApiService>()
            .AddHttpMessageHandler<CouchbaseAuthenticationHandler>();

        services.AddTransient<ICouchbaseBootstrapper, CouchbaseBootstrapper>();

        // Squash codegen wiring (per ADR-0019). Components are stateless and
        // idempotent; safe singletons.
        services.TryAddSingleton<CouchbaseSnapshotCanonicalizer>();
        services.TryAddSingleton<CouchbaseDataOpClassifier>();
        services.TryAddSingleton( provider => new HybridStrategy(
            provider.GetRequiredService<CouchbaseSnapshotCanonicalizer>(),
            provider.GetRequiredService<CouchbaseDataOpClassifier>(),
            provider.GetService<ILogger<HybridStrategy>>() ) );
        services.TryAddSingleton<CouchbaseSquashVerifier>();
        services.TryAddSingleton( provider =>
        {
            // Default topology instance for descriptor composition; live
            // CaptureAsync overrides at runtime.
            ITopologySignature topology = new CouchbaseTopologySignature();
            var descriptor = new SquashStrategyDescriptor(
                TopologySignature: topology,
                DataOpClassifier: provider.GetRequiredService<CouchbaseDataOpClassifier>(),
                Generator: provider.GetRequiredService<HybridStrategy>(),
                Verifier: provider.GetRequiredService<CouchbaseSquashVerifier>(),
                Canonicalizer: provider.GetRequiredService<CouchbaseSnapshotCanonicalizer>() );
            descriptor.EnsureValid();
            return descriptor;
        } );

        return services;
    }

    private static IEnumerable<T> GetEnumerable<T>( this IConfiguration config, string key )
        => config.GetSection( key ).Get<IEnumerable<T>>() ?? [];
}
