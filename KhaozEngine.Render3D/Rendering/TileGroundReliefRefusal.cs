namespace KhaozEngine.Render3D;

/// <summary>The stable reasons a tile-ground layer's relief members are refused.</summary>
public enum TileGroundReliefRefusal
{
    /// <summary>Refuses a layer that flags a normal or roughness map but carries no surface texels.</summary>
    MapFlagWithoutSurface,

    /// <summary>Refuses a layer whose surface texels neither map flag claims.</summary>
    SurfaceWithoutMapFlag,

    /// <summary>Refuses a height-mapped layer whose blend sharpness is outside the supported range or not a number.</summary>
    HeightBlendSharpnessOutsideRange,

    /// <summary>Refuses a feather breakup outside [0, 1] or not a number.</summary>
    FeatherBreakupOutsideRange,

    /// <summary>Refuses an anti-tiling value the enum does not define.</summary>
    AntiTilingUndefined,
}
