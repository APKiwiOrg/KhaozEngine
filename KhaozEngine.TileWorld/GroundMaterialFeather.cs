namespace KhaozEngine.TileWorld;

/// <summary>Per-material feather settings for overlay tiles marked TileSettings.FeatherOverlay.</summary>
public sealed class GroundMaterialFeather
{
    /// <summary>Inward fade width in positive finite metres, overriding the mesher default for this material.
    /// Still capped at half the tile size. Null uses the mesher default.</summary>
    public float? WidthMetres { get; set; }

    /// <summary>Deterministic world-space noise that wanders the feathered edge inward, in [0, 1].
    /// Null means 0, a straight edge.</summary>
    public float? Breakup { get; set; }

    /// <summary>A detached copy for build snapshots.</summary>
    public GroundMaterialFeather Clone() => new() { WidthMetres = WidthMetres, Breakup = Breakup };
}
