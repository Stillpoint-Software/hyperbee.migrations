namespace Hyperbee.Migrations;

/// <summary>
/// The migration apply entry point that the squash CLI, the recovery verb,
/// and the runner project discover at startup. Each migration assembly
/// exposes <b>exactly one</b> public, non-abstract, default-constructible
/// type implementing this interface. The implementer wires the user's
/// existing <c>Add{Provider}Migrations</c> setup using
/// <see cref="MigrationHostContext.ConnectionString"/> and applies any
/// <see cref="MigrationHostContext.OverrideOptions"/> the caller provides.
/// </summary>
/// <remarks>
/// <para>
/// Per ADR-0024: the squash CLI references zero provider packages directly.
/// Provider discovery happens via the migration assembly's reference
/// closure -- whichever providers the user's project depends on (Postgres,
/// MongoDB, etc.) determine which <c>Add{Provider}Migrations</c>
/// extensions are reachable in the host implementation. The CLI calls
/// <see cref="ConfigureAsync(MigrationHostContext, CancellationToken)"/>
/// once per CLI invocation, resolves the typed runner from the returned
/// <see cref="IServiceProvider"/>, and drives the existing
/// <c>ISquashStrategy</c> + <c>MigrationRunner</c> machinery from there.
/// </para>
/// <para>
/// Same interface for all 5 first-party providers (Postgres, Aerospike,
/// OpenSearch, MongoDB, Couchbase) and any future third-party provider.
/// Multi-provider hosts implement the same interface and wire multiple
/// <c>Add{Provider}Migrations</c> calls; the CLI's <c>--provider</c> flag
/// selects which typed runner to invoke from the returned service
/// provider.
/// </para>
/// <para>
/// Sample implementation (single-provider, Postgres):
/// <code>
/// public class BillingMigrationsHost : IMigrationHost
/// {
///     public Task&lt;IServiceProvider&gt; ConfigureAsync(
///         MigrationHostContext ctx, CancellationToken ct )
///     {
///         var services = new ServiceCollection();
///         services.AddSingleton&lt;IConfiguration&gt;( new ConfigurationBuilder().Build() );
///         services.AddLogging();
///         services.AddNpgsqlDataSource( ctx.ConnectionString );
///         services.AddPostgresMigrations( opts =&gt;
///         {
///             opts.Assemblies = [typeof( BillingMigrationsHost ).Assembly];
///             opts.SchemaName = "billing";
///             ctx.OverrideOptions?.Invoke( opts );
///         } );
///         return Task.FromResult&lt;IServiceProvider&gt;( services.BuildServiceProvider() );
///     }
/// }
/// </code>
/// </para>
/// </remarks>
public interface IMigrationHost
{
    /// <summary>
    /// Build a configured <see cref="IServiceProvider"/> for the supplied
    /// <paramref name="context"/>. Implementations call
    /// <c>Add{Provider}Migrations</c> using
    /// <see cref="MigrationHostContext.ConnectionString"/>, apply any
    /// caller-supplied option overrides, and return the constructed
    /// provider.
    /// </summary>
    /// <param name="context">Caller-supplied connection + overrides + hints.</param>
    /// <param name="cancellationToken">Cancellation token honored during setup.</param>
    Task<IServiceProvider> ConfigureAsync(
        MigrationHostContext context,
        CancellationToken cancellationToken = default );
}

/// <summary>
/// Inputs that <see cref="IMigrationHost.ConfigureAsync(MigrationHostContext, CancellationToken)"/>
/// receives from the CLI / runner / recovery verb. Carries the connection
/// string for the target cluster (ephemeral container during squash
/// codegen, production cluster during deploy or recover), plus optional
/// callback hooks the host honors during DI wiring.
/// </summary>
/// <param name="ConnectionString">
/// The target cluster's connection string. The host MUST use this rather
/// than reading from <see cref="Microsoft.Extensions.Configuration.IConfiguration"/>
/// when wiring the provider's native client, otherwise the CLI cannot
/// redirect the host to an ephemeral fixture.
/// </param>
/// <param name="OverrideOptions">
/// Optional callback invoked after the host's own
/// <c>Add{Provider}Migrations</c> options-configure step, allowing the
/// CLI to inject filters (e.g., <c>UpToVersion</c>, <c>FromVersion</c>,
/// profile narrowing). Hosts that ignore this break the CLI's ability to
/// scope the migration apply -- always invoke
/// <c>OverrideOptions?.Invoke(opts)</c> at the end of the options lambda.
/// </param>
/// <param name="ProviderHints">
/// Optional caller-supplied key/value pairs the host may consult for
/// provider-specific overrides not expressible via
/// <see cref="OverrideOptions"/>. Keys are provider-agnostic free text;
/// well-known keys are documented per provider in the operator guide.
/// Reserved for forward-compatible extension; v3.0 ships with no
/// required keys.
/// </param>
public sealed record MigrationHostContext(
    string ConnectionString,
    Action<MigrationOptions> OverrideOptions = null,
    IReadOnlyDictionary<string, string> ProviderHints = null );
