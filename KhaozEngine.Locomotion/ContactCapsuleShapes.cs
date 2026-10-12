using System;
using KhaozEngine.Physics;

namespace KhaozEngine.Locomotion;

internal enum ContactCapsuleRole { Body, Shell, RoundedSweep, RoundedContact }

/// <summary>Immutable exact-dimension shapes. Role-local slots retain a prepared route without body/shell eviction.</summary>
internal sealed class ContactCapsuleShapes
{
    // The route inventory needs up to three exact dimension pairs in one role. Retain that
    // allowance for each of the controller's eight mode segments. This bounds retained storage,
    // not query count. A new dimension is cold work, never a refusal or a changed geometry.
    internal const int SlotsPerRole = 8 * 3;
    readonly CapsuleShape?[] _slots = new CapsuleShape?[4 * SlotsPerRole];
    readonly int[] _next = new int[4];
    [ThreadStatic] static ContactCapsuleShapes? _threadShapes;
    internal static ContactCapsuleShapes ForThread => _threadShapes ??= new();

    internal CapsuleShape Get(ContactCapsuleRole role, float radius, float length)
    {
        int first = (int)role * SlotsPerRole;
        int radiusBits = BitConverter.SingleToInt32Bits(radius), lengthBits = BitConverter.SingleToInt32Bits(length);
        for (int i = first; i < first + SlotsPerRole; i++)
        {
            CapsuleShape? shape = _slots[i];
            if (shape is not null && BitConverter.SingleToInt32Bits(shape.Radius) == radiusBits &&
                BitConverter.SingleToInt32Bits(shape.Length) == lengthBits) return shape;
        }
        var created = new CapsuleShape(radius, length);
        int slot = _next[(int)role];
        _slots[first + slot] = created;
        _next[(int)role] = (slot + 1) % SlotsPerRole;
        return created;
    }
}
