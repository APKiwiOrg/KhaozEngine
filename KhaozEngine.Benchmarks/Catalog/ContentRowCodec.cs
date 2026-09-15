using System;
using System.Text;

namespace KhaozEngine.Benchmarks.Catalog;

/// <summary>
/// The row codec for the six engine types plus the synthetic game type. Fields are written POSITIONALLY in
/// schema order and a marker field takes zero width (spec sections 3.2 and 7.9). Every decode path is
/// TOTAL: a malformed row returns false with the offset untouched, and nothing here throws.
/// </summary>
public static class ContentRowCodec
{
    /// <summary>Asset references are constrained to 128 bytes by their type's codec (section 3.3).</summary>
    public const int MaxAssetReferenceBytes = 128;

    public static int EncodeTag(in TagRowData row, Span<byte> destination) =>
        ContentVarint.Write(destination, (uint)row.Sort);

    public static bool TryDecodeTag(ReadOnlySpan<byte> source, ref TagRowData row)
    {
        int offset = 0;
        if (!ContentVarint.TryRead(source, ref offset, out uint sort)) return false;
        row.Sort = (int)sort;
        return offset == source.Length;
    }

    public static int EncodeItem(in ItemRowData row, Span<byte> destination)
    {
        int written = ContentVarint.Write(destination, (uint)row.TagCount);
        if (row.TagCount > 0) written += ContentVarint.Write(destination[written..], (uint)row.Tag0);
        if (row.TagCount > 1) written += ContentVarint.Write(destination[written..], (uint)row.Tag1);
        if (row.TagCount > 2) written += ContentVarint.Write(destination[written..], (uint)row.Tag2);
        if (row.TagCount > 3) written += ContentVarint.Write(destination[written..], (uint)row.Tag3);
        destination[written++] = row.Stackable ? (byte)1 : (byte)0;
        written += ContentVarint.Write(destination[written..], (uint)row.MaxStack);
        destination[written++] = row.Tradable ? (byte)1 : (byte)0;
        written += ContentVarint.Write(destination[written..], (uint)row.Value);
        written += WriteAsset(destination[written..], row.Icon);
        written += WriteAsset(destination[written..], row.Mesh);
        written += WriteAsset(destination[written..], row.HeldMesh);
        written += ContentVarint.Write(destination[written..], (uint)row.GroundPose);
        written += ContentVarint.WriteSigned(destination[written..], row.IconTilt);
        written += ContentVarint.WriteSigned(destination[written..], row.IconSpin);
        written += ContentVarint.Write(destination[written..], (uint)row.DurabilityMax);
        written += ContentVarint.Write(destination[written..], (uint)row.SocketMax);
        written += ContentVarint.Write(destination[written..], (uint)row.EquipProfile);
        return written;
    }

    public static bool TryDecodeItem(ReadOnlySpan<byte> source, ref ItemRowData row)
    {
        int offset = 0;
        if (!ContentVarint.TryRead(source, ref offset, out uint tagCount) || tagCount > 4) return false;
        row.TagCount = (int)tagCount;
        row.Tag0 = row.Tag1 = row.Tag2 = row.Tag3 = 0;
        for (int i = 0; i < tagCount; i++)
        {
            if (!ContentVarint.TryRead(source, ref offset, out uint tag)) return false;
            switch (i)
            {
                case 0: row.Tag0 = (int)tag; break;
                case 1: row.Tag1 = (int)tag; break;
                case 2: row.Tag2 = (int)tag; break;
                default: row.Tag3 = (int)tag; break;
            }
        }
        if (offset >= source.Length) return false;
        row.Stackable = source[offset++] != 0;
        if (!ContentVarint.TryRead(source, ref offset, out uint maxStack)) return false;
        row.MaxStack = (int)maxStack;
        if (offset >= source.Length) return false;
        row.Tradable = source[offset++] != 0;
        if (!ContentVarint.TryRead(source, ref offset, out uint value)) return false;
        row.Value = (int)value;
        if (!TryReadAsset(source, ref offset, out row.Icon)) return false;
        if (!TryReadAsset(source, ref offset, out row.Mesh)) return false;
        if (!TryReadAsset(source, ref offset, out row.HeldMesh)) return false;
        if (!ContentVarint.TryRead(source, ref offset, out uint pose)) return false;
        row.GroundPose = (int)pose;
        if (!ContentVarint.TryReadSigned(source, ref offset, out row.IconTilt)) return false;
        if (!ContentVarint.TryReadSigned(source, ref offset, out row.IconSpin)) return false;
        if (!ContentVarint.TryRead(source, ref offset, out uint durability)) return false;
        row.DurabilityMax = (int)durability;
        if (!ContentVarint.TryRead(source, ref offset, out uint sockets)) return false;
        row.SocketMax = (int)sockets;
        if (!ContentVarint.TryRead(source, ref offset, out uint equip)) return false;
        row.EquipProfile = (int)equip;
        return offset == source.Length;
    }

    public static int EncodeStat(in StatRowData row, Span<byte> destination)
    {
        int written = ContentVarint.Write(destination, (uint)row.Scale);
        written += ContentVarint.WriteSigned(destination[written..], row.Min);
        written += ContentVarint.WriteSigned(destination[written..], row.Max);
        written += ContentVarint.Write(destination[written..], (uint)row.TagCount);
        if (row.TagCount > 0) written += ContentVarint.Write(destination[written..], (uint)row.Tag0);
        if (row.TagCount > 1) written += ContentVarint.Write(destination[written..], (uint)row.Tag1);
        return written;
    }

    public static bool TryDecodeStat(ReadOnlySpan<byte> source, ref StatRowData row)
    {
        int offset = 0;
        if (!ContentVarint.TryRead(source, ref offset, out uint scale)) return false;
        row.Scale = (int)scale;
        if (!ContentVarint.TryReadSigned(source, ref offset, out row.Min)) return false;
        if (!ContentVarint.TryReadSigned(source, ref offset, out row.Max)) return false;
        if (!ContentVarint.TryRead(source, ref offset, out uint tagCount) || tagCount > 2) return false;
        row.TagCount = (int)tagCount;
        row.Tag0 = row.Tag1 = 0;
        if (tagCount > 0)
        {
            if (!ContentVarint.TryRead(source, ref offset, out uint t0)) return false;
            row.Tag0 = (int)t0;
        }
        if (tagCount > 1)
        {
            if (!ContentVarint.TryRead(source, ref offset, out uint t1)) return false;
            row.Tag1 = (int)t1;
        }
        return offset == source.Length;
    }

    public static int EncodeLootTable(in LootTableRowData row, Span<byte> destination)
    {
        int written = ContentVarint.Write(destination, (uint)row.RollCount);
        written += ContentVarint.Write(destination[written..], (uint)row.TagCount);
        if (row.TagCount > 0) written += ContentVarint.Write(destination[written..], (uint)row.Tag0);
        destination[written++] = row.Guaranteed ? (byte)1 : (byte)0;
        return written;
    }

    public static bool TryDecodeLootTable(ReadOnlySpan<byte> source, ref LootTableRowData row)
    {
        int offset = 0;
        if (!ContentVarint.TryRead(source, ref offset, out uint rollCount)) return false;
        row.RollCount = (int)rollCount;
        if (!ContentVarint.TryRead(source, ref offset, out uint tagCount) || tagCount > 1) return false;
        row.TagCount = (int)tagCount;
        row.Tag0 = 0;
        if (tagCount > 0)
        {
            if (!ContentVarint.TryRead(source, ref offset, out uint t0)) return false;
            row.Tag0 = (int)t0;
        }
        if (offset >= source.Length) return false;
        row.Guaranteed = source[offset++] != 0;
        return offset == source.Length;
    }

    public static int EncodeLootEntry(in LootEntryRowData row, Span<byte> destination)
    {
        int written = ContentVarint.Write(destination, (uint)row.Table);
        written += ContentVarint.Write(destination[written..], (uint)row.Item);
        written += ContentVarint.Write(destination[written..], (uint)row.NestedTable);
        written += ContentVarint.Write(destination[written..], (uint)row.Weight);
        written += ContentVarint.Write(destination[written..], (uint)row.ChanceBp);
        written += ContentVarint.Write(destination[written..], (uint)row.MinCount);
        written += ContentVarint.Write(destination[written..], (uint)row.MaxCount);
        written += ContentVarint.Write(destination[written..], (uint)row.Sort);
        written += ContentVarint.Write(destination[written..], (uint)row.RequiredTagCount);
        if (row.RequiredTagCount > 0) written += ContentVarint.Write(destination[written..], (uint)row.RequiredTag0);
        return written;
    }

    public static bool TryDecodeLootEntry(ReadOnlySpan<byte> source, ref LootEntryRowData row)
    {
        int offset = 0;
        if (!ReadInt(source, ref offset, out row.Table)) return false;
        if (!ReadInt(source, ref offset, out row.Item)) return false;
        if (!ReadInt(source, ref offset, out row.NestedTable)) return false;
        if (!ReadInt(source, ref offset, out row.Weight)) return false;
        if (!ReadInt(source, ref offset, out row.ChanceBp)) return false;
        if (!ReadInt(source, ref offset, out row.MinCount)) return false;
        if (!ReadInt(source, ref offset, out row.MaxCount)) return false;
        if (!ReadInt(source, ref offset, out row.Sort)) return false;
        if (!ContentVarint.TryRead(source, ref offset, out uint tagCount) || tagCount > 1) return false;
        row.RequiredTagCount = (int)tagCount;
        row.RequiredTag0 = 0;
        if (tagCount > 0 && !ReadInt(source, ref offset, out row.RequiredTag0)) return false;
        return offset == source.Length;
    }

    public static int EncodeBaseSocket(in BaseSocketRowData row, Span<byte> destination)
    {
        int written = ContentVarint.Write(destination, (uint)row.Item);
        written += ContentVarint.Write(destination[written..], (uint)row.Sort);
        written += ContentVarint.Write(destination[written..], (uint)row.SocketType);
        return written;
    }

    public static bool TryDecodeBaseSocket(ReadOnlySpan<byte> source, ref BaseSocketRowData row)
    {
        int offset = 0;
        if (!ReadInt(source, ref offset, out row.Item)) return false;
        if (!ReadInt(source, ref offset, out row.Sort)) return false;
        if (!ReadInt(source, ref offset, out row.SocketType)) return false;
        return offset == source.Length;
    }

    public static int EncodeGame(in GameRowData row, Span<byte> destination)
    {
        int written = ContentVarint.Write(destination, (uint)row.Slot);
        written += ContentVarint.Write(destination[written..], (uint)row.WeaponArchetype);
        return written;
    }

    public static bool TryDecodeGame(ReadOnlySpan<byte> source, ref GameRowData row)
    {
        int offset = 0;
        if (!ReadInt(source, ref offset, out row.Slot)) return false;
        if (!ReadInt(source, ref offset, out row.WeaponArchetype)) return false;
        return offset == source.Length;
    }

    private static bool ReadInt(ReadOnlySpan<byte> source, ref int offset, out int value)
    {
        if (!ContentVarint.TryRead(source, ref offset, out uint raw)) { value = 0; return false; }
        value = (int)raw;
        return true;
    }

    private static int WriteAsset(Span<byte> destination, string? reference)
    {
        if (string.IsNullOrEmpty(reference)) return ContentVarint.Write(destination, 0);
        int prefix = ContentVarint.Write(destination, (uint)reference.Length);
        int bytes = Encoding.UTF8.GetBytes(reference, destination[prefix..]);
        return prefix + bytes;
    }

    private static bool TryReadAsset(ReadOnlySpan<byte> source, ref int offset, out string value)
    {
        value = string.Empty;
        if (!ContentVarint.TryRead(source, ref offset, out uint length)) return false;
        if (length > MaxAssetReferenceBytes) return false;
        if (offset + (int)length > source.Length) return false;
        if (length > 0) value = Encoding.UTF8.GetString(source.Slice(offset, (int)length));
        offset += (int)length;
        return true;
    }
}
