using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Reflection;
using System.Runtime.Loader;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hyperbee.Migrations.Couchbase
{
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
                    .GetSection( "Migrations:FromAssemblies" )
                    .GetArray<string>()
                    .Select( name => Assembly.Load( new AssemblyName( name ) ) );

                var pathAssemblies = config
                    .GetSection( "Migrations:FromPaths" )
                    .GetArray<string>()
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

            return services;
        }

        private static T[] GetArray<T>( this IConfiguration section ) => section.Get<T[]>() ?? Array.Empty<T>();
    }
}