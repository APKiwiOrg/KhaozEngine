using System;

namespace KhaozEngine.Benchmarks.Items;

internal readonly record struct FragmentPlan(int ChunkCount, int GameMessageBytes, int WireBytes);

/// <summary>
/// Spec 7.5's two shapes. The fragmenter is the floor a cold open and a correction resync both need,
/// and the delta is the optimisation that makes the steady state one frame. A delta that would not fit
/// one frame is never sent: the builder measures as it writes and abandons to the fragmenter.
/// </summary>
internal static class PageWire
{
    internal const int MaximumGameMessageBytes = 1_024;
    internal const int EnvelopeBytes = 4;
    internal const int FragmentHeaderBytes = 5;
    internal const int ChunkPayloadBytes = MaximumGameMessageBytes - EnvelopeBytes - FragmentHeaderBytes;
    internal const int DeltaHeaderBytes = 3;

    internal static FragmentPlan Fragment(ReadOnlySpan<byte> page, Span<byte> destination)
    {
        int chunkCount = (page.Length + ChunkPayloadBytes - 1) / ChunkPayloadBytes;
        if (chunkCount > 255) throw new ArgumentOutOfRangeException(nameof(page), page.Length, "A page above 255 chunks cannot be fragmented.");
        int written = 0;
        for (int chunk = 0; chunk < chunkCount; chunk++)
        {
            int start = chunk * ChunkPayloadBytes;
            int length = Math.Min(ChunkPayloadBytes, page.Length - start);
            destination[written++] = 1;
            destination[written++] = (byte)1;
            destination[written++] = 0;
            destination[written++] = (byte)chunk;
            destination[written++] = (byte)chunkCount;
            page.Slice(start, length).CopyTo(destination[written..]);
            written += length;
        }

        return new FragmentPlan(chunkCount, written, written + (chunkCount * EnvelopeBytes));
    }

    /// <summary>
    /// The delta of 7.5. Answers -1 when the next change would not fit the one frame budget, which is
    /// the signal to abandon the delta and send the whole page through the fragmenter.
    /// </summary>
    internal static int TryBuildDelta(
        Span<byte> destination,
        byte containerId,
        byte pageIndex,
        int firstSlot,
        ReadOnlySpan<PageSlotInput> changes)
    {
        int budget = MaximumGameMessageBytes - EnvelopeBytes;
        destination[0] = containerId;
        destination[1] = pageIndex;
        destination[2] = (byte)changes.Length;
        int written = DeltaHeaderBytes;
        foreach (PageSlotInput change in changes)
        {
            int cost = Varint.Size((ulong)(uint)(change.Slot - firstSlot))
                + 1
                + (change.DefinitionId == 0 ? 0 : ContainerPageCodec.DeltaEntryBodySize(change, firstSlot));
            if (written + cost > budget) return -1;
            written += Varint.Write(destination[written..], (ulong)(uint)(change.Slot - firstSlot));
            if (change.DefinitionId == 0)
            {
                destination[written++] = 0x00;
                continue;
            }

            destination[written++] = 0x01;
            written += ContainerPageCodec.WriteEntryBody(destination[written..], change);
        }

        return written;
    }
}
