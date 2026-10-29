using System;
using KhaozEngine.Locomotion;

namespace KhaozEngine.Movement;

internal static partial class NavBakeIdentity
{
    /// <summary>Format 2 plus 13 identity bytes per profile and ordered Stair-then-Drop acceptance bits.</summary>
    internal const ushort DropFormatVersion = 3;
    private const int DropIdentityBytes = 13;

    internal static bool HasContactIdentity(ushort version) => version is ContactFormatVersion or DropFormatVersion;

    private static void WriteDropIdentity(NavBakeWriter writer, NavBakeProfile profile)
    {
        float allowance = profile.MaxDropMetres;
        writer.WriteSingle(allowance);
        writer.WriteSingle(allowance > 0 ? profile.Tuning.WalkSpeed : 1);
        writer.WriteSingle(allowance > 0 ? profile.Tuning.RunSpeed : 1);
        writer.WriteUInt8(allowance > 0 && profile.Tuning.AirMomentum ? (byte)1 : (byte)0);
    }

    private static bool ReadDropIdentity(ref NavBakeReader reader, bool aquatic, ref MoveTuning tuning, out float allowance)
    {
        allowance = 0;
        if (!reader.TryReadSingle(out allowance) || !reader.TryReadSingle(out float walk) ||
            !reader.TryReadSingle(out float run) || !reader.TryReadUInt8(out byte momentum) || momentum > 1 ||
            !float.IsFinite(allowance) || allowance < 0 || !float.IsFinite(walk) || walk < 0 ||
            !float.IsFinite(run) || run < 0) return false;
        if (allowance == 0)
            return Bits(allowance) == 0 && Bits(walk) == Bits(1) && Bits(run) == Bits(1) && momentum == 0;
        if (aquatic || tuning.Controller != MovementControllerKind.Contact) return false;
        tuning = tuning with { WalkSpeed = walk, RunSpeed = run, AirMomentum = momentum == 1 };
        return true;
    }

    private static string? FirstDropDifference(DecodedProfile bake, DecodedProfile want)
    {
        if (Bits(bake.MaxDropMetres) != Bits(want.MaxDropMetres))
            return $"field MaxDropMetres: bake {FormatFloat(Bits(bake.MaxDropMetres))}, expected {FormatFloat(Bits(want.MaxDropMetres))}";
        if (bake.MaxDropMetres == 0) return null;
        if (Bits(bake.Tuning.WalkSpeed) != Bits(want.Tuning.WalkSpeed))
            return $"field WalkSpeed: bake {FormatFloat(Bits(bake.Tuning.WalkSpeed))}, expected {FormatFloat(Bits(want.Tuning.WalkSpeed))}";
        if (Bits(bake.Tuning.RunSpeed) != Bits(want.Tuning.RunSpeed))
            return $"field RunSpeed: bake {FormatFloat(Bits(bake.Tuning.RunSpeed))}, expected {FormatFloat(Bits(want.Tuning.RunSpeed))}";
        return bake.Tuning.AirMomentum != want.Tuning.AirMomentum
            ? $"field AirMomentum: bake {bake.Tuning.AirMomentum}, expected {want.Tuning.AirMomentum}" : null;
    }

    internal static string? FirstDropRuntimeDifference(in MoveTuning bake, in MoveTuning want)
    {
        if (bake.WalkSpeed != want.WalkSpeed) return "WalkSpeed must equal the baked Drop profile's";
        if (bake.RunSpeed != want.RunSpeed) return "RunSpeed must equal the baked Drop profile's";
        if (bake.AirMomentum != want.AirMomentum) return "AirMomentum must equal the baked Drop profile's";
        return null;
    }
}
