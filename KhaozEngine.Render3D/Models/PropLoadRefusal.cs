namespace KhaozEngine.Render3D;

/// <summary>The reason a PropLoad input was refused.</summary>
public enum PropLoadRefusal
{
    /// <summary>Refuses a prop whose source cannot be loaded.</summary>
    AssetLoadFailed,

    /// <summary>Refuses a prop height outside its validation band.</summary>
    DeclaredHeightOutsideRange,

    /// <summary>Refuses a degenerate prop with no measurable vertical extent.</summary>
    NoMeasurableHeight,

    /// <summary>Refuses an implausible raw-to-declared normalization scale.</summary>
    ScaleOutsideRange,

    /// <summary>Refuses normalization with no material parts.</summary>
    MaterialPartsEmpty,
}
