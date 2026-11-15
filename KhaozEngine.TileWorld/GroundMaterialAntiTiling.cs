namespace KhaozEngine.TileWorld;

/// <summary>Hex-tiling of a material's top-down taps to hide texture repetition.</summary>
public enum GroundMaterialAntiTiling
{
    /// <summary>Repeat the texture as authored.</summary>
    None,
    /// <summary>Offset copies while keeping orientation, for directional textures.</summary>
    Offset,
    /// <summary>Offset and rotate copies, for isotropic textures.</summary>
    Rotate,
}
