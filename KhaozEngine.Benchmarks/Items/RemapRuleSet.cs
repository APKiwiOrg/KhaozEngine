using System;
using System.Collections.Generic;

namespace KhaozEngine.Benchmarks.Items;

/// <summary>Content type ids the spike's reference targets name, spec 3.3.</summary>
internal static class ContentTypeIds
{
    internal const ushort Item = 1;
    internal const ushort Mod = 2;
    internal const ushort SocketType = 3;
    internal const ushort RarityRule = 4;
    internal const ushort RareNameWord = 5;
    internal const ushort UniqueTemplate = 6;
}

internal readonly record struct RemapRule(int Sequence, int IntroducedIn, ushort TypeId, byte Kind, int FromId, int ToId);

internal readonly record struct RemapOutcome(int IdsVisited, int IdsRewritten, int BytesWritten);

/// <summary>
/// Contracts 8.3's pass: every rule whose <c>IntroducedIn</c> is strictly greater than the page stamp,
/// in <c>Sequence</c> order, in one pass. A rule that changes nothing is a SCAN rather than a rewrite,
/// which is almost every rule on almost every page, so the scan never writes and never re-encodes. A
/// rewrite re-encodes innermost first, because a <c>ReplacedBy</c> can change a varint's WIDTH.
/// </summary>
internal sealed class RemapRuleSet
{
    private readonly Dictionary<long, int> replacements = new();

    internal RemapRuleSet(IReadOnlyList<RemapRule> rules, int pageStamp)
    {
        RuleCount = rules.Count;
        foreach (RemapRule rule in rules)
        {
            if (rule.IntroducedIn <= pageStamp) continue;
            Applicable++;
            if (rule.Kind != 1 && rule.Kind != 3) continue;
            replacements[Key(rule.TypeId, rule.FromId)] = rule.ToId;
        }
    }

    internal int RuleCount { get; }
    internal int Applicable { get; private set; }

    private static long Key(ushort typeId, int fromId) => ((long)typeId << 32) | (uint)fromId;

    private int Lookup(ushort typeId, int fromId, ref int visited, ref int rewritten)
    {
        visited++;
        if (!replacements.TryGetValue(Key(typeId, fromId), out int toId)) return fromId;
        rewritten++;
        return toId;
    }

    /// <summary>
    /// Walks every reference id in one page. Answers <c>BytesWritten</c> 0 when nothing matched, which
    /// is the no-rewrite case the budget is derived against.
    /// </summary>
    internal RemapOutcome ApplyToPage(ReadOnlySpan<byte> page, Span<PageEntry> entries, Span<byte> destination)
    {
        int visited = 0;
        int rewritten = 0;
        if (!ContainerPageCodec.TryDecode(page, ContainerPageCodec.ContainerPageSlots, entries, out PageHeader header, out int entryCount, out _))
            return new RemapOutcome(0, 0, 0);

        for (int index = 0; index < entryCount; index++)
        {
            PageEntry entry = entries[index];
            _ = Lookup(ContentTypeIds.Item, entry.DefinitionId, ref visited, ref rewritten);
            ScanPayload(page.Slice(entry.PayloadStart, entry.PayloadLength), ref visited, ref rewritten);
        }

        if (rewritten == 0) return new RemapOutcome(visited, 0, 0);
        int written = Rewrite(page, entries[..entryCount], header, destination);
        return new RemapOutcome(visited, rewritten, written);
    }

    private void ScanPayload(ReadOnlySpan<byte> payload, ref int visited, ref int rewritten)
    {
        Span<PayloadField> fields = stackalloc PayloadField[InstancePayload.MaximumFields];
        if (!InstancePayload.TryDecode(payload, fields, out int fieldCount, out _)) return;
        for (int index = 0; index < fieldCount; index++)
        {
            PayloadField field = fields[index];
            ReadOnlySpan<byte> body = payload.Slice(field.BodyStart, field.BodyLength);
            switch (field.Kind)
            {
                case InstanceKinds.UniqueTemplate:
                    ScanScalar(body, ContentTypeIds.UniqueTemplate, ref visited, ref rewritten);
                    break;
                case InstanceKinds.Rarity:
                    _ = Lookup(ContentTypeIds.RarityRule, body[0], ref visited, ref rewritten);
                    break;
                case InstanceKinds.Affixes:
                case InstanceKinds.Enchantments:
                    ScanAffixes(body, ref visited, ref rewritten);
                    break;
                case InstanceKinds.Sockets:
                    ScanSockets(body, ref visited, ref rewritten);
                    break;
                case InstanceKinds.RareName:
                    ScanRareName(body, ref visited, ref rewritten);
                    break;
                default:
                    break;
            }
        }
    }

    private void ScanScalar(ReadOnlySpan<byte> body, ushort typeId, ref int visited, ref int rewritten)
    {
        int offset = 0;
        if (Varint.TryRead(body, ref offset, Varint.MaximumBytes32, out ulong value, out _))
            _ = Lookup(typeId, (int)value, ref visited, ref rewritten);
    }

    private void ScanAffixes(ReadOnlySpan<byte> body, ref int visited, ref int rewritten)
    {
        int count = body[0];
        int offset = 1;
        for (int entry = 0; entry < count; entry++)
        {
            if (!Varint.TryRead(body, ref offset, Varint.MaximumBytes32, out ulong modId, out _)) return;
            _ = Lookup(ContentTypeIds.Mod, (int)modId, ref visited, ref rewritten);
            offset += 3;
            if (!Varint.TryRead(body, ref offset, Varint.MaximumBytes32, out _, out _)) return;
        }
    }

    private void ScanSockets(ReadOnlySpan<byte> body, ref int visited, ref int rewritten)
    {
        int offset = 0;
        if (!Varint.TryRead(body, ref offset, Varint.MaximumBytes32, out ulong count, out _)) return;
        for (ulong entry = 0; entry < count; entry++)
        {
            if (!Varint.TryRead(body, ref offset, Varint.MaximumBytes32, out ulong socketType, out _)) return;
            _ = Lookup(ContentTypeIds.SocketType, (int)socketType, ref visited, ref rewritten);
            if (!Varint.TryRead(body, ref offset, Varint.MaximumBytes32, out ulong contained, out _)) return;
            _ = Lookup(ContentTypeIds.Item, (int)contained, ref visited, ref rewritten);
            if (!Varint.TryRead(body, ref offset, Varint.MaximumBytes64, out _, out _)) return;
            if (!Varint.TryRead(body, ref offset, Varint.MaximumBytes32, out ulong nestedLength, out _)) return;
            if (nestedLength > (ulong)(body.Length - offset)) return;
            ScanPayload(body.Slice(offset, (int)nestedLength), ref visited, ref rewritten);
            offset += (int)nestedLength;
        }
    }

    private void ScanRareName(ReadOnlySpan<byte> body, ref int visited, ref int rewritten)
    {
        int offset = 0;
        if (!Varint.TryRead(body, ref offset, Varint.MaximumBytes32, out ulong rarity, out _)) return;
        _ = Lookup(ContentTypeIds.RarityRule, (int)rarity, ref visited, ref rewritten);
        if (offset >= body.Length) return;
        int words = body[offset++];
        for (int word = 0; word < words; word++)
        {
            if (!Varint.TryRead(body, ref offset, Varint.MaximumBytes32, out ulong wordId, out _)) return;
            _ = Lookup(ContentTypeIds.RareNameWord, (int)wordId, ref visited, ref rewritten);
        }
    }

    /// <summary>
    /// The re-encode a hit forces. It rebuilds the payload innermost first so a widened varint carries
    /// the two lengths above it, and it restores canonical order on the affix list, because a
    /// replacement can move a mod id past its neighbour.
    /// </summary>
    private int Rewrite(ReadOnlySpan<byte> page, ReadOnlySpan<PageEntry> entries, in PageHeader header, Span<byte> destination)
    {
        Span<byte> payloadScratch = stackalloc byte[InstancePayload.MaximumPayloadBytes];
        var rebuilt = new PageSlotInput[entries.Length];
        var owned = new byte[entries.Length][];
        int visited = 0;
        int rewritten = 0;
        for (int index = 0; index < entries.Length; index++)
        {
            PageEntry entry = entries[index];
            int written = RewritePayload(page.Slice(entry.PayloadStart, entry.PayloadLength), payloadScratch, ref visited, ref rewritten);
            owned[index] = payloadScratch[..written].ToArray();
            int definitionId = Lookup(ContentTypeIds.Item, entry.DefinitionId, ref visited, ref rewritten);
            rebuilt[index] = new PageSlotInput(entry.Slot, entry.Flags, definitionId, entry.Count, entry.InstanceId, owned[index]);
        }

        return ContainerPageCodec.Encode(
            destination,
            header.PageIndex,
            header.FirstSlot,
            header.SlotCount,
            header.ContentVersion,
            rebuilt);
    }

    private int RewritePayload(ReadOnlySpan<byte> payload, Span<byte> destination, ref int visited, ref int rewritten)
    {
        Span<PayloadField> fields = stackalloc PayloadField[InstancePayload.MaximumFields];
        if (!InstancePayload.TryDecode(payload, fields, out int fieldCount, out _))
        {
            payload.CopyTo(destination);
            return payload.Length;
        }

        Span<byte> body = stackalloc byte[InstancePayload.MaximumPayloadBytes];
        int written = 0;
        for (int index = 0; index < fieldCount; index++)
        {
            PayloadField field = fields[index];
            ReadOnlySpan<byte> source = payload.Slice(field.BodyStart, field.BodyLength);
            int bodyLength;
            switch (field.Kind)
            {
                case InstanceKinds.Affixes:
                case InstanceKinds.Enchantments:
                    bodyLength = RewriteAffixes(source, body, ref visited, ref rewritten);
                    break;
                default:
                    source.CopyTo(body);
                    bodyLength = source.Length;
                    break;
            }

            written += InstancePayload.WriteField(destination[written..], field.Kind, body[..bodyLength]);
        }

        return written;
    }

    private int RewriteAffixes(ReadOnlySpan<byte> source, Span<byte> destination, ref int visited, ref int rewritten)
    {
        int count = source[0];
        Span<int> mods = stackalloc int[64];
        Span<byte> tiers = stackalloc byte[64];
        Span<ushort> positions = stackalloc ushort[64];
        int offset = 1;
        for (int entry = 0; entry < count && entry < mods.Length; entry++)
        {
            if (!Varint.TryRead(source, ref offset, Varint.MaximumBytes32, out ulong modId, out _)) break;
            mods[entry] = Lookup(ContentTypeIds.Mod, (int)modId, ref visited, ref rewritten);
            tiers[entry] = source[offset++];
            positions[entry] = System.Buffers.Binary.BinaryPrimitives.ReadUInt16LittleEndian(source[offset..]);
            offset += 2;
            if (!Varint.TryRead(source, ref offset, Varint.MaximumBytes32, out _, out _)) break;
        }

        for (int outer = 1; outer < count; outer++)
        {
            int mod = mods[outer];
            byte tier = tiers[outer];
            ushort position = positions[outer];
            int inner = outer - 1;
            while (inner >= 0 && mods[inner] > mod)
            {
                mods[inner + 1] = mods[inner];
                tiers[inner + 1] = tiers[inner];
                positions[inner + 1] = positions[inner];
                inner--;
            }

            mods[inner + 1] = mod;
            tiers[inner + 1] = tier;
            positions[inner + 1] = position;
        }

        destination[0] = (byte)count;
        int written = 1;
        for (int entry = 0; entry < count; entry++)
            written += InstancePayload.WriteAffixEntry(destination[written..], mods[entry], tiers[entry], positions[entry]);
        return written;
    }
}
