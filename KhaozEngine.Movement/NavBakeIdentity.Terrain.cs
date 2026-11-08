using System;
using System.Collections.Generic;

namespace KhaozEngine.Movement;

internal static partial class NavBakeIdentity
{
    internal const ushort TerrainFormatVersion = 4;
    internal static ushort FormatFor(PhysicsNavBakeOptions options, IReadOnlyList<NavBakeProfile> profiles) =>
        options.CaptureTerrainDescentEvidence ? TerrainFormatVersion : FormatFor(profiles);
    internal static bool HasDropIdentity(ushort version) => version is DropFormatVersion or TerrainFormatVersion;
    private static void ValidateTerrainExpectation(PhysicsNavBakeOptions options, NavBakeProfile[] profiles)
    {
        foreach (var profile in profiles)
        {
            if (!profile.AllowTerrainDescents) continue;
            _ = profile.ResolveKind();
            if (!options.CaptureTerrainDescentEvidence)
                throw new ArgumentException("Terrain profiles require the capture evidence option.", nameof(options));
        }
    }
    private static bool ReadTerrainFlag(ref NavBakeReader reader, float allowance, out bool enabled)
    {
        enabled = false;
        if (!reader.TryReadUInt8(out byte flag) || flag > 1 || flag == 1 && allowance == 0) return false;
        enabled = flag == 1;
        return true;
    }
    private static string? FirstTerrainDifference(DecodedProfile bake, DecodedProfile want) =>
        bake.AllowTerrainDescents != want.AllowTerrainDescents
            ? $"field AllowTerrainDescents: bake {bake.AllowTerrainDescents}, expected {want.AllowTerrainDescents}" : null;
}
