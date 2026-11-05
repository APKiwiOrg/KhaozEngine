namespace KhaozEngine.Render3D;

/// <summary>Stable reasons skeleton arrays, name lookup or compose buffers are refused.</summary>
public enum SkeletonRefusal
{
    /// <summary>Refuses skeleton node arrays with different lengths.</summary>
    NodeArrayLengthsMismatch,
    /// <summary>Refuses a name table that does not match the node count.</summary>
    NameCountMismatch,
    /// <summary>Refuses a required name absent from the skeleton.</summary>
    JointNameMissing,
    /// <summary>Refuses a pose buffer that does not cover every node.</summary>
    LocalPoseCountMismatch,
    /// <summary>Refuses a palette that does not cover every skin bone.</summary>
    BonePaletteCountMismatch,
    /// <summary>Refuses ambiguous name lookup on duplicated named nodes.</summary>
    DuplicateNodeName,
}
