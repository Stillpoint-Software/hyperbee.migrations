# ADR-0012: WithProductionDefaults() Extension Method (Not Environment Profile Enum)

**Status:** Accepted
**Date:** 2026-05-02

## Context

Several requirements coordinate dev-vs-prod safety defaults that must change together:

- `ClusterHealthThreshold` (R-03): Yellow / Green
- `WaitMode` (R-12): PerStatement / PerMigration
- `RequireUnsafeJustification` (R-18): false / true
- `ContextResolutionPolicy` (R-15): SkipIfUnset / RequireExplicit

In assessment 0002's Synthesis phase (Phase 2), the proposed solution was an `EnvironmentProfile = Development | Production` enum: one operator decision would flip all four behaviors. The synthesis explicitly flagged this as load-bearing — if the maintainer rejected the enum, the entire synthesis would collapse.

Independent Review (Phase 3.5) rejected the enum on three grounds:

1. **Hidden coupling** — flipping `Profile` silently flips four behaviors. The operator sees `Profile = Production` and must remember (or look up) what that implies. This is the laziest-path footgun the Mechanism Design analysis explicitly warns against.
2. **Contradicts a stated goal** — the user goal "same migrations run unchanged across all three topologies" applies to migration *files*, not DI configuration. An environment enum in DI re-introduces environment-aware switches that consumers reasoned about *not* having.
3. **Discoverability** — an enum value is set once at config time; an extension method shows in IntelliSense at the registration site, is grep-able in code review, and is callable as part of an audit trail.

Red-Blue₂ (Phase 3.75) resolved this contested point: Red (the IR's position) won; the synthesis was modified.

The forces in tension: operator ergonomics (one decision flips four defaults coherently) vs lazy-path safety (no hidden coupling); maintainer simplicity (one named noun consolidates the behaviors) vs IntelliSense-level discoverability.

## Decision

We will provide `services.AddOpenSearchMigrations(opts => { ... }).WithProductionDefaults();` as the single forcing function for production safety defaults.

The extension method explicitly sets:
- `ClusterHealthThreshold = Green`
- `WaitMode = PerMigration`
- `RequireUnsafeJustification = true`
- `ContextResolutionPolicy = RequireExplicit`

Per-option settings the operator chains AFTER `WithProductionDefaults()` win — the extension does not re-apply defaults if values were explicitly set later in the chain.

We will NOT provide an `EnvironmentProfile` enum. We will NOT auto-detect production environment from `DOTNET_ENVIRONMENT` / `ASPNETCORE_ENVIRONMENT` and apply defaults silently.

The startup banner (R-25) emits all resolved defaults at INFO so operators verify what's set in production logs.

## Consequences

**Easier:**
- Production deployments call one discoverable extension; the call site shows what changed without operators reading documentation
- Audit trails (git blame, code review) trivially identify which deployments use production defaults
- Resolved defaults visible in production logs (R-25 banner) so operators verify what's actually set
- Per-option overrides chain after the extension and win cleanly — no inheritance/override magic
- Extension method approach generalizes: future named bundles (`.WithCanaryDefaults()`, `.WithMigrationDryRunDefaults()`) follow the same pattern

**Harder:**
- Operators must explicitly call the extension; no implicit "set environment" gives prod safety
- Developers running locally with `DOTNET_ENVIRONMENT=Production` won't get prod defaults unless they call the extension explicitly — this is intentional but requires onboarding
- The runner project (R-26) must document the extension call in its sample `Program.cs`; new adopters who skip docs may ship dev defaults to prod
- A future regret about explicit-only opt-in cannot be reversed without superseding this ADR

**Constrains:**
- Future "named profile" requests (Staging, Canary) must justify avoiding the same hidden-coupling concern; if added, they should be additional extension methods, not enum values
- Per-option default changes must be reflected in the extension method's body; drift between "what's documented as production-safe" and "what the extension sets" must be tested
- The startup banner is required for completeness — without it, the extension's effects are invisible in deployed environments
