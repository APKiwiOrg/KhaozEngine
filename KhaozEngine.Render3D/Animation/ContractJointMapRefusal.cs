namespace KhaozEngine.Render3D;

/// <summary>The stable reasons contract joint map input or access is refused.</summary>
public enum ContractJointMapRefusal
{
    /// <summary>Refuses a skeleton exceeding the skinned-draw node limit.</summary>
    NodeLimitExceeded,

    /// <summary>Refuses ambiguous skeleton node names.</summary>
    DuplicateNodeName,

    /// <summary>Refuses a required contract joint absent from the skeleton.</summary>
    ContractJointMissing,

    /// <summary>Refuses a contract joint whose parent index is outside the skeleton.</summary>
    ParentIndexInvalid,

    /// <summary>Refuses a joint under a different parent than the contract declares.</summary>
    ParentNameMismatch,

    /// <summary>Refuses a non-root contract joint outside the skin.</summary>
    ContractJointOutsideSkin,

    /// <summary>Refuses an uninvertible parent base frame.</summary>
    ParentBaseFrameSingular,

    /// <summary>Refuses an uninvertible rigid base frame.</summary>
    BaseFrameSingular,

    /// <summary>Refuses a mesh using a different skeleton instance.</summary>
    MeshSkeletonMismatch,

    /// <summary>Refuses a frame lookup for a node outside the contract.</summary>
    NodeOutsideContract,

    /// <summary>Refuses a stance with a different name than requested.</summary>
    StanceNameMismatch,

    /// <summary>Refuses a stance track targeting no skeleton node.</summary>
    StanceNodeMissing,

    /// <summary>Refuses a stance track on a non-contract joint.</summary>
    StanceNodeOutsideContract,

    /// <summary>Refuses a stance scale channel.</summary>
    StanceScaleTrack,

    /// <summary>Refuses a stance channel with other than one key.</summary>
    StanceTrackKeyCountMismatch,

    /// <summary>Refuses a name lookup absent from the joint map.</summary>
    NodeNameMissing,

    /// <summary>Refuses a bone lookup for a named node outside the skin.</summary>
    NamedNodeOutsideSkin,
}
