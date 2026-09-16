using System;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;
using KhaozEngine.Items;

namespace KhaozEngine.ItemInstances.Journal;

/// <summary>
/// The working copy half: what each operation of the vocabulary DOES to a paged container, and the arithmetic
/// the window needs before it decides.
/// <para>
/// <b>The page rule is spec 5.6's and nothing wider:</b> the pages holding the slots the operation changed,
/// and no others. Every kind names its slots, so those pages are known BEFORE the operation is applied, which
/// is what lets the window close on the projection write cap without a mutation to undo. A move across two
/// pages of one container is one commit with two projection writes on the same stream, and atomicity is the
/// database transaction's.
/// </para>
/// <para>
/// <b>Everything here refuses by throwing.</b> An action a game refuses never reaches the journal (spec
/// 10.6), so an operation the working copy cannot perform is a caller bug rather than a player outcome.
/// </para>
/// </summary>
public sealed partial class ContainerCommitBuilder
{
    /// <summary>The slack a page write is allowed to grow by beyond the entry it seats: the header fields, a
    /// wider entry count varint and a slot varint. It exists to keep the window's byte check an UPPER bound,
    /// which is the only direction a cap can be approximated in.</summary>
    const int PageGrowthSlack = 64;

    byte[] ApplyToWorkingCopy(in ContainerOperation operation)
    {
        switch (operation.Kind)
        {
            case ContainerOperationKind.Move:
                ApplyMove(operation);
                break;
            case ContainerOperationKind.Split:
                ApplySplit(operation);
                break;
            case ContainerOperationKind.Merge:
                ApplyMerge(operation);
                break;
            case ContainerOperationKind.Grant:
                ApplyGrant(operation);
                break;
            case ContainerOperationKind.Take:
                ApplyTake(operation);
                break;
            case ContainerOperationKind.Craft:
                ApplyCraft(operation);
                break;
            default:
                throw new ArgumentException(
                    FormattableString.Invariant($"{operation.Kind} is not an operation."), nameof(operation));
        }

        if (operation.PresentAtCommit) _presentAtCommit = true;

        // A craft's event body is spec 10.6's and the crafting framework encodes it. Every other kind writes
        // its own canonical encoding, so the event and the intent agree by construction.
        return operation.Kind == ContainerOperationKind.Craft
            ? operation.EventPayload.ToArray()
            : operation.ToCanonicalArray();
    }

    void ApplyMove(in ContainerOperation operation)
    {
        PagedItemContainer source = _containers[operation.Container];
        PagedItemContainer destination = _containers[operation.DestinationContainerOrOwn];
        ItemSlot moving = Occupied(source, operation.Slot, operation.InstanceId);
        Require(operation.Count <= moving.Stack.Count, "A move cannot carry more units than the slot holds.");
        Require(
            destination.SlotAt(operation.DestinationSlot).IsEmpty,
            "A move lands in an empty slot. Folding one entry into another is a merge.");

        if (operation.Count == moving.Stack.Count)
        {
            source.TakeSlotAt(operation.Slot);
            destination.SetSlotAt(operation.DestinationSlot, moving);
            return;
        }

        RequirePlain(moving);
        source.SetSlotAt(operation.Slot, Fewer(moving, operation.Count));
        destination.SetSlotAt(
            operation.DestinationSlot,
            new ItemSlot(new ItemStack(moving.Stack.ItemId, operation.Count), default, false));
    }

    void ApplySplit(in ContainerOperation operation)
    {
        PagedItemContainer container = _containers[operation.Container];
        ItemSlot stack = Occupied(container, operation.Slot, operation.InstanceId);
        RequirePlain(stack);
        Require(operation.Count < stack.Stack.Count, "A split leaves units behind. Moving the lot is a move.");
        Require(container.SlotAt(operation.DestinationSlot).IsEmpty, "A split lands in an empty slot.");

        container.SetSlotAt(operation.Slot, Fewer(stack, operation.Count));
        container.SetSlotAt(
            operation.DestinationSlot,
            new ItemSlot(new ItemStack(stack.Stack.ItemId, operation.Count), default, false));
    }

    void ApplyMerge(in ContainerOperation operation)
    {
        PagedItemContainer container = _containers[operation.Container];
        Require(operation.Slot != operation.DestinationSlot, "A slot does not merge into itself.");
        ItemSlot source = Occupied(container, operation.Slot, operation.InstanceId);
        ItemSlot destination = Occupied(container, operation.DestinationSlot, operation.DestinationInstanceId);
        Require(
            InstanceStacking.CanMerge(destination, source, container.Stackable),
            "Spec 4.6 refuses this merge: the definitions differ, the predicate says no, one side is quarantined, or the payloads are not byte identical.");

        ItemSlot merged = InstanceStacking.Merge(destination, source, out int remainder);
        container.SetSlotAt(operation.DestinationSlot, merged);
        if (remainder == 0) container.TakeSlotAt(operation.Slot);
        else container.SetSlotAt(operation.Slot, source with { Stack = source.Stack with { Count = remainder } });
    }

    void ApplyGrant(in ContainerOperation operation)
    {
        PagedItemContainer container = _containers[operation.Container];
        var arriving = new ItemSlot(
            new ItemStack(operation.DefinitionId, operation.Count, operation.InstanceId), operation.Payload, false);
        ItemSlot seated = container.SlotAt(operation.Slot);
        if (seated.IsEmpty)
        {
            // Spec 5.7 rule 1: a grant that opens a NEW slot is refused at or above capacity. Rule 2, the
            // merge below, is allowed at any occupancy, which is why the gate is asked here and not there.
            Require(!container.IsAtCapacity, "A grant opening a new slot is refused at capacity.");
            container.SetSlotAt(operation.Slot, arriving);
            return;
        }

        Require(
            InstanceStacking.CanMerge(seated, arriving, container.Stackable),
            "A grant into an occupied slot merges into it, and spec 4.6 refuses this merge.");
        ItemSlot merged = InstanceStacking.Merge(seated, arriving, out int remainder);
        Require(remainder == 0, "A grant the slot cannot hold in full is the caller's to split before it commits.");
        container.SetSlotAt(operation.Slot, merged);
    }

    void ApplyTake(in ContainerOperation operation)
    {
        PagedItemContainer container = _containers[operation.Container];
        ItemSlot held = Occupied(container, operation.Slot, operation.InstanceId);
        Require(operation.Count <= held.Stack.Count, "A take cannot remove more units than the slot holds.");

        if (operation.Count == held.Stack.Count)
        {
            container.TakeSlotAt(operation.Slot);
            return;
        }

        RequirePlain(held);
        container.SetSlotAt(operation.Slot, Fewer(held, operation.Count));
    }

    void ApplyCraft(in ContainerOperation operation)
    {
        PagedItemContainer container = _containers[operation.Container];
        Require(operation.InstanceId != 0, "A craft targets an owned item, which always has an instance id.");
        ItemSlot target = Occupied(container, operation.Slot, operation.InstanceId);
        Require(!target.Quarantined, "A quarantined item is out of play and is not craftable.");

        container.SetSlotAt(operation.Slot, target with { Payload = operation.Payload });
        if (operation.DefinitionId == 0) return;

        PagedItemContainer currency = _containers[operation.DestinationContainerOrOwn];
        ItemSlot paid = currency.SlotAt(operation.DestinationSlot);
        Require(!paid.IsEmpty, "The craft's currency slot is empty.");
        Require(paid.Stack.ItemId == operation.DefinitionId, "The craft's currency slot holds a different definition.");
        Require(operation.Count > 0 && operation.Count <= paid.Stack.Count, "The craft's currency slot holds too few units.");

        if (operation.Count == paid.Stack.Count) currency.TakeSlotAt(operation.DestinationSlot);
        else currency.SetSlotAt(operation.DestinationSlot, Fewer(paid, operation.Count));
    }

    /// <summary>The pages this operation would dirty that are not dirty already, which is what the projection
    /// write cap is counted in.</summary>
    int CountUndirtiedPages(in ContainerOperation operation)
    {
        ItemContainerPage first = _containers[operation.Container].PageForSlot(operation.Slot);
        ItemContainerPage? second = SecondPage(operation);
        int added = first.IsDirty ? 0 : 1;
        if (second is not null && !ReferenceEquals(second, first) && !second.IsDirty) added++;
        return added;
    }

    /// <summary>The normalized intent's size once this operation joins, which is spec 6.5's two shapes.</summary>
    int ProjectedIntentBytes(in ContainerOperation operation)
    {
        if (Window.HoldsClientOperation) return _operations[0].CanonicalByteCount;
        if (operation.Origin == ContainerOperationOrigin.Client && _operations.Count == 0)
            return operation.CanonicalByteCount;

        int size = ContentVarint.Size((uint)(_operations.Count + 1)) + operation.CanonicalByteCount;
        foreach (ContainerOperation joined in _operations) size += joined.CanonicalByteCount;
        return size;
    }

    /// <summary>
    /// An UPPER bound on the commit's owned bytes once this operation joins: the intent, every event, every
    /// page already dirty, every page this operation would newly dirty at its CURRENT size, and the most the
    /// touched pages can grow by. It never underestimates, which is the only direction a cap may be
    /// approximated in. The result bytes are the caller's and <c>JournalCommit</c> counts them itself, so
    /// Close validates the real total against the same limits.
    /// </summary>
    int ProjectedCommitBytes(in ContainerOperation operation, int projectedIntentBytes)
    {
        ItemContainerPage first = _containers[operation.Container].PageForSlot(operation.Slot);
        ItemContainerPage? second = SecondPage(operation);
        int joining = first.IsDirty ? 0 : MeasurePage(first);
        if (second is not null && !ReferenceEquals(second, first) && !second.IsDirty) joining += MeasurePage(second);

        int eventBytes = operation.Kind == ContainerOperationKind.Craft
            ? operation.EventPayload.Length
            : operation.CanonicalByteCount;
        int growth = EntryBound(_containers[operation.Container].SlotAt(operation.Slot))
            + operation.Payload.Length
            + PageGrowthSlack;
        return projectedIntentBytes + _eventBytes + eventBytes + _pageBytes + joining + growth;
    }

    ItemContainerPage? SecondPage(in ContainerOperation operation) => operation.Kind switch
    {
        ContainerOperationKind.Move or ContainerOperationKind.Split or ContainerOperationKind.Merge =>
            _containers[operation.DestinationContainerOrOwn].PageForSlot(operation.DestinationSlot),
        ContainerOperationKind.Craft when operation.DefinitionId != 0 =>
            _containers[operation.DestinationContainerOrOwn].PageForSlot(operation.DestinationSlot),
        _ => null,
    };

    static int EntryBound(in ItemSlot slot)
        => slot.IsEmpty
            ? 0
            : ItemContainerPageCodec.EntryBodySize(
                slot.Quarantined ? ItemContainerPageCodec.EntryFlagQuarantined : 0u,
                slot.Stack.ItemId,
                slot.Stack.Count,
                slot.Stack.InstanceId,
                slot.Payload.Length);

    static ItemSlot Occupied(PagedItemContainer container, int slot, long instanceId)
    {
        ItemSlot held = container.SlotAt(slot);
        Require(!held.IsEmpty, "The operation names an empty slot.");

        // Spec 15.1: the declared instance id is what stops a replay landing on a slot something else has
        // refilled since, so the working copy is held to it here as well as in the hash.
        Require(
            held.Stack.InstanceId == instanceId,
            FormattableString.Invariant(
                $"Slot {slot} holds instance {held.Stack.InstanceId} and the operation declared {instanceId}."));
        return held;
    }

    static ItemSlot Fewer(in ItemSlot slot, int count)
        => slot with { Stack = slot.Stack with { Count = slot.Stack.Count - count } };

    static void RequirePlain(in ItemSlot slot)
        => Require(
            slot.Stack.InstanceId == 0 && slot.Payload.IsEmpty && !slot.Quarantined,
            "Units cannot be split off an owned item: an owned item is one item, and its payload is not divisible.");

    static void Require(bool condition, string message)
    {
        if (!condition) throw new ArgumentException(message);
    }
}
