using Couchbase.Extensions.DependencyInjection;
using Hyperbee.Migrations;
using Hyperbee.Migrations.Providers.Couchbase;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Logging;

namespace Hyperbee.Migrations.Couchbase.Samples;

/// <summary>
/// IMigrationHost implementation discovered by the CLI (per ADR-0024). Wires
/// the sample project's AddCouchbaseMigrations setup with the caller-supplied
/// connection string.
/// </summary>
public class CouchbaseMigrationsHost : IMigrationHost
{
    public Task<IServiceProvider> ConfigureAsync(
        MigrationHostContext context, CancellationToken cancellationToken = default )
    {
        var services = new ServiceCollection();
        services.AddSingleton<IConfiguration>( new ConfigurationBuilder().Build() );
        services.AddLogging();

        // mgmt-port hint plumbed in by CouchbaseSquashProvider when
        // the ephemeral container's management port differs from the
        // 8091 default (any Testcontainers-style mapped-port deployment).
        // CouchbaseRestApiService now always uses BootstrapHttpPort for
        // REST calls (the URI's port is conventionally the KV port, not
        // mgmt), so setting BootstrapHttpPort from the hint makes the
        // record-store's bucket-health checks hit the right port.
        int? mgmtPortHint = null;
        if ( context.ProviderHints != null
            && context.ProviderHints.TryGetValue( "mgmt-port", out var mgmtPortRaw )
            && int.TryParse( mgmtPortRaw, out var mgmtPort ) )
        {
            mgmtPortHint = mgmtPort;
        }

        // bucket-name hint plumbed in by CouchbaseSquashProvider so
        // a caller can override the sample default ("hyperbee") --
        // useful when the live cluster uses the Testcontainers library
        // auto-created bucket (a GUID name) instead of a fixed one.
        var bucketNameHint = context.ProviderHints != null
            && context.ProviderHints.TryGetValue( "bucket-name", out var bn )
            && !string.IsNullOrWhiteSpace( bn )
            ? bn
            : "hyperbee";

        services.AddCouchbase( opts =>
        {
            opts.ConnectionString = context.ConnectionString;
            opts.UserName = "Administrator";
            opts.Password = "password";
            if ( mgmtPortHint.HasValue )
                opts.BootstrapHttpPort = mgmtPortHint.Value;
        } );

        services.AddCouchbaseMigrations( opts =>
        {
            opts.BucketName = bucketNameHint;
            // LockName has no default in CouchbaseMigrationOptions
            // (the runner appsettings.json sets it explicitly via
            // Lock.Name). Hosts driving the runner programmatically
            // -- such as this one, consumed by the squash CLI's
            // apply-via-host pass -- must set it too, otherwise
            // CouchbaseRecordStore.CreateLockAsync feeds an empty
            // string to RequestMutexAsync and throws
            // ArgumentException("name").
            opts.LockName = "migration-mutex";
            opts.Assemblies = new[] { typeof( CouchbaseMigrationsHost ).Assembly };
            context.OverrideOptions?.Invoke( opts );
        } );

        return Task.FromResult<IServiceProvider>( services.BuildServiceProvider() );
    }
}
