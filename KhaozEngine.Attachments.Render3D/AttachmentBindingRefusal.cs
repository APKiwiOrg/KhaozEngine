namespace KhaozEngine.Attachments.Render3D;

/// <summary>The stable reasons AttachmentBinding input or access is refused.</summary>
public enum AttachmentBindingRefusal
{
    /// <summary>Refuses map-only binding for a set containing skinned parts.</summary>
    HostMeshRequired,

    /// <summary>Refuses a rigid part whose joint cannot bind to the contract skin.</summary>
    RigidJointUnresolved,

    /// <summary>Refuses a garment-palette request for a rigid part.</summary>
    PartHasNoGarmentPalette,

    /// <summary>Refuses output shorter than the attachment-part count.</summary>
    WorldOutputTooSmall,

    /// <summary>Refuses a non-finite non-affine or singular model transform.</summary>
    ModelTransformInvalid,

    /// <summary>Refuses an invalid rigid joint pose before publishing any output.</summary>
    JointPoseInvalid,

    /// <summary>Refuses an invalid composed rigid world transform.</summary>
    WorldTransformInvalid,

    /// <summary>Refuses rigid pose composition rejected by the socket validator.</summary>
    PoseCompositionFailed,

    /// <summary>Refuses a part id absent from this binding.</summary>
    PartMissing,

    /// <summary>Refuses frame data using a different skeleton instance.</summary>
    SkeletonMismatch,

    /// <summary>Refuses a host palette with the wrong bone count.</summary>
    HostPaletteLengthMismatch,

    /// <summary>Refuses garment bind and rest counts not matching its joint table.</summary>
    GarmentBindCountsMismatch,

    /// <summary>Refuses a garment joint that cannot bind to the host skin.</summary>
    GarmentJointUnresolved,

    /// <summary>Refuses non-finite garment binds or binds outside the host tolerance.</summary>
    GarmentBindMismatch,

    /// <summary>Refuses a host without one bind and rest pose per skin bone.</summary>
    HostBindCountsMismatch,

    /// <summary>Refuses a non-finite host inverse bind.</summary>
    HostInverseBindNonFinite,

    /// <summary>Refuses a garment without captured or supplied skin metadata.</summary>
    GarmentMetadataMissing,

    /// <summary>Refuses garment metadata without a skeleton.</summary>
    GarmentSkeletonMissing,

    /// <summary>Refuses a garment joint referring outside its skeleton.</summary>
    GarmentNodeMissing,

    /// <summary>Refuses a garment joint count outside the renderer capacity.</summary>
    GarmentJointCountOutsideRange,

    /// <summary>Refuses a host mesh using a different contract skeleton instance.</summary>
    HostSkeletonMismatch,

    /// <summary>Refuses an invalid host joint model matrix before palette publication.</summary>
    HostJointPoseInvalid,

    /// <summary>Refuses output shorter than the garment joint count.</summary>
    GarmentPaletteOutputTooSmall,
}
