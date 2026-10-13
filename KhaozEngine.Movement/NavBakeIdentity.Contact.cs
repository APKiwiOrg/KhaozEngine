using System.Collections.Generic;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Movement;

internal static partial class NavBakeIdentity
{
    private const int ContactIdentityBytes = 14;

    internal static ushort FormatFor(IReadOnlyList<NavBakeProfile> profiles)
    {
        foreach (NavBakeProfile profile in profiles)
            if (profile.Tuning.Controller == MovementControllerKind.Contact) return ContactFormatVersion;
        return FormatVersion;
    }

    private static void WriteContactIdentity(NavBakeWriter writer, in MoveTuning tuning)
    {
        writer.WriteUInt8(tuning.Controller == MovementControllerKind.Contact ? (byte)1 : (byte)0);
        writer.WriteSingle(tuning.FootRadiusFraction);
        writer.WriteUInt8((byte)tuning.Water.Mode);
        writer.WriteSingle(tuning.Water.SurfaceJumpSpeed);
        writer.WriteSingle(0);
    }

    private static bool ReadContactIdentity(ref NavBakeReader reader, bool aquatic, ref MoveTuning tuning)
    {
        if (!reader.TryReadUInt8(out byte kind) || !reader.TryReadSingle(out float fraction) ||
            !reader.TryReadUInt8(out byte mode) || !reader.TryReadSingle(out float jump) ||
            !reader.TryReadSingle(out float offset) || Bits(offset) != 0) return false;
        if (kind == 0)
        {
            return Bits(fraction) == Bits(0.5f) && mode == 0 && Bits(jump) == 0;
        }
        if (kind != 1 || aquatic || mode is < 1 or > 3 || !float.IsFinite(fraction) ||
            fraction <= 0 || fraction > 1 || !float.IsFinite(jump) || jump < 0) return false;
        tuning = tuning with
        {
            Controller = MovementControllerKind.Contact,
            FootRadiusFraction = fraction,
            Water = new((WaterTraversalMode)mode, jump),
        };
        return true;
    }

    internal static string? FirstContactDifference(in MoveTuning bake, in MoveTuning want)
    {
        if (bake.Controller != want.Controller)
            return $"field Controller: bake {bake.Controller}, expected {want.Controller}";
        if (Bits(bake.FootRadiusFraction) != Bits(want.FootRadiusFraction))
            return $"field FootRadiusFraction: bake {FormatFloat(Bits(bake.FootRadiusFraction))}, " +
                $"expected {FormatFloat(Bits(want.FootRadiusFraction))}";
        if (bake.Water.Mode != want.Water.Mode)
            return $"field Water.Mode: bake {bake.Water.Mode}, expected {want.Water.Mode}";
        if (Bits(bake.Water.SurfaceJumpSpeed) != Bits(want.Water.SurfaceJumpSpeed))
            return $"field Water.SurfaceJumpSpeed: bake {FormatFloat(Bits(bake.Water.SurfaceJumpSpeed))}, " +
                $"expected {FormatFloat(Bits(want.Water.SurfaceJumpSpeed))}";
        return null;
    }
}
