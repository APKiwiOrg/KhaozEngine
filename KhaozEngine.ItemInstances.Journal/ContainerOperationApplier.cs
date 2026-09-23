using System;
using System.Collections.Generic;
using KhaozEngine.ItemInstances;
using KhaozEngine.Items;

namespace KhaozEngine.ItemInstances.Journal;

/// <summary>Applies one recorded container operation to a caller-owned working copy. The live builder and
/// an event sourced reducer use the same preflight and mutation rules.</summary>
public static class ContainerOperationApplier
{
    /// <summary>A craft's stored before payload does not match the item at its declared slot.</summary>
    public const string BeforeMismatch = "before-mismatch";

    /// <summary>Tries one operation. Known invalid data returns false before changing any slot. A caller's
    /// working-copy implementation remains responsible for honoring valid writes.</summary>
    public static bool TryApply(
        IReadOnlyDictionary<string, IPagedContainerWorkingCopy> containers,
        in ContainerOperation operation,
        out string? reason)
    {
        ArgumentNullException.ThrowIfNull(containers);
        try
        {
            operation.Validate();
        }
        catch (ArgumentException)
        {
            reason = "operation-shape";
            return false;
        }

        if (!containers.TryGetValue(operation.Container, out IPagedContainerWorkingCopy? source)
            || !containers.TryGetValue(operation.DestinationContainerOrOwn,
                out IPagedContainerWorkingCopy? destination))
        {
            reason = "container-missing";
            return false;
        }
        if (!InRange(source, operation.Slot))
        {
            reason = "slot-range";
            return false;
        }

        return operation.Kind switch
        {
            ContainerOperationKind.Move => Move(source, destination, operation, out reason),
            ContainerOperationKind.Split => Split(source, operation, out reason),
            ContainerOperationKind.Merge => Merge(source, operation, out reason),
            ContainerOperationKind.Grant => Grant(source, operation, out reason),
            ContainerOperationKind.Take => Take(source, operation, out reason),
            ContainerOperationKind.Craft => Craft(source, destination, operation, out reason),
            ContainerOperationKind.Slide => Slide(source, operation, out reason),
            _ => Refuse("operation-kind", out reason),
        };
    }

    static bool Slide(IPagedContainerWorkingCopy container, in ContainerOperation operation,
        out string? reason)
    {
        int source = operation.Slot, destination = operation.DestinationSlot;
        if (source == destination) return Refuse("same-slot", out reason);
        long sourceEnd = (long)source + operation.Count;
        long destinationEnd = (long)destination + operation.Count;
        long addressSpace = (long)container.PageCount * ItemContainerPageCodec.ContainerPageSlots;
        if (sourceEnd > addressSpace || destinationEnd > addressSpace)
            return Refuse("slot-range", out reason);

        for (int offset = 0; offset < operation.Count; offset++)
        {
            if (container.SlotAt(source + offset).IsEmpty) return Refuse("empty-slot", out reason);
            int landing = destination + offset;
            if ((landing < source || landing >= sourceEnd) && !container.SlotAt(landing).IsEmpty)
                return Refuse("destination-occupied", out reason);
        }

        if (destination < source)
        {
            for (int offset = 0; offset < operation.Count; offset++)
            {
                ItemSlot moving = container.TakeSlotAt(source + offset);
                container.SetSlotAt(destination + offset, moving);
            }
        }
        else
        {
            for (int offset = operation.Count - 1; offset >= 0; offset--)
            {
                ItemSlot moving = container.TakeSlotAt(source + offset);
                container.SetSlotAt(destination + offset, moving);
            }
        }

        reason = null;
        return true;
    }

    static bool Move(IPagedContainerWorkingCopy source, IPagedContainerWorkingCopy destination,
        in ContainerOperation operation, out string? reason)
    {
        if (!TryOccupied(source, operation.Slot, operation.InstanceId, out ItemSlot moving, out reason))
            return false;
        if (!TrySlot(destination, operation.DestinationSlot, out ItemSlot target, out reason)) return false;
        if (!target.IsEmpty) return Refuse("destination-occupied", out reason);
        if (operation.Count > moving.Stack.Count) return Refuse("count-range", out reason);
        if (operation.Count < moving.Stack.Count && !Plain(moving))
            return Refuse("plain-required", out reason);

        if (operation.Count == moving.Stack.Count)
        {
            source.TakeSlotAt(operation.Slot);
            destination.SetSlotAt(operation.DestinationSlot, moving);
        }
        else
        {
            source.SetSlotAt(operation.Slot, Fewer(moving, operation.Count));
            destination.SetSlotAt(operation.DestinationSlot,
                new ItemSlot(new ItemStack(moving.Stack.ItemId, operation.Count), default, false));
        }
        reason = null;
        return true;
    }

    static bool Split(IPagedContainerWorkingCopy container, in ContainerOperation operation,
        out string? reason)
    {
        if (!TryOccupied(container, operation.Slot, operation.InstanceId, out ItemSlot stack, out reason))
            return false;
        if (!TrySlot(container, operation.DestinationSlot, out ItemSlot target, out reason)) return false;
        if (!Plain(stack)) return Refuse("plain-required", out reason);
        if (operation.Count >= stack.Stack.Count) return Refuse("count-range", out reason);
        if (!target.IsEmpty) return Refuse("destination-occupied", out reason);

        container.SetSlotAt(operation.Slot, Fewer(stack, operation.Count));
        container.SetSlotAt(operation.DestinationSlot,
            new ItemSlot(new ItemStack(stack.Stack.ItemId, operation.Count), default, false));
        reason = null;
        return true;
    }

    static bool Merge(IPagedContainerWorkingCopy container, in ContainerOperation operation,
        out string? reason)
    {
        if (operation.Slot == operation.DestinationSlot) return Refuse("same-slot", out reason);
        if (!TryOccupied(container, operation.Slot, operation.InstanceId, out ItemSlot source, out reason)
            || !TryOccupied(container, operation.DestinationSlot, operation.DestinationInstanceId,
                out ItemSlot destination, out reason)) return false;
        if (!InstanceStacking.CanMerge(destination, source, container.Stackable))
            return Refuse("merge-refused", out reason);

        ItemSlot merged = InstanceStacking.Merge(destination, source, out int remainder);
        container.SetSlotAt(operation.DestinationSlot, merged);
        if (remainder == 0) container.TakeSlotAt(operation.Slot);
        else container.SetSlotAt(operation.Slot, source with { Stack = source.Stack with { Count = remainder } });
        reason = null;
        return true;
    }

    static bool Grant(IPagedContainerWorkingCopy container, in ContainerOperation operation,
        out string? reason)
    {
        if (!TrySlot(container, operation.Slot, out ItemSlot seated, out reason)) return false;
        if (operation.Payload.Length > ItemInstancePayload.MaxInstancePayloadBytes
            || ItemInstancePayload.Validate(operation.Payload.Span) is not null
            || (operation.InstanceId == 0 && !operation.Payload.IsEmpty))
            return Refuse("payload-invalid", out reason);

        var arriving = new ItemSlot(
            new ItemStack(operation.DefinitionId, operation.Count, operation.InstanceId), operation.Payload, false);
        if (seated.IsEmpty)
        {
            if (container.IsAtCapacity) return Refuse("capacity", out reason);
            container.SetSlotAt(operation.Slot, arriving);
            reason = null;
            return true;
        }

        if (!InstanceStacking.CanMerge(seated, arriving, container.Stackable))
            return Refuse("merge-refused", out reason);
        ItemSlot merged = InstanceStacking.Merge(seated, arriving, out int remainder);
        if (remainder != 0) return Refuse("count-range", out reason);
        container.SetSlotAt(operation.Slot, merged);
        reason = null;
        return true;
    }

    static bool Take(IPagedContainerWorkingCopy container, in ContainerOperation operation,
        out string? reason)
    {
        if (!TryOccupied(container, operation.Slot, operation.InstanceId, out ItemSlot held, out reason))
            return false;
        if (operation.Count > held.Stack.Count) return Refuse("count-range", out reason);
        if (operation.Count < held.Stack.Count && !Plain(held))
            return Refuse("plain-required", out reason);

        if (operation.Count == held.Stack.Count) container.TakeSlotAt(operation.Slot);
        else container.SetSlotAt(operation.Slot, Fewer(held, operation.Count));
        reason = null;
        return true;
    }

    static bool Craft(IPagedContainerWorkingCopy container, IPagedContainerWorkingCopy currency,
        in ContainerOperation operation, out string? reason)
    {
        if (operation.InstanceId == 0) return Refuse("instance-missing", out reason);
        if (!TryOccupied(container, operation.Slot, operation.InstanceId, out ItemSlot target, out reason))
            return false;
        if (target.Quarantined) return Refuse("quarantined", out reason);
        if (!ItemCraftedEvent.TryRead(operation.EventPayload, out ItemCraftedEvent audit, out _)
            || audit.InstanceId != operation.InstanceId
            || !audit.After.Span.SequenceEqual(operation.Payload.Span)
            || ItemInstancePayload.Validate(operation.Payload.Span) is not null)
            return Refuse("payload-invalid", out reason);
        if (!audit.Before.Span.SequenceEqual(target.Payload.Span))
            return Refuse(BeforeMismatch, out reason);

        ItemSlot paid = default;
        if (operation.DefinitionId != 0)
        {
            if (ReferenceEquals(container, currency) && operation.Slot == operation.DestinationSlot)
                return Refuse("currency-target", out reason);
            if (!TrySlot(currency, operation.DestinationSlot, out paid, out reason)) return false;
            if (paid.IsEmpty || paid.Stack.ItemId != operation.DefinitionId
                || operation.Count == 0 || operation.Count > paid.Stack.Count)
                return Refuse("currency-refused", out reason);
        }

        container.SetSlotAt(operation.Slot, target with { Payload = operation.Payload });
        if (operation.DefinitionId != 0)
        {
            if (operation.Count == paid.Stack.Count) currency.TakeSlotAt(operation.DestinationSlot);
            else currency.SetSlotAt(operation.DestinationSlot, Fewer(paid, operation.Count));
        }
        reason = null;
        return true;
    }

    static bool TrySlot(IPagedContainerWorkingCopy container, int slot,
        out ItemSlot value, out string? reason)
    {
        value = default;
        if (!InRange(container, slot)) return Refuse("slot-range", out reason);
        value = container.SlotAt(slot);
        reason = null;
        return true;
    }

    static bool TryOccupied(IPagedContainerWorkingCopy container, int slot, long instanceId,
        out ItemSlot value, out string? reason)
    {
        if (!TrySlot(container, slot, out value, out reason)) return false;
        if (value.IsEmpty) return Refuse("empty-slot", out reason);
        if (value.Stack.InstanceId != instanceId) return Refuse("instance-mismatch", out reason);
        return true;
    }

    static bool InRange(IPagedContainerWorkingCopy container, int slot)
        => slot >= 0 && (long)slot < (long)container.PageCount * ItemContainerPageCodec.ContainerPageSlots;

    static bool Plain(in ItemSlot slot)
        => slot.Stack.InstanceId == 0 && slot.Payload.IsEmpty && !slot.Quarantined;

    static ItemSlot Fewer(in ItemSlot slot, int count)
        => slot with { Stack = slot.Stack with { Count = slot.Stack.Count - count } };

    static bool Refuse(string token, out string? reason)
    {
        reason = token;
        return false;
    }
}
