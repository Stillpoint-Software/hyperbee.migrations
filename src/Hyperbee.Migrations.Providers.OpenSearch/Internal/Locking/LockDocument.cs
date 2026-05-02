#nullable enable
using OpenSearch.Client;

namespace Hyperbee.Migrations.Providers.OpenSearch.Internal.Locking;

// POCO matching the lock index's strict mapping (per LockIndexInitStep).
// Field names MUST match the keyword/date mappings exactly — strict mapping
// rejects any unrecognized field.
//
// Fields:
//   name            descriptive lock name (matches Options.LockName)
//   owner           runner identity for forensics: {machine}/{pid}/{runnerId?}
//   acquiredAt      timestamp of original acquisition (preserved across renewals)
//   lastHeartbeat   most recent heartbeat timestamp; updated each renewal

public sealed class LockDocument
{
    [PropertyName( "name" )] public string Name { get; set; } = string.Empty;
    [PropertyName( "owner" )] public string Owner { get; set; } = string.Empty;
    [PropertyName( "acquiredAt" )] public DateTimeOffset AcquiredAt { get; set; }
    [PropertyName( "lastHeartbeat" )] public DateTimeOffset LastHeartbeat { get; set; }
}
