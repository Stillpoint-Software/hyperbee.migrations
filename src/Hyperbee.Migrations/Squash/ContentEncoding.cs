namespace Hyperbee.Migrations.Squash;

/// <summary>
/// Byte encoding for emitted squash content. Per ADR-0019 the canonicalizer's
/// determinism gate (C12) requires byte-stable output; encoding choice is
/// part of that contract.
/// </summary>
public enum ContentEncoding : byte
{
    /// <summary>UTF-8, no byte-order mark. The default for SQL/CSharp/JSON.</summary>
    Utf8 = 0,

    /// <summary>UTF-8 with byte-order mark. Use only when the consumer requires it.</summary>
    Utf8Bom = 1,

    /// <summary>Raw bytes, encoding is provider-defined (e.g., a binary protocol payload).</summary>
    Raw = 2
}
