using System;
using System.Buffers.Binary;
using System.IO;

namespace KhaozEngine.Items;

/// <summary>The durable and wire form of an <see cref="ItemContainer"/>: version, slot count, then one entry
/// per OCCUPIED slot, so an empty bank costs a header rather than a thousand empty rows.</summary>
/// <remarks>Little-endian by construction on every host (BinaryWriter's own contract), sparse by slot index,
/// entries in ascending slot order. The decoder builds the container through the caller's own slot count and
/// stackable rule, so a blob cannot smuggle a different geometry in: a blob whose declared slot count differs
/// from the caller's expectation is refused whole, which is the same severity the quarantine path wants.</remarks>
public static class ItemContainerCodec
{
    /// <summary>
    /// The container codec's CURRENT format version, contracts 15's public <c>ushort</c> named constant.
    /// Bump it when the shape changes, never reuse a number.
    /// <para>
    /// <b>Byte 0 is the dispatch, and it costs one rule.</b> Version 1 put a single <c>byte</c> at offset
    /// 0, so a reader has to tell a version 1 blob from a version 2 one before it knows how wide the
    /// version field is. The value 1 at byte 0 means the version 1 format, and ANYTHING ELSE means a
    /// <c>ushort</c> version whose low byte is that value. The cost is that container codec versions
    /// CONGRUENT TO 1 MODULO 256 are never assigned: version 257 is skipped, and versions 2 through 256
    /// are free. Assigning 257 would make every stored version 1 bank in the fleet unreadable, so it is
    /// written here rather than only in spec 4.4 and spec 21.
    /// </para>
    /// <para>
    /// This type reads version 1 and writes version 1. Version 2 is a PAGE, carrying instance ids, opaque
    /// payloads and a content version stamp, and its codec is <c>ItemContainerPageCodec</c> in
    /// <c>KhaozEngine.ItemInstances</c>, the package above this one. A version 2 blob handed to this type
    /// is refused by number rather than guessed at.
    /// </para>
    /// </summary>
    public const ushort Version = 2;

    /// <summary>The legacy single-byte version this type reads and writes, and the value byte 0 carries on
    /// every blob written before version 2 existed.</summary>
    public const byte Version1 = 1;

    const int HeaderBytes = 1 + 2;
    const int EntryBytes = 2 + 4 + 4;

    /// <summary>Encodes a container in the VERSION 1 format, which is what this type writes: a version 2
    /// page carries instance ids and payloads this package has no reader for, and its writer is
    /// <c>ItemContainerPageCodec.Encode</c> one package up. Never null, never empty.</summary>
    /// <param name="container">The container to encode.</param>
    public static byte[] Encode(ItemContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Version1);
            writer.Write((ushort)container.SlotCount);
            for (int i = 0; i < container.SlotCount; i++)
            {
                ItemStack stack = container[i];
                if (stack.IsEmpty) continue;
                writer.Write((ushort)i);
                writer.Write(stack.ItemId);
                writer.Write(stack.Count);
            }
        }
        return buffer.ToArray();
    }

    /// <summary>Decodes a blob into a fresh container built on the CALLER's geometry and rules. Null, empty,
    /// or malformed answers false, which is the caller's cue to seat a fresh container rather than throw.</summary>
    /// <param name="blob">The stored bytes.</param>
    /// <param name="slotCount">The slot count this consumer expects. A blob declaring another is refused.</param>
    /// <param name="stackable">The game's stackable rule, handed to the container built here.</param>
    /// <param name="container">The decoded container.</param>
    public static bool TryDecode(byte[]? blob, int slotCount, Func<int, bool> stackable,
        out ItemContainer container)
    {
        container = null!;
        ArgumentNullException.ThrowIfNull(stackable);
        if (Validate(blob, slotCount) is not null || blob is not { Length: > 0 }) return false;

        var decoded = new ItemContainer(slotCount, stackable);
        int count = (blob.Length - HeaderBytes) / EntryBytes;
        for (int i = 0; i < count; i++)
        {
            int at = HeaderBytes + (i * EntryBytes);
            int slot = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(at));
            int itemId = BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(at + 2));
            int stackCount = BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(at + 6));
            decoded.SetAt(slot, new ItemStack(itemId, stackCount));
        }
        container = decoded;
        return true;
    }

    /// <summary>Vets a blob for a persistence layer. A non-null return is the quarantine reason. Null input
    /// (no state) is not a fault.</summary>
    /// <param name="blob">The stored bytes.</param>
    /// <param name="expectedSlotCount">The geometry this consumer runs, which the blob must declare.</param>
    public static string? Validate(byte[]? blob, int expectedSlotCount)
    {
        if (blob is null or { Length: 0 }) return null;
        if (blob.Length < HeaderBytes) return "item container blob is shorter than its own header";
        // Byte 0 is the version dispatch (see Version). The value 1 is the version 1 format; anything else
        // means the version field is a ushort, so the refusal names the number the ushort reader found
        // rather than the byte, and a version 2 page is refused here by number for its own codec to read.
        if (blob[0] != Version1)
        {
            ushort declaredVersion = BinaryPrimitives.ReadUInt16LittleEndian(blob);
            return $"item container blob version {declaredVersion}, this build's version 1 reader reads {Version1}";
        }

        int declared = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(1));
        if (declared != expectedSlotCount)
            return $"item container blob declares {declared} slots, this consumer runs {expectedSlotCount}";
        int body = blob.Length - HeaderBytes;
        if (body % EntryBytes != 0)
            return $"item container blob body is {body} bytes, not a whole number of {EntryBytes}-byte entries";
        int entries = body / EntryBytes;
        if (entries > declared) return $"item container blob carries {entries} entries over {declared} slots";
        int previousSlot = -1;
        for (int i = 0; i < entries; i++)
        {
            int at = HeaderBytes + (i * EntryBytes);
            int slot = BinaryPrimitives.ReadUInt16LittleEndian(blob.AsSpan(at));
            int itemId = BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(at + 2));
            int count = BinaryPrimitives.ReadInt32LittleEndian(blob.AsSpan(at + 6));
            // Ascending, strictly: order is what makes a duplicate slot impossible without a second pass, and
            // Encode only ever writes ascending, so anything else is corruption rather than a variant.
            if (slot <= previousSlot) return $"item container blob entry {i} is out of order at slot {slot}";
            previousSlot = slot;
            if (slot >= declared) return $"item container blob entry {i} names slot {slot} of {declared}";
            if (itemId == 0) return $"item container blob entry {i} is an occupied slot with the empty id";
            if (itemId < 0) return $"item container blob entry {i} carries item id {itemId}, below zero";
            if (count <= 0) return $"item container blob entry {i} carries count {count}, not positive";
        }
        return null;
    }
}
