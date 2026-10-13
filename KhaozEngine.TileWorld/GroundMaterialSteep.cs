using System;
using System.Collections.Generic;
using System.Globalization;

namespace KhaozEngine.TileWorld;

/// <summary>
/// A ground material's steep companion: the material its texture blends toward as the ground under it steepens.
/// Authored as the optional <c>steep</c> object of a catalog material row,
/// <c>{ "material": 14, "fromDegrees": 40, "toDegrees": 55 }</c>.
/// <para>The rule belongs to the MATERIAL, not to a tile or a corner. Wherever the tile ground shader takes a tap
/// of a material that carries one, the tap becomes a blend of the material and its companion, weighted by a
/// smoothstep over this band on the slope of the interpolated ground normal. Below <see cref="ResolvedFromDegrees"/>
/// the material renders alone, past <see cref="ResolvedToDegrees"/> the companion does. So a grass foot whose
/// corners voted Grass up a cliff face still renders rock on the face itself.</para>
/// <para>The companion's own <c>steep</c> is never followed: one hop, no chains. The catalog loader refuses an
/// unknown companion, a self reference, a Water companion, a band outside (0, 90) degrees, a band whose start is
/// not below its end and a band too narrow for its two ends to differ in cosine, each by name. See
/// <see cref="GroundMaterialSteepRules"/>.</para>
/// </summary>
public sealed class GroundMaterialSteep
{
    /// <summary>Slope in degrees where the companion starts to show when <see cref="FromDegrees"/> is omitted. 40
    /// degrees sits past the steepest ground a walkable slope normally reaches, so ordinary hills keep their own
    /// material.</summary>
    public const float DefaultFromDegrees = 40f;

    /// <summary>Slope in degrees where the companion has fully replaced the material when
    /// <see cref="ToDegrees"/> is omitted. 15 degrees past the start gives a soft transition across the shoulder
    /// of a cliff rather than a line.</summary>
    public const float DefaultToDegrees = 55f;

    /// <summary>The companion material's catalog id. Required in the catalog row. It must name another material
    /// of the same catalog file that is not <see cref="GroundMaterialKind.Water"/>.</summary>
    public ushort Material { get; set; }

    /// <summary>Slope in degrees where the blend starts, null for <see cref="DefaultFromDegrees"/>. Strictly
    /// inside (0, 90).</summary>
    public float? FromDegrees { get; set; }

    /// <summary>Slope in degrees where the blend completes, null for <see cref="DefaultToDegrees"/>. Strictly
    /// inside (0, 90) and above the resolved start.</summary>
    public float? ToDegrees { get; set; }

    /// <summary>The start of the band with the default applied.</summary>
    public float ResolvedFromDegrees => FromDegrees ?? DefaultFromDegrees;

    /// <summary>The end of the band with the default applied.</summary>
    public float ResolvedToDegrees => ToDegrees ?? DefaultToDegrees;

    /// <summary>A detached copy, for the build snapshots that must not see later edits of the source.</summary>
    public GroundMaterialSteep Clone() => new() { Material = Material, FromDegrees = FromDegrees, ToDegrees = ToDegrees };

    internal static string Degrees(float value) => value.ToString("R", CultureInfo.InvariantCulture);
}

/// <summary>The checks a <see cref="GroundMaterialSteep"/> must pass against the catalog it was declared in. The
/// catalog loader throws on the first problem, the world validator reports every one as <c>material.steep</c>, and
/// the ground material set builder refuses a set it could not slot.</summary>
public static class GroundMaterialSteepRules
{
    /// <summary>What is wrong with <paramref name="material"/>'s steep companion, or null when it has none or it
    /// is sound. <paramref name="catalogs"/> is the catalog the companion must resolve in.</summary>
    public static string? Problem(GroundMaterial material, TileWorldCatalogs catalogs)
    {
        ArgumentNullException.ThrowIfNull(material);
        ArgumentNullException.ThrowIfNull(catalogs);
        if (material.Steep is not { } steep) return null;

        string at = $"material {material.Id} ('{material.Name}') steep";
        if (steep.Material == material.Id)
            return $"{at} names the material itself. A companion has to be another material.";
        GroundMaterial? companion = catalogs.Material(steep.Material);
        if (companion is null)
            return $"{at} names material {steep.Material}, which is not in the same catalog.";
        if (companion.Kind == GroundMaterialKind.Water)
            return $"{at} names material {steep.Material} ('{companion.Name}'), which is Water. A steep companion must be ground.";

        float from = steep.ResolvedFromDegrees;
        float to = steep.ResolvedToDegrees;
        if (!InsideRightAngle(from))
            return $"{at} fromDegrees {GroundMaterialSteep.Degrees(from)} is outside (0, 90).";
        if (!InsideRightAngle(to))
            return $"{at} toDegrees {GroundMaterialSteep.Degrees(to)} is outside (0, 90).";
        if (!(from < to))
            return $"{at} fromDegrees {GroundMaterialSteep.Degrees(from)}{DefaultNote(steep.FromDegrees)} is not below " +
                $"toDegrees {GroundMaterialSteep.Degrees(to)}{DefaultNote(steep.ToDegrees)}.";
        // The renderer blends on the cosines of the two ends, so a band they cannot tell apart has no blend at all.
        // The expression is the renderer's own (TileGroundSteepShading in Render3D), in plain float maths so this
        // package takes no render dependency.
        if (!(CosOf(to) < CosOf(from)))
            return $"{at} band {GroundMaterialSteep.Degrees(from)} to {GroundMaterialSteep.Degrees(to)} degrees is too " +
                "narrow to render: both ends have the same cosine in single precision. Widen the band.";
        return null;
    }

    /// <summary>Throws on the first material in <paramref name="catalogs"/> whose companion is unsound, naming
    /// <paramref name="source"/>. Materials are checked in ascending id order, so the error is stable.</summary>
    public static void ThrowIfInvalid(TileWorldCatalogs catalogs, string source)
    {
        ArgumentNullException.ThrowIfNull(catalogs);
        var ids = new List<ushort>(catalogs.Materials.Keys);
        ids.Sort();
        foreach (ushort id in ids)
            if (Problem(catalogs.Materials[id], catalogs) is { } problem)
                throw new TileWorldException($"{source}: {problem}");
    }

    // Strictly inside, and false for NaN, which no comparison passes.
    static float CosOf(float degrees) => MathF.Cos(degrees * (MathF.PI / 180f));

    static bool InsideRightAngle(float degrees) => degrees > 0f && degrees < 90f;

    static string DefaultNote(float? authored) => authored is null ? " (the default)" : "";
}
