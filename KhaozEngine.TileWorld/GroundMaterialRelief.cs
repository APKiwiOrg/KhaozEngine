namespace KhaozEngine.TileWorld;

/// <summary>Catalog height-blend bounds, matching Render3D's TileGroundRelief without a render dependency.</summary>
public static class GroundMaterialRelief
{
    /// <summary>The softest height blend, matching TileGroundRelief.MinimumHeightBlendSharpness.</summary>
    public const float MinimumHeightBlendSharpness = 1f;

    /// <summary>The crispest height blend, matching TileGroundRelief.MaximumHeightBlendSharpness.</summary>
    public const float MaximumHeightBlendSharpness = 64f;
}
