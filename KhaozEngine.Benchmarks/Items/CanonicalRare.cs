using System;

namespace KhaozEngine.Benchmarks.Items;

/// <summary>
/// The PoE row of spec 3.8: the contracts 9.8 worked example (item level, durability, rarity, three
/// affixes, one occupied socket) plus identification state and a rolled rare name. It is built field by
/// field through the same encoder everything else uses, so the byte count budgets 1 and 2 report is an
/// encode rather than a transcription.
/// </summary>
internal static class CanonicalRare
{
    internal const int DefinitionId = 2_000;
    internal const long InstanceId = 1_000_000_000;

    internal static byte[] BuildPayload(int nameWordOffset = 0)
    {
        Span<byte> payload = stackalloc byte[InstancePayload.MaximumPayloadBytes];
        Span<byte> body = stackalloc byte[64];
        int written = 0;

        body[0] = 68;
        written += InstancePayload.WriteField(payload[written..], InstanceKinds.ItemLevel, body[..1]);

        body[0] = 90;
        body[1] = 100;
        written += InstancePayload.WriteField(payload[written..], InstanceKinds.Durability, body[..2]);

        body[0] = 1;
        body[1] = 0x0F;
        written += InstancePayload.WriteField(payload[written..], InstanceKinds.Identification, body[..2]);

        body[0] = 3;
        written += InstancePayload.WriteField(payload[written..], InstanceKinds.Rarity, body[..1]);

        body[0] = 3;
        int affixLength = 1;
        affixLength += InstancePayload.WriteAffixEntry(body[affixLength..], 91, 1, 13_107);
        affixLength += InstancePayload.WriteAffixEntry(body[affixLength..], 260, 2, 65_535);
        affixLength += InstancePayload.WriteAffixEntry(body[affixLength..], 4_210, 3, 52_428);
        written += InstancePayload.WriteField(payload[written..], InstanceKinds.Affixes, body[..affixLength]);

        Span<byte> nestedBody = stackalloc byte[4];
        nestedBody[0] = 55;
        Span<byte> nested = stackalloc byte[8];
        int nestedLength = InstancePayload.WriteField(nested, InstanceKinds.ItemLevel, nestedBody[..1]);
        int socketLength = Varint.Write(body, 1);
        socketLength += Varint.Write(body[socketLength..], 7);
        socketLength += Varint.Write(body[socketLength..], 833);
        socketLength += Varint.Write(body[socketLength..], 4_201);
        socketLength += Varint.Write(body[socketLength..], (ulong)(uint)nestedLength);
        nested[..nestedLength].CopyTo(body[socketLength..]);
        socketLength += nestedLength;
        written += InstancePayload.WriteField(payload[written..], InstanceKinds.Sockets, body[..socketLength]);

        int nameLength = Varint.Write(body, 3);
        body[nameLength++] = 3;
        nameLength += Varint.Write(body[nameLength..], (ulong)(uint)(11 + (nameWordOffset % 30)));
        nameLength += Varint.Write(body[nameLength..], (ulong)(uint)(42 + (nameWordOffset % 30)));
        nameLength += Varint.Write(body[nameLength..], (ulong)(uint)(97 + (nameWordOffset % 30)));
        written += InstancePayload.WriteField(payload[written..], InstanceKinds.RareName, body[..nameLength]);

        return payload[..written].ToArray();
    }

    internal static PageSlotInput SlotAt(int slot, byte[] payload)
        => new(slot, 0, DefinitionId + (slot % 90), 1, (ulong)(InstanceId + slot), payload);

    /// <summary>A whole page of 100 rares, spec 5.2's geometry.</summary>
    internal static byte[] BuildPage(int pageIndex, int contentVersion)
    {
        var entries = new PageSlotInput[ContainerPageCodec.ContainerPageSlots];
        int firstSlot = pageIndex * ContainerPageCodec.ContainerPageSlots;
        for (int slot = 0; slot < entries.Length; slot++)
            entries[slot] = SlotAt(firstSlot + slot, BuildPayload(slot));
        int size = ContainerPageCodec.EncodedSize(pageIndex, firstSlot, ContainerPageCodec.ContainerPageSlots, contentVersion, entries);
        var page = new byte[size];
        int written = ContainerPageCodec.Encode(page, pageIndex, firstSlot, ContainerPageCodec.ContainerPageSlots, contentVersion, entries);
        return written == size ? page : page.AsSpan(0, written).ToArray();
    }
}
