using System;
using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances.Journal;

/// <summary>
/// Encodes request identity without outcomes. Crafts use
/// <c>[None: varint = 0][Version: varint = 1][CraftPlanId: varint][Canonical operation]</c>.
/// Other kinds retain their canonical operation bytes. Stored event parameters remain unchanged.
/// </summary>
public static class ContainerOperationIntent
{
    const uint CraftVersion = 1;

    /// <summary>The bytes reserved for the operation's normalized intent.</summary>
    public static int ByteCount(in ContainerOperation operation)
        => operation.Kind == ContainerOperationKind.Craft
            ? CraftByteCount(operation, ReadPlanId(operation)) : operation.CanonicalByteCount;

    /// <summary>Writes one request's intent and returns its byte count.</summary>
    public static int Write(in ContainerOperation operation, Span<byte> destination)
    {
        if (operation.Kind != ContainerOperationKind.Craft) return operation.WriteCanonical(destination);
        return WriteCraft(operation, ReadPlanId(operation), destination);
    }

    /// <summary>Encodes an applied operation. A craft takes only its authored plan id from its audit body.</summary>
    public static byte[] ToArray(in ContainerOperation operation)
    {
        byte[] intent = new byte[ByteCount(operation)];
        Write(operation, intent);
        return intent;
    }

    /// <summary>
    /// Encodes a craft request before resolving its outcome. The operation needs canonical request fields,
    /// while its payload and audit body may be empty. The plan id is the authored crafting currency row.
    /// </summary>
    public static byte[] ForCraft(in ContainerOperation operation, int craftPlanId)
    {
        operation.ValidateCanonical();
        if (operation.Kind != ContainerOperationKind.Craft || operation.InstanceId == 0)
            throw new ArgumentException("A craft request targets an owned item.", nameof(operation));
        ArgumentOutOfRangeException.ThrowIfNegativeOrZero(craftPlanId);
        byte[] intent = new byte[CraftByteCount(operation, craftPlanId)];
        WriteCraft(operation, craftPlanId, intent);
        return intent;
    }

    internal static bool TryReadCraft(ReadOnlyMemory<byte> intent, out ContainerOperation operation,
        out int planId, out ReadOnlyMemory<byte> legacyCanonical)
    {
        operation = default;
        planId = 0;
        legacyCanonical = default;
        int offset = 0;
        if (!ContentVarint.TryRead(intent.Span, ref offset, out uint marker, out _) || marker != 0
            || !ContentVarint.TryRead(intent.Span, ref offset, out uint version, out _) || version != CraftVersion
            || !ContentVarint.TryRead(intent.Span, ref offset, out uint rawPlan, out _)
            || rawPlan is 0 or > int.MaxValue
            || !ContainerOperation.TryReadCanonical(intent.Span[offset..], out ContainerOperation read, out _)
            || read.Kind != ContainerOperationKind.Craft || read.InstanceId == 0) return false;

        operation = read;
        planId = (int)rawPlan;
        legacyCanonical = intent[offset..];
        return true;
    }

    static int ReadPlanId(in ContainerOperation operation)
    {
        operation.Validate();
        if (!ItemCraftedEvent.TryRead(operation.EventPayload, out ItemCraftedEvent audit, out _)
            || audit.InstanceId != operation.InstanceId)
            throw new ArgumentException("A craft intent needs its target's authored plan audit body.", nameof(operation));
        return audit.CurrencyId;
    }

    static int CraftByteCount(in ContainerOperation operation, int planId)
        => 2 + ContentVarint.Size((uint)planId) + operation.CanonicalByteCount;

    static int WriteCraft(in ContainerOperation operation, int planId, Span<byte> destination)
    {
        if (destination.Length < CraftByteCount(operation, planId))
            throw new ArgumentException("The destination cannot hold the craft intent.", nameof(destination));
        int written = ContentVarint.Write(destination, 0);
        written += ContentVarint.Write(destination[written..], CraftVersion);
        written += ContentVarint.Write(destination[written..], (uint)planId);
        return written + operation.WriteCanonicalParameters(destination[written..]);
    }
}
