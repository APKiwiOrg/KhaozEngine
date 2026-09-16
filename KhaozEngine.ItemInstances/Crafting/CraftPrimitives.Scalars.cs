using System;

namespace KhaozEngine.ItemInstances;

/// <summary>
/// The four primitives of spec 10.2 whose subject is a SCALAR FIELD: 11 <c>Repair</c>, 12
/// <c>SetQuality</c>, 13 <c>Identify</c> and 14 <c>SetFlag</c>.
/// <para>
/// <b>A scalar at its zero value REMOVES its field rather than writing a zero.</b> Kind 1 with no bits set
/// and kind 3 at quality 0 are what an item that never had either carries, and the generator writes neither,
/// so writing a zero would give one set of properties two byte forms and stop two identical items stacking
/// (spec 4.6). Kind 5 is the exception and always keeps both halves, because a durability of 0 out of 120 is
/// a real state and an absent kind 5 is a different item entirely.
/// </para>
/// </summary>
public static partial class CraftPrimitives
{
    /// <summary>The highest flag bit v1 assigns, which is bit 2, <c>Fractured</c>.</summary>
    public const int MaxFlagBit = 2;

    /// <summary>
    /// Primitive 11. Raises kind 5's current toward its maximum. An <paramref name="amount"/> of 0 raises it
    /// TO the maximum and no further, and any amount stops there too, so no craft can raise the ceiling.
    /// </summary>
    /// <param name="copy">The craft in progress.</param>
    /// <param name="amount">How much to repair, or 0 for a full repair.</param>
    /// <returns>The refusal, or null when the step applied.</returns>
    public static CraftRefusal? Repair(ref CraftWorkingCopy copy, int amount)
    {
        if (copy.IsRefused)
        {
            return copy.Refusal;
        }

        if (amount < 0)
        {
            return copy.Refuse(new CraftRefusal(CraftRefusalKind.ValueOutOfRange, amount));
        }

        if (!copy.TryGetPair(InstancePropertyKind.Durability, out ulong current, out ulong maximum))
        {
            return copy.Refuse(new CraftRefusal(CraftRefusalKind.FieldAbsent, InstancePropertyKind.Durability));
        }

        ulong raised = amount == 0 ? maximum : Math.Min(current + (ulong)amount, maximum);
        return copy.SetPair(InstancePropertyKind.Durability, raised, maximum) ? null : copy.Refusal;
    }

    /// <summary>
    /// Primitive 12. Writes kind 3, as a DELTA on what the item carries or as an ABSOLUTE. A delta walking
    /// off either end clamps, because walking off an end is ordinary authoring, while an absolute outside
    /// the field's own range is a row that means something the format cannot store and is refused.
    /// </summary>
    /// <param name="copy">The craft in progress.</param>
    /// <param name="value">The delta, or the absolute quality in whole percentage points.</param>
    /// <param name="absolute">Whether <paramref name="value"/> is the quality rather than a change to it.</param>
    /// <returns>The refusal, or null when the step applied.</returns>
    public static CraftRefusal? SetQuality(ref CraftWorkingCopy copy, int value, bool absolute)
    {
        if (copy.IsRefused)
        {
            return copy.Refusal;
        }

        if (absolute && value is < 0 or > ushort.MaxValue)
        {
            return copy.Refuse(new CraftRefusal(CraftRefusalKind.ValueOutOfRange, InstancePropertyKind.Quality));
        }

        _ = copy.TryGetScalar(InstancePropertyKind.Quality, out ulong held);
        long quality = absolute ? value : (long)held + value;
        quality = Math.Clamp(quality, 0, ushort.MaxValue);
        if (quality == 0)
        {
            return copy.Remove(InstancePropertyKind.Quality) ? null : copy.Refusal;
        }

        return copy.SetScalar(InstancePropertyKind.Quality, (ulong)quality) ? null : copy.Refusal;
    }

    /// <summary>
    /// Primitive 13, the only one with no content parameters at all. It sets kind 128's state to 1 and its
    /// revealed mask to the OR of every REGISTERED <c>identificationMaskBit</c>, which is <c>0b1111</c> in
    /// v1 and is DERIVED from the registration rather than written down here.
    /// <para>
    /// <b>Spec 12.7 says "all ones" and this writes the registered bits instead.</b> All ones would write
    /// the 28 bits spec 12.7's own earlier sentence and spec 21 both reserve as unassigned and zero in v1, so
    /// a later engine release assigning bit 4 would find every item ever identified already claiming it. The
    /// two readings are behaviourally identical, because <c>ItemInstanceVisibility.CanSee</c> consults the
    /// mask only while <c>identified</c> is false, so this is the conservative one at no cost.
    /// </para>
    /// </summary>
    /// <param name="copy">The craft in progress.</param>
    /// <returns>The refusal, or null when the step applied.</returns>
    public static CraftRefusal? Identify(ref CraftWorkingCopy copy)
    {
        if (copy.IsRefused)
        {
            return copy.Refusal;
        }

        uint mask = 0;
        foreach (InstancePropertyRegistration registration in copy.Registry.ByKind)
        {
            if (registration.IsIdentificationGated)
            {
                mask |= 1u << registration.IdentificationMaskBit;
            }
        }

        return copy.SetIdentification(identified: true, mask) ? null : copy.Refusal;
    }

    /// <summary>
    /// Primitive 14. Writes one bit of kind 1, and refuses a bit above <see cref="MaxFlagBit"/> in v1,
    /// because spec 3.3 reserves bits 3 to 31 and holds them at 0. A bit nobody has assigned is not a flag a
    /// currency can set: it would be a durable byte with no meaning, written by a craft that read as though
    /// it had done something.
    /// </summary>
    /// <param name="copy">The craft in progress.</param>
    /// <param name="bit">The bit, 0 corrupted, 1 mirrored or 2 fractured.</param>
    /// <param name="value">Whether to set it or clear it.</param>
    /// <returns>The refusal, or null when the step applied.</returns>
    public static CraftRefusal? SetFlag(ref CraftWorkingCopy copy, int bit, bool value)
    {
        if (copy.IsRefused)
        {
            return copy.Refusal;
        }

        if (bit is < 0 or > MaxFlagBit)
        {
            return copy.Refuse(new CraftRefusal(CraftRefusalKind.FlagBitReserved, bit));
        }

        _ = copy.TryGetScalar(InstancePropertyKind.Flags, out ulong held);
        uint bits = (uint)held;
        bits = value ? bits | (1u << bit) : bits & ~(1u << bit);
        if (bits == 0)
        {
            return copy.Remove(InstancePropertyKind.Flags) ? null : copy.Refusal;
        }

        return copy.SetScalar(InstancePropertyKind.Flags, bits) ? null : copy.Refusal;
    }
}
