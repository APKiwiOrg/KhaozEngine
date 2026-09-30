using System;
using KhaozEngine.Catalog;
using KhaozEngine.ItemInstances;

namespace KhaozEngine.Tests.ItemInstances;

internal static class EntryOrderFixtures
{
    internal static InstanceFieldShape Shape(
        ReadOnlyMemory<InstanceSlotKind> header,
        InstanceCountWidth count,
        ReadOnlyMemory<InstanceSlotKind> entry,
        int order = 1)
        => new(header, count, entry, (InstanceEntryOrder)order);

    internal sealed class OrderedCodec(InstanceCountWidth width, bool marker = false, bool duplicates = false)
        : IInstancePropertyCodec
    {
        public bool TryValidate(ReadOnlySpan<byte> body, out string? reason)
        {
            reason = InstancePayloadReason.FieldMalformed;
            int offset = 0;
            uint count;
            if (width == InstanceCountWidth.Byte)
            {
                if (body.IsEmpty) return false;
                count = body[offset++];
            }
            else if (!ContentVarint.TryRead(body, ref offset, out count, out _)) return false;

            ulong previous = 0;
            for (uint index = 0; index < count; index++)
            {
                if (marker)
                {
                    if (offset >= body.Length) return false;
                    offset++;
                }

                if (!ContentVarint.TryReadUInt64(body, ref offset, out ulong id, out _)
                    || id == 0 || id < previous || (!duplicates && id == previous)) return false;

                previous = id;
            }

            if (offset != body.Length) return false;
            reason = null;
            return true;
        }
    }
}
