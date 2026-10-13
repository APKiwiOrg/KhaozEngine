using System;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Movement;

internal static partial class NavBakeIdentity
{
    // Runtime compatibility follows numeric equality. File identity comparisons preserve every stored bit.
    internal static string? FirstRuntimeDifference(in MoveTuning bake, in MoveTuning want)
    {
        foreach (TuningField field in TuningFields)
        {
            uint stored = field.Bits(bake), expected = field.Bits(want);
            bool equal = field.IsBool ? stored == expected :
                BitConverter.UInt32BitsToSingle(stored) == BitConverter.UInt32BitsToSingle(expected);
            if (equal) continue;
            return field.IsBool
                ? $"field {field.Name}: bake {stored == 1u}, expected {expected == 1u}"
                : $"field {field.Name}: bake {FormatFloat(stored)}, expected {FormatFloat(expected)}";
        }
        if (bake.Controller != want.Controller)
            return $"field Controller: bake {bake.Controller}, expected {want.Controller}";
        if (bake.FootRadiusFraction != want.FootRadiusFraction)
            return $"field FootRadiusFraction: bake {FormatFloat(Bits(bake.FootRadiusFraction))}, " +
                $"expected {FormatFloat(Bits(want.FootRadiusFraction))}";
        if (bake.Water.Mode != want.Water.Mode)
            return $"field Water.Mode: bake {bake.Water.Mode}, expected {want.Water.Mode}";
        if (bake.Water.SurfaceJumpSpeed != want.Water.SurfaceJumpSpeed)
            return $"field Water.SurfaceJumpSpeed: bake {FormatFloat(Bits(bake.Water.SurfaceJumpSpeed))}, " +
                $"expected {FormatFloat(Bits(want.Water.SurfaceJumpSpeed))}";
        return null;
    }
}
