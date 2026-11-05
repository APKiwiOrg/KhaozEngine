namespace KhaozEngine.Render3D;

/// <summary>The stable reasons TileGroundUpload input is refused.</summary>
public enum TileGroundUploadRefusal
{
    /// <summary>Refuses a tile-ground upload or params pack with an unsupported layer count.</summary>
    LayerCountOutsideRange,

    /// <summary>Refuses an upload slice with the wrong RGBA byte length.</summary>
    LayerByteLengthMismatch,
}
