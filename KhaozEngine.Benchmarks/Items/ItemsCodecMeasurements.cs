using System;
using KhaozEngine.ItemInstances;
using KhaozEngine.Primitives;

namespace KhaozEngine.Benchmarks.Items;

internal readonly record struct CodecMeasurements(
    int RarePayloadBytes,
    int RareSlotEntryBytes,
    int CanonicalPageBytes,
    int GeneratedPageBytes,
    double GeneratedEntryMeanBytes,
    int ChunkCount,
    int FragmentGameMessageBytes,
    int FragmentWireBytes,
    int DeltaBytes,
    int MaximumChangedSlotsInOneFrame,
    int PublicViewBytes,
    int GroundComponentBytes,
    double GroundBytesPerViewerPerSecond);

/// <summary>
/// Budgets 1, 2, 3, 7, 8 and 11: everything that is answered by encoding something and counting the
/// bytes that came out.
/// </summary>
internal static class ItemsCodecMeasurements
{
    internal const int GroundInstancesInInterest = 28;
    internal const double TileTickSeconds = 0.25;

    internal static CodecMeasurements Measure(
        ItemGenerator generator,
        SyntheticContent content,
        IRandomSource random,
        int contentVersion)
    {
        byte[] payload = CanonicalRare.BuildPayload();
        Span<PayloadField> fields = stackalloc PayloadField[InstancePayload.MaximumFields];
        if (!InstancePayload.TryDecode(payload, fields, out _, out string? reason))
            throw new InvalidOperationException($"The canonical rare payload did not decode: {reason}.");

        PageSlotInput entry = CanonicalRare.SlotAt(0, payload);
        int slotEntryBytes = ContainerPageCodec.EntrySize(entry, 0);

        byte[] canonicalPage = CanonicalRare.BuildPage(0, contentVersion);
        (byte[] generatedPage, double generatedEntryMean) = BuildGeneratedPage(generator, content, random, 0, contentVersion);

        var fragmentBuffer = new byte[canonicalPage.Length + (256 * PageWire.FragmentHeaderBytes)];
        FragmentPlan plan = PageWire.Fragment(canonicalPage, fragmentBuffer);

        var deltaBuffer = new byte[PageWire.MaximumGameMessageBytes];
        var oneChange = new[] { entry };
        int deltaBytes = PageWire.TryBuildDelta(deltaBuffer, 1, 0, 0, oneChange);
        int maximumChanges = MaximumChangesInOneFrame(payload, deltaBuffer);

        Span<byte> publicView = stackalloc byte[InstancePayload.MaximumPayloadBytes];
        int publicViewBytes = InstancePayload.PublicView(payload, publicView);
        int componentBytes = publicViewBytes
            + Varint.Size((ulong)CanonicalRare.InstanceId)
            + Varint.Size((ulong)(uint)publicViewBytes);
        double perViewerPerSecond = componentBytes * GroundInstancesInInterest / TileTickSeconds;

        return new CodecMeasurements(
            payload.Length,
            slotEntryBytes,
            canonicalPage.Length,
            generatedPage.Length,
            generatedEntryMean,
            plan.ChunkCount,
            plan.GameMessageBytes,
            plan.WireBytes,
            deltaBytes,
            maximumChanges,
            publicViewBytes,
            componentBytes,
            perViewerPerSecond);
    }

    internal static (byte[] Page, double EntryMeanBytes) BuildGeneratedPage(
        ItemGenerator generator,
        SyntheticContent content,
        IRandomSource random,
        int pageIndex,
        int contentVersion)
    {
        var entries = new PageSlotInput[ContainerPageCodec.ContainerPageSlots];
        int firstSlot = pageIndex * ContainerPageCodec.ContainerPageSlots;
        long entryBytes = 0;
        for (int slot = 0; slot < entries.Length; slot++)
        {
            int baseId = content.BaseIdOf(random.NextInt(0, content.BaseCount));
            GenerationResult generated = generator.Generate(
                new GenerationContext(baseId, random.NextInt(1, 101), 0, 0, 0));
            entries[slot] = new PageSlotInput(
                firstSlot + slot,
                0,
                generated.BaseId,
                1,
                (ulong)generated.InstanceId,
                generated.Payload);
            entryBytes += ContainerPageCodec.EntrySize(entries[slot], firstSlot);
        }

        int size = ContainerPageCodec.EncodedSize(pageIndex, firstSlot, ContainerPageCodec.ContainerPageSlots, contentVersion, entries);
        var page = new byte[size];
        ContainerPageCodec.Encode(page, pageIndex, firstSlot, ContainerPageCodec.ContainerPageSlots, contentVersion, entries);
        return (page, (double)entryBytes / entries.Length);
    }

    private static int MaximumChangesInOneFrame(byte[] payload, byte[] deltaBuffer)
    {
        var changes = new PageSlotInput[ContainerPageCodec.ContainerPageSlots];
        for (int slot = 0; slot < changes.Length; slot++) changes[slot] = CanonicalRare.SlotAt(slot, payload);
        int fitting = 0;
        for (int count = 1; count <= changes.Length; count++)
        {
            if (PageWire.TryBuildDelta(deltaBuffer, 1, 0, 0, changes.AsSpan(0, count)) < 0) break;
            fitting = count;
        }

        return fitting;
    }
}
