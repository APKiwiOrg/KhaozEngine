using System;
using System.Collections.Generic;

namespace KhaozEngine.NetWorld;

/// <summary>A refused cell-blob rewrite. Existing <see cref="InvalidOperationException"/> catches remain valid.
/// The reason and identifiers describe the refusal. The message is developer prose. Disagreeing successful
/// inference candidates still use <see cref="AmbiguousCellBlobGenerationException"/>.</summary>
public sealed class CellBlobMigrationException : InvalidOperationException
{
    IReadOnlyList<ushort>? unregisteredExtensionIds;
    IReadOnlyList<string>? recoveryMembers;

    /// <summary>Creates a refusal with its required reason.</summary>
    public CellBlobMigrationException(CellBlobMigrationRefusal reason, string message) : base(message) => Reason = reason;

    /// <summary>The rule that refused the rewrite.</summary>
    public CellBlobMigrationRefusal Reason { get; }
    /// <summary>The stated source wire generation, for a known-generation walk refusal.</summary>
    public int? FromGeneration { get; init; }
    /// <summary>The requested target wire generation, for a known-generation walk refusal.</summary>
    public int? ToGeneration { get; init; }
    /// <summary>The schema label supplied to inference, for a no-candidate refusal.</summary>
    public string? SchemaLabel { get; init; }
    /// <summary>The inclusive oldest candidate wire generation, for a no-candidate refusal.</summary>
    public int? OldestGeneration { get; init; }
    /// <summary>The inclusive newest candidate wire generation, for a no-candidate refusal.</summary>
    public int? NewestGeneration { get; init; }
    /// <summary>The distinct extension ids the supplied registry rejected, in encounter order, as an owned
    /// read-only snapshot. Empty when inference observed none, including when no registry was supplied.</summary>
    public IReadOnlyList<ushort>? UnregisteredExtensionIds
    {
        get => unregisteredExtensionIds;
        init => unregisteredExtensionIds = value is null ? null : Array.AsReadOnly<ushort>([.. value]);
    }
    /// <summary>The recovery option names, AssumedWireGeneration then Registry, as an owned read-only snapshot
    /// for a no-candidate refusal.</summary>
    public IReadOnlyList<string>? RecoveryMembers
    {
        get => recoveryMembers;
        init => recoveryMembers = value is null ? null : Array.AsReadOnly<string>([.. value]);
    }
}
