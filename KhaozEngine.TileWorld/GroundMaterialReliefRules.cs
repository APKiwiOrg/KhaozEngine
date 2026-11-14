using System;
using System.Collections.Generic;

namespace KhaozEngine.TileWorld;

/// <summary>Semantic relief checks shared by catalog loading, validation and material set building.</summary>
public static class GroundMaterialReliefRules
{
    sealed record Finding(TileWorldRefusal Reason, string Field, float? Value, string Message);

    /// <summary>The first relief problem on a material, or null when its settings are sound.</summary>
    public static string? Problem(GroundMaterial material) => FindProblem(material)?.Message;

    static Finding? FindProblem(GroundMaterial material)
    {
        ArgumentNullException.ThrowIfNull(material);
        string at = $"material {material.Id} ('{material.Name}')";
        if (material.HeightBlendSharpness is { } sharpness)
        {
            if (string.IsNullOrWhiteSpace(material.HeightTexture))
                return new(TileWorldRefusal.GroundMaterialHeightSharpnessWithoutHeight,
                    "heightBlendSharpness", null, $"{at} sets heightBlendSharpness without a height texture.");
            if (!float.IsFinite(sharpness) || sharpness < GroundMaterialRelief.MinimumHeightBlendSharpness
                || sharpness > GroundMaterialRelief.MaximumHeightBlendSharpness)
                return new(TileWorldRefusal.GroundMaterialHeightSharpnessOutsideRange,
                    "heightBlendSharpness", sharpness, $"{at} heightBlendSharpness must be finite and inside [1, 64].");
        }
        if (material.Feather?.WidthMetres is { } width && (!float.IsFinite(width) || width <= 0f))
            return new(TileWorldRefusal.GroundMaterialFeatherWidthInvalid,
                "feather.widthMetres", width, $"{at} feather.widthMetres must be finite and positive.");
        if (material.Feather?.Breakup is { } breakup && (!float.IsFinite(breakup) || breakup < 0f || breakup > 1f))
            return new(TileWorldRefusal.GroundMaterialFeatherBreakupOutsideRange,
                "feather.breakup", breakup, $"{at} feather.breakup must be finite and inside [0, 1].");
        if (!Enum.IsDefined(material.AntiTiling))
            return new(TileWorldRefusal.GroundMaterialAntiTilingUndefined,
                "antiTiling", (int)material.AntiTiling, $"{at} antiTiling must name a defined value.");
        return null;
    }

    /// <summary>Refuses the first relief problem in ascending material id order. The exception carries Reason,
    /// SourceName, MaterialId, MaterialName, Field and the invalid Value when the rule has one.</summary>
    public static void ThrowIfInvalid(TileWorldCatalogs catalogs, string source)
    {
        ArgumentNullException.ThrowIfNull(catalogs);
        var ids = new List<ushort>(catalogs.Materials.Keys);
        ids.Sort();
        foreach (ushort id in ids)
        {
            GroundMaterial material = catalogs.Materials[id];
            if (FindProblem(material) is { } problem)
                throw new TileWorldException(problem.Reason, $"{source}: {problem.Message}")
                {
                    SourceName = source,
                    MaterialId = material.Id,
                    MaterialName = material.Name,
                    Field = problem.Field,
                    Value = problem.Value,
                };
        }
    }
}
