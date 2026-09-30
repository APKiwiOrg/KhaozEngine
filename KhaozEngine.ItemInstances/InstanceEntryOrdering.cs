using System;

namespace KhaozEngine.ItemInstances;

/// <summary>Normalizes legacy declarations and refuses entry ordering the structural walker cannot apply.</summary>
internal static class InstanceEntryOrdering
{
    internal static InstanceFieldShape Normalize(
        ushort kind,
        IInstancePropertyCodec codec,
        in InstanceFieldShape shape,
        ReadOnlySpan<InstanceReferenceTarget> references)
    {
        InstanceFieldShape normalized = shape;
        if (shape.EntryOrder == InstanceEntryOrder.Authored && ReferenceEquals(codec, InstancePropertyCodec.AffixList))
        {
            normalized = shape with { EntryOrder = InstanceEntryOrder.AscendingByEntryReference };
        }

        if (normalized.EntryOrder == InstanceEntryOrder.Authored) return normalized;

        if (normalized.EntryOrder != InstanceEntryOrder.AscendingByEntryReference)
        {
            throw Refusal(kind, "the order is unknown");
        }

        if (normalized.Count is not (InstanceCountWidth.Byte or InstanceCountWidth.Varint)
            || normalized.Entry.IsEmpty)
        {
            throw Refusal(kind, "ascending order requires a repeating field with a byte or varint count");
        }

        if (normalized.Nests)
        {
            throw Refusal(kind, "ascending order cannot carry nested payloads in the header or entries");
        }

        int entryReferences = 0;
        foreach (InstanceReferenceTarget reference in references)
        {
            if (reference.Site == InstanceReferenceSite.Header) continue;

            if (reference.Site != InstanceReferenceSite.Entry)
            {
                throw Refusal(kind, "ascending order requires a known reference site");
            }

            entryReferences++;
            InstanceSlotKind slot = normalized.Entry.Span[reference.SlotIndex];
            if (slot is not (InstanceSlotKind.Varint or InstanceSlotKind.Byte or InstanceSlotKind.Fixed2))
            {
                throw Refusal(kind, "ascending order requires a scalar entry reference slot");
            }
        }

        if (entryReferences != 1)
        {
            throw Refusal(kind, "ascending order requires exactly one entry reference target");
        }

        return normalized;
    }

    static ArgumentException Refusal(ushort kind, string detail)
        => new(FormattableString.Invariant($"Kind {kind} declares an unsupported entry order because {detail}."), "shape");
}
