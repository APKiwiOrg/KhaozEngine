namespace KhaozEngine.Attachments;

/// <summary>Stable reasons for refusing AttachmentSkinnedGeometry input data.</summary>
public enum AttachmentSkinnedGeometryRefusal
{
    /// <summary>Refuses skinned geometry without exactly one embedded buffer.</summary>
    EmbeddedBufferCountMismatch,

    /// <summary>Refuses skinned geometry without exactly one object skin.</summary>
    SkinCountMismatch,

    /// <summary>Refuses animation in a skinned attachment.</summary>
    AnimationPresent,

    /// <summary>Refuses node or mesh morph weights in a skinned attachment.</summary>
    MorphWeightsPresent,

    /// <summary>Refuses a mesh node not referencing skin zero.</summary>
    MeshNodeSkinMismatch,

    /// <summary>Refuses a skin joint table outside its allowed count.</summary>
    JointCountOutsideRange,

    /// <summary>Refuses repeated skin joint node indices.</summary>
    JointNodeDuplicate,

    /// <summary>Refuses an unnamed skin joint.</summary>
    JointNameEmpty,

    /// <summary>Refuses repeated ordinal joint names.</summary>
    JointNameDuplicate,

    /// <summary>Refuses a bind accessor whose count differs from the skin joint count.</summary>
    InverseBindCountMismatch,

    /// <summary>Refuses a non-finite inverse-bind matrix.</summary>
    InverseBindNonFinite,

    /// <summary>Refuses morph targets in a skinned primitive.</summary>
    MorphTargetsPresent,

    /// <summary>Refuses unsupported influence sets beyond JOINTS_0 and WEIGHTS_0.</summary>
    ExtraInfluenceAttribute,

    /// <summary>Refuses position joint and weight accessors of different lengths.</summary>
    VertexAttributeCountMismatch,

    /// <summary>Refuses a vertex joint index beyond the skin joint count.</summary>
    JointIndexOutsideSkin,

    /// <summary>Refuses a non-finite or negative skin influence weight.</summary>
    WeightInvalid,

    /// <summary>Refuses a vertex with no positive skin weight.</summary>
    WeightsAllZero,
}
