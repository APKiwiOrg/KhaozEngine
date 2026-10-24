namespace KhaozEngine.TileWorld;

/// <summary>An available authored corner height in metres, with its owning region and resolved height plane.</summary>
public readonly record struct TileCornerHeightRead(float Height, RegionCoord Owner, int SourcePlane);
