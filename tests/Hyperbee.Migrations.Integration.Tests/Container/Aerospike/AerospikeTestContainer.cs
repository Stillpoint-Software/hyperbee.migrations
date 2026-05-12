using Aerospike.Client;
using DotNet.Testcontainers.Builders;
using DotNet.Testcontainers.Containers;
using Hyperbee.Migrations.Wait;

namespace Hyperbee.Migrations.Integration.Tests.Container.Aerospike;

public class AerospikeTestContainer
{
    public static IAsyncClient AsyncClient { get; set; }
    public static IAerospikeClient Client { get; set; }
    public static INetwork Network { get; set; }
    public static string Host { get; set; }
    public static int Port { get; set; }

    public static async Task Initialize( TestContext context )
    {
        var cancellationToken = context.CancellationTokenSource.Token;

        var network = new NetworkBuilder()
            .WithName( Guid.NewGuid().ToString( "D" ) )
            .WithCleanUp( true )
            .Build();

        await network.CreateAsync( cancellationToken )
            .ConfigureAwait( false );

        // Aerospike opens its service port before the cluster is actually
        // ready to accept client connections. The container wait strategy
        // is just TCP-open; the WaitHelper retry below covers the brief
        // window where the port is open but the cluster isn't yet
        // accepting AsyncClient handshakes.
        //
        // DEFAULT_TTL=86400: the Aerospike CE image's entrypoint template
        // ONLY emits `nsup-period` when DEFAULT_TTL != 0 (see the official
        // image's aerospike.template.conf). With DEFAULT_TTL=0 (the
        // default), the namespace has no nsup-period directive, NSUP is
        // disabled, and any write with expiration > 0 returns FORBIDDEN_OP
        // ("Operation not allowed at this time"). The runner's lock record
        // uses expiration=60, so every CreateLockAsync fails without NSUP.
        // 86400s (24h) is a benign default for records that don't set
        // their own TTL — the lock record's explicit expiration=60
        // overrides this for the single record that actually cares.
        var aerospikeContainer = new ContainerBuilder()
            .WithImage( "aerospike/aerospike-server:latest" )
            .WithNetwork( network )
            .WithNetworkAliases( "db" )
            .WithPortBinding( 3100, 3000 )
            .WithEnvironment( "DEFAULT_TTL", "86400" )
            .WithCleanUp( true )
            .WithWaitStrategy( DotNet.Testcontainers.Builders.Wait.ForUnixContainer().UntilExternalTcpPortIsAvailable( 3000 ) )
            .Build();

        await aerospikeContainer.StartAsync( cancellationToken )
            .ConfigureAwait( false );

        Host = aerospikeContainer.Hostname;
        Port = aerospikeContainer.GetMappedPublicPort( 3000 );

        // Aerospike opens its service port before the cluster will accept
        // client connections, AND a fresh client connection can succeed
        // before the cluster will accept *writes* (Error 22 "Operation not
        // allowed at this time" while the partition map converges). Two
        // gates: (1) construct the AsyncClient, (2) confirm the cluster
        // accepts a sentinel write. Both reuse WaitHelper for parity with
        // the rest of the codebase.
        AsyncClient asyncClient = null;

        await WaitHelper.WaitUntilAsync(
            _ =>
            {
                try
                {
                    asyncClient = new AsyncClient( Host, Port );
                    return Task.FromResult( true );
                }
                catch ( AerospikeException )
                {
                    asyncClient?.Close();
                    asyncClient = null;
                    return Task.FromResult( false );
                }
            },
            timeout: TimeSpan.FromSeconds( 30 ),
            cancellationToken: cancellationToken )
            .ConfigureAwait( false );

        // Sentinel-write probe. The runner's first write is CreateLockAsync;
        // if the partition map isn't yet allocated for the target set, that
        // write fails with Error 22 ("Operation not allowed at this time").
        // Aerospike maps (namespace, set, key) → partition deterministically,
        // so a probe against one set doesn't guarantee writes to a DIFFERENT
        // set will succeed. The runner writes to the "SchemaMigrations" set
        // (per appsettings.json), so probe THAT set specifically.
        //
        // The probe also includes a generic-namespace probe up front because
        // the namespace itself must be loaded before any set within it
        // accepts writes; both gates are needed.
        // The runner's lock write sets expiration=60 (TTL on the lock record).
        // Mirror that here — Aerospike will reject a TTL'd write with
        // FORBIDDEN_OP if the namespace's nsup-period is 0 / TTL is disabled,
        // which is a different condition than "namespace not ready." Catching
        // this gate at fixture-probe time means we fail loud at startup
        // instead of silently when the runner first tries to lock.
        var writePolicy = new WritePolicy
        {
            socketTimeout = 1000,
            totalTimeout = 5000,
            expiration = 60
        };

        async Task ProbeSetAsync( string setName, string keyName )
        {
            var probeKey = new Key( "test", setName, keyName );

            await WaitHelper.WaitUntilAsync(
                async _ =>
                {
                    try
                    {
                        await asyncClient.Put( writePolicy, cancellationToken, probeKey, new Bin( "ready", 1 ) ).ConfigureAwait( false );
                        return true;
                    }
                    catch ( AerospikeException )
                    {
                        return false;
                    }
                },
                timeout: TimeSpan.FromSeconds( 30 ),
                cancellationToken: cancellationToken )
                .ConfigureAwait( false );

            try { await asyncClient.Delete( writePolicy, cancellationToken, probeKey ).ConfigureAwait( false ); }
            catch ( AerospikeException ) { /* probe cleanup is best-effort */ }
        }

        await ProbeSetAsync( "probe", "aerospike-test-container-ready" ).ConfigureAwait( false );
        // Runner uses the "SchemaMigrations" set per
        // runners/Hyperbee.MigrationRunner.Aerospike/appsettings.json.
        await ProbeSetAsync( "SchemaMigrations", "container-ready-probe" ).ConfigureAwait( false );

        AsyncClient = asyncClient;
        Client = asyncClient;
        Network = network;
    }
}
