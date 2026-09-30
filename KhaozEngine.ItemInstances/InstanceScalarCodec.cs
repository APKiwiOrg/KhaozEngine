using System;
using KhaozEngine.Catalog;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The value contracts of v1 kinds 1 to 6, applied after the shared unsigned 64 bit shape walk.
/// Charges and Durability bound each slot independently, without ordering Current and Maximum.
/// </summary>
internal sealed class InstanceScalarCodec : IInstancePropertyCodec
{
    internal static IInstancePropertyCodec Flags { get; } = new InstanceScalarCodec(1, 0, 7);
    internal static IInstancePropertyCodec ItemLevel { get; } = new InstanceScalarCodec(1, 1, ushort.MaxValue);
    internal static IInstancePropertyCodec Quality { get; } = new InstanceScalarCodec(1, 0, ushort.MaxValue);
    internal static IInstancePropertyCodec Charges { get; } = new InstanceScalarCodec(2, 0, uint.MaxValue);
    internal static IInstancePropertyCodec Durability { get; } = new InstanceScalarCodec(2, 0, ushort.MaxValue);
    internal static IInstancePropertyCodec BoundTo { get; } = new InstanceScalarCodec(1, 1, ulong.MaxValue);

    readonly int _slots;
    readonly ulong _minimum;
    readonly ulong _maximum;

    InstanceScalarCodec(int slots, ulong minimum, ulong maximum)
    {
        _slots = slots;
        _minimum = minimum;
        _maximum = maximum;
    }

    public bool TryValidate(ReadOnlySpan<byte> body, out string? reason)
    {
        int offset = 0;
        for (int slot = 0; slot < _slots; slot++)
        {
            if (!ContentVarint.TryReadUInt64(body, ref offset, out ulong value, out reason))
            {
                return false;
            }

            if (value < _minimum || value > _maximum)
            {
                reason = InstancePayloadReason.FieldMalformed;
                return false;
            }
        }

        reason = offset == body.Length ? null : InstancePayloadReason.FieldMalformed;
        return reason is null;
    }
}
