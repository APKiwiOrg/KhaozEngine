namespace KhaozEngine.TileWorld;

/// <summary>The stable reasons TileGroundMaterialSet input is refused.</summary>
public enum TileGroundMaterialSetRefusal
{
    /// <summary>Refuses layers not matching material count plus the reserved layer.</summary>
    LayerCountMismatch,

    /// <summary>Refuses a hand-built set above the material layer limit.</summary>
    LayerLimitExceeded,

    /// <summary>Refuses a material layer with the wrong RGBA byte length.</summary>
    LayerByteLengthMismatch,

    /// <summary>Refuses a material id assigned to more than one render slot.</summary>
    DuplicateMaterialId,

    /// <summary>Refuses a steep companion on the reserved missing-material layer.</summary>
    MissingLayerHasCompanion,

    /// <summary>Refuses a steep companion referencing the reserved missing slot.</summary>
    CompanionIsMissingLayer,

    /// <summary>Refuses surface bytes with the wrong length. Carries LayerIndex, ActualByteLength,
    /// ExpectedByteLength and the surface Width and Height.</summary>
    SurfaceByteLengthMismatch,

    /// <summary>Refuses relief on the reserved magenta layer. Carries LayerIndex.</summary>
    MissingLayerHasRelief,
}
