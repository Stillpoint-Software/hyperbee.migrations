# AWS Managed OpenSearch — Scheduled Validation Runbook

**Status:** Draft v1
**Owner:** Hyperbee Migrations maintainers
**Cadence:** pre-release; nightly when AWS credentials are available in CI
**Per:** R-28c (scheduled validation), R-21 (auth), R-24c (production scenarios)

## Purpose

Single-node Testcontainers (every PR) and 3-node multi-node Testcontainers (every PR via [`multi_node_tests.yml`](../../.github/workflows/multi_node_tests.yml)) cover the in-cluster correctness behaviors. Neither exercises the AWS-specific surface:

- **SigV4 request signing** (transport-replacing auth, separate `.Aws` extension package)
- **AWS endpoint loud-fail** behavior at startup against a real domain hostname
- **ISM endpoint capability detection** against AWS Managed domains, which historically expose the legacy `/_opendistro/_ism` surface on older versions
- **IRSA / instance-profile credential rotation** — credentials resolve per request via `AWSCredentials.GetCredentials()`; only a real AWS environment exercises that lifecycle

This runbook is the manual-or-scheduled equivalent of `multi_node_tests.yml` for AWS-specific behaviors. Run it before each release, and as often as account access permits in between.

## Prerequisites

- An AWS Managed OpenSearch domain in a region you have permissions in. Free-tier `t3.small` is sufficient for smoke testing; a `t3.medium` two-AZ domain better mirrors production replica behavior.
- IAM identity (user, role, or assumed role via STS) with at least `es:ESHttp*` against `<domain-arn>/*`. For the ISM scenario, `es:ESHttp*` against `<domain-arn>/_plugins/_ism/*` is also required (or `_opendistro_*` on older domains).
- AWS credentials resolvable via the standard chain — env vars (`AWS_ACCESS_KEY_ID` / `AWS_SECRET_ACCESS_KEY` / optional `AWS_SESSION_TOKEN`), instance profile, IRSA, or `aws configure` profile.
- The runner project's published binary (or `dotnet run`-able source). The `Hyperbee.Migrations.Providers.OpenSearch.Aws` package must be referenced.

## Runner configuration

```jsonc
// runners/Hyperbee.MigrationRunner.OpenSearch/appsettings.aws-validation.json
{
  "OpenSearch": {
    "ConnectionString": "https://<domain-id>.<region>.es.amazonaws.com",
    "Authentication": {
      "Mode": "AwsSigV4",
      "Region": "us-east-1",
      "Service": "es"
    }
  },
  "Migrations": {
    "LedgerIndex": ".migrations-aws-validation",
    "LockIndex": ".migrations-aws-validation-lock",
    "LockName": "validation-lock",
    "Lock": { "Enabled": true },
    "FromPaths": [
      "..\\..\\..\\..\\..\\runners\\samples\\Hyperbee.Migrations.OpenSearch.Samples\\bin\\Debug\\net10.0\\Hyperbee.Migrations.OpenSearch.Samples.dll"
    ]
  }
}
```

For OpenSearch Serverless, use `Service: "aoss"` and a `<id>.<region>.aoss.amazonaws.com` endpoint.

## Validation steps

### 1 — Loud-fail check (negative test)

Confirms the core `AddOpenSearchClient` path correctly rejects an AWS endpoint when the `.Aws` extension wasn't wired (R-21 #2).

```bash
# Run with AwsSigV4 mode but DON'T reference the .Aws extension — this won't
# happen against a runner that depends on the extension package, but the
# core's URL guard is the safety net for misconfigured deployments. To
# exercise it, point a non-AWS-aware host at an AWS URL:
DOTNET_ENVIRONMENT=aws-validation \
  ./Hyperbee.MigrationRunner.OpenSearch \
  --connection https://<domain-id>.<region>.es.amazonaws.com \
  --auth-mode Anonymous
```

**Expected:** `AwsSigV4NotConfiguredException` at startup with the `services.AddOpenSearchAwsClient(...)` snippet in the message. Process exits non-zero before any wire request.

**Pass criterion:** the exception message includes both `amazonaws.com` and `AddOpenSearchAwsClient`.

### 2 — Smoke test (positive path, all v1 verbs)

Run the samples against the AWS domain. Each sample exercises a different verb family.

```bash
DOTNET_ENVIRONMENT=aws-validation \
  ./Hyperbee.MigrationRunner.OpenSearch
```

**Expected:** all 8 samples (1000–8000) complete successfully. The runner's exit code is 0.

**Verify on the cluster:**

```bash
# All sample indices created
aws es-http GET --domain <domain> /_cat/indices/sample_*?format=json

# Ledger entries written, with forensic fields populated (R-06)
aws es-http GET --domain <domain> /.migrations-aws-validation/_search?pretty
```

Each ledger entry should show:
- `direction: "Up"`
- `status: "succeeded"`
- `appliedBy: "<machine>/<pid>"`

If `appliedBy` shows a stable hostname (e.g., the EC2 instance id or k8s pod name), credential resolution is working through IRSA/instance profile (R-21 #4).

### 3 — ISM endpoint detection

Confirms the bootstrap step correctly resolves to the modern or legacy ISM surface depending on the AWS domain's version.

```bash
# Examine the bootstrapper's log output. The IsmEndpointDetectStep
# emits an INFO log on success:
#   "ism-detect resolved to `_plugins/_ism` (modern OpenSearch ISM surface)"
# OR
#   "ism-detect resolved to `_opendistro/_ism` (legacy opendistro ISM surface — common on older AWS Managed domains)"
grep "ism-detect" runner.log
```

**Expected:** exactly one `ism-detect resolved` line per bootstrap. The resolved prefix matches what `aws es-http HEAD --domain <domain> /_plugins/_ism/policies` returns (200 → modern; 404 → check legacy).

**If neither prefix works**, the runbook surfaces the IAM-permission failure: the bootstrap step fails with `OpenSearchProviderException` naming `es:ESHttp*` against the ISM resource ARN. Add the IAM action to the deploy role and rerun.

### 4 — Credential rotation (long-running)

Optional. If the validation runs for ≥1 hour against an IRSA-authenticated workload, the IAM session token should rotate at least once during the run without runner restart.

```bash
# Start a long-running migration scenario (e.g., bulk-load 100K docs)
# while watching for credential refresh in the AWS SDK debug log.
DOTNET_ENVIRONMENT=aws-validation AWS_SDK_DEBUG=true \
  ./Hyperbee.MigrationRunner.OpenSearch &
sleep 3700  # > 1 hour
```

**Expected:** the migration completes successfully. AWS SDK debug log shows multiple credential resolution events (one per request, with the same identity but potentially different session tokens after rotation).

**Pass criterion:** no 403 / signature-mismatch errors during the run. R-21 #4 spec: "credential resolver lifetime — SigV4 signer is wired to an identity resolver that re-resolves credentials per request, not cached at client construction."

## Reporting

Add a single line to the release checklist after each run:

```
2026-05-XX  AWS Managed OpenSearch validation: PASS  (us-east-1 / domain-X / runbook v1)
```

If validation can't be performed for a release (no account access in CI; account locked; etc.), add the deferral notice instead:

```
2026-05-XX  AWS Managed OpenSearch validation: DEFERRED  (reason: <reason>)
```

The release process MUST include either a PASS or a DEFERRED line — never just silently skip the validation.

## When validation fails

Failure during step 1 (loud-fail) → core's `AddOpenSearchClient` URL guard regressed. Check the AWS-pattern matcher in `ServiceCollectionExtensions.ThrowIfAwsEndpoint`.

Failure during step 2 (smoke) → look at the FIRST failing sample and which verb it tests. Compare to single-node Testcontainers behavior; AWS-specific failures usually involve auth, region mismatch, or IAM permissions on a specific endpoint (e.g., `_index_template` on older domains).

Failure during step 3 (ISM detection) → the `IsmEndpointDetectStep`'s probe path is failing for non-404 reasons. Common causes: the IAM role lacks `es:ESHttp*` against `_plugins/_ism/*` (or `_opendistro_*` for older domains). The exception message names the IAM action required.

Failure during step 4 (rotation) → uncommon. Check the AWS SDK version pinned by the OpenSearch.Net.Auth.AwsSigV4 package; older AWSSDK.Core versions had IRSA refresh bugs. Workaround: explicit `Credentials = new InstanceProfileAWSCredentials()` with a refresh interval rather than the default chain.

## Out of scope

- **Full CI automation of this runbook** — deferred to v1.1 per the requirements doc Open Questions section. Requires AWS account scaffolding in CI plus credential management; not blocking v1.
- **OpenSearch Serverless validation against a `_plugins/_ism` endpoint** — Serverless doesn't support ISM. The runbook's step 3 is skipped for `aoss` deployments.
- **Cross-region failover testing** — out of scope for migration tooling; that's a deployment-architecture concern.
