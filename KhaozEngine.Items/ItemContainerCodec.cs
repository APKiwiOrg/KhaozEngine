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
    /// <summary>The format byte every blob starts with. Bump it when the shape changes, never reuse it.</summary>
    public const byte Version = 1;

    const int HeaderBytes = 1 + 2;
    const int EntryBytes = 2 + 4 + 4;

    /// <summary>Encodes a container. Never null, never empty.</summary>
    /// <param name="container">The container to encode.</param>
    public static byte[] Encode(ItemContainer container)
    {
        ArgumentNullException.ThrowIfNull(container);
        using var buffer = new MemoryStream();
        using (var writer = new BinaryWriter(buffer, System.Text.Encoding.UTF8, leaveOpen: true))
        {
            writer.Write(Version);
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
        if (blob[0] != Version) return $"item container blob version {blob[0]}, this build reads {Version}";
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
