# ADR-0030: ConnectionSettings Escape Hatch on the OpenSearch Client Factories

**Status:** Accepted
**Date:** 2026-08-26
**Related ADRs:** ADR-0006 (Options Inheritance with DI Registration), ADR-0023 (Multi-Runner Composition), ADR-0029 (Ledger Wire Contract is Library-Owned)

## Context

OpenSearch is the only provider whose client the library constructs. `AddOpenSearchClient` (core) and `AddOpenSearchAwsClient` (`.Aws`) build a `ConnectionSettings`, wire authentication into it, and register the resulting `IOpenSearchClient`. Every other provider resolves a client the consumer registered — `IAsyncClient`, `IClusterProvider`, `IMongoClient`, `NpgsqlDataSource` — so those consumers already have unrestricted access to client configuration.

The OpenSearch factories are closed constructors. `ConnectionSettings` carries a large surface the typed auth options do not model, all of it legitimately consumer-owned:

- `RequestTimeout`, `MaximumRetries`, `MaxRetryTimeout` — deployment-dependent tuning.
- `EnableHttpCompression`, `Proxy` — network topology.
- `ServerCertificateValidationCallback` — self-signed certificates on development clusters.
- `DisableDirectStreaming`, `EnableDebugMode`, `OnRequestCompleted` — diagnostics.
- Connection-pool and sniffing choices for multi-node clusters.
- `DefaultMappingFor<TDocument>` covering the *consumer's own* types when the same client is shared with application code.

With no hook, a consumer needing any of these has exactly one option: stop calling the factory and hand-roll the registration. That means re-implementing the auth-mode switch, the certificate loader, the AWS-endpoint loud-fail (`ThrowIfAwsEndpoint`), the mutual-exclusion guard, and — for the SigV4 path — the `AwsSigV4HttpConnection` construction and credential-rotation behavior. That is a lot of library logic to fork for one line of transport tuning, and the fork then silently misses every subsequent fix to any of it. This is not hypothetical; it is what a consumer did.

A separate question is whether the hook should be the remedy for the ADR-0029 `_mget` defect. It should not, and that is settled there: making correct operation depend on the consumer declaring a mapping for a library-internal type inverts ownership. The hook is worth having on its own merits, for consumer-owned concerns.

## Decision

Both factories take an optional `Action<ConnectionSettings>? configureSettings`, applied **last**.

```csharp
public static IServiceCollection AddOpenSearchClient(
    this IServiceCollection services,
    Uri endpoint,
    Action<OpenSearchAuthenticationOptions>? configure = null,
    Action<ConnectionSettings>? configureSettings = null );

public static IServiceCollection AddOpenSearchAwsClient(
    this IServiceCollection services,
    Uri endpoint,
    Action<OpenSearchAwsAuthenticationOptions> configure,
    Action<ConnectionSettings>? configureSettings = null );
```

Both `IConfiguration` overloads forward the same parameter. All parameters are optional and trailing, so every existing call site compiles unchanged.

**Last-wins ordering.** The callback runs after the endpoint and authentication wiring, so a consumer can override anything the library set. An escape hatch that the library can silently overwrite is not an escape hatch. `Action<ConnectionSettings>` rather than `Func<ConnectionSettings, ConnectionSettings>` because `ConnectionSettings` mutates in place and returns itself, and `Action<TOptions>` matches the configure-callback idiom already used throughout these extensions.

**Shared construction.** Both packages route through one internal `BuildClient( settings, configureSettings )` in the core package, so the hook and its validation behave identically on both registration paths. The `.Aws` package gets `InternalsVisibleTo` for this.

**Validated, not merely documented.** The ledger index is created with a `strict` mapping whose fields are camelCase, matching the client's default field-name inference. A hook that replaces the serializer or sets a non-camelCase `DefaultFieldNameInferrer` breaks every ledger write. That failure is loud on its own — `strict_dynamic_mapping_exception` — but it arrives at first write, names fields rather than the cause, and reads like a schema problem.

Registration therefore probes one known ledger property through the configured inferrer and fails with a pointed message naming the cause and the remediation (scope the change with `DefaultMappingFor<TDocument>`, or register a separate client for application use). One probe suffices: field-name inference is a single client-wide setting.

The validation runs only when a hook was supplied, so the default path costs nothing.

## Consequences

**Positive:**
- Consumers stop forking client registration to reach transport settings, and stop missing library fixes as a result.
- The foot-gun the hook introduces is caught where it is introduced, with remediation, instead of at first ledger write.
- The two factories stay behaviorally identical, including the failure mode.

**Negative:**
- A public surface over a third-party type. `ConnectionSettings` is `OpenSearch.Client`'s, so its shape is not ours to version; a breaking change there becomes a breaking change here. Accepted — the alternative (mirroring a subset as typed options) is strictly worse, see below.
- The hook can still break things the probe does not cover — replacing `IConnection` on the SigV4 path removes request signing, for instance. Documented on the parameter rather than validated; enumerating every way to misconfigure a transport is not tractable.

**Neutral:**
- No symmetric change for the other four providers. They do not construct clients, so there is nothing to open up. OpenSearch is the outlier here, and the fix is to make its factory overridable rather than to add factories elsewhere.
- `DefaultIndex` and `DefaultMappingFor` become reachable through a supported path. Per ADR-0029 the ledger ignores both, and a test pins that.

## Alternatives Considered

- **Mirror the useful settings as typed options on `OpenSearchAuthenticationOptions`** — rejected. It is an unbounded surface to chase, it lags upstream, and it still fails the consumer who needs the one property nobody mirrored. The library gains nothing by standing between the consumer and a well-documented settings object.
- **Document "register your own `IOpenSearchClient` instead"** — rejected as the primary answer. It is already supported and remains the right call for consumers who share a client with application code, but it forfeits the AWS-endpoint guard, the auth-mode handling, and the mutual-exclusion check for what is often a one-line need.
- **`Func<ConnectionSettings, ConnectionSettings>`** — rejected. More general in principle, but `ConnectionSettings` mutates in place, so the return value is always the same instance, and requiring a return adds a way to get it wrong.
- **Apply the hook before auth wiring** — rejected. The library would overwrite consumer intent for any overlapping setting, which is the opposite of an escape hatch.
- **Fail-fast on the whole class of ledger-affecting settings** (serializer, inferrer, `DefaultIndex`, connection) — rejected as over-reach. Only field-name inference actually breaks the ledger; the others are legitimate and ADR-0029 already makes the ledger immune to index inference.

## References

- [`ServiceCollectionExtensions.cs`](../../src/Hyperbee.Migrations.Providers.OpenSearch/ServiceCollectionExtensions.cs) — `AddOpenSearchClient`, `BuildClient`
- [`ServiceCollectionExtensions.cs`](../../src/Hyperbee.Migrations.Providers.OpenSearch.Aws/ServiceCollectionExtensions.cs) — `AddOpenSearchAwsClient`
- `OpenSearchConnectionSettingsHookTests`
