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

    /// <summary>Refuses a host mesh or worn set using a different skeleton instance from its binding.</summary>
    HostSkeletonMismatch,

    /// <summary>Refuses an invalid host joint model matrix before palette publication.</summary>
    HostJointPoseInvalid,

    /// <summary>Refuses output shorter than the garment joint count.</summary>
    GarmentPaletteOutputTooSmall,

    /// <summary>Refuses a body region map for another rig, another part count or other part triangle counts.</summary>
    BodyRegionMapMismatch,

    /// <summary>Refuses a skinned part whose region runs do not sum to its mesh's triangles.</summary>
    PartRegionTagsMismatch,

    /// <summary>Refuses a worn set naming a region the body's region map does not declare.</summary>
    OutfitRegionUnknown,

    /// <summary>Refuses an outfit over a scene that is not an <see cref="IAttachmentViewScene"/>.</summary>
    OutfitViewsUnsupported,

    /// <summary>Refuses a declared chain joint absent from the garment skin.</summary>
    ChainJointMissing,

    /// <summary>Refuses a chain joint that is also a host skin joint.</summary>
    ChainJointIsHostJoint,

    /// <summary>Refuses a chain joint whose parent is not the preceding chain joint.</summary>
    ChainNotParentLine,

    /// <summary>Refuses a first chain joint without a named host skin joint as its parent.</summary>
    ChainAnchorNotHostJoint,

    /// <summary>Refuses a chain joint whose inverse bind does not match its rest frame on the host anchor.</summary>
    ChainBindMismatch,

    /// <summary>Refuses body colliders for another rig identity or skeleton instance.</summary>
    BodyCollidersMismatch,

    /// <summary>Refuses a collider whose joint name does not resolve to a host skin joint.</summary>
    ColliderJointUnresolved,

    /// <summary>Refuses a wearer's combined body and set colliders above the simulation limit.</summary>
    WearerCollidersTooMany,

    /// <summary>Refuses collider binding without the host skin's inverse binds.</summary>
    ColliderHostMeshRequired,

    /// <summary>Refuses a worn part that needs a displaced view over a scene without
    /// <see cref="IAttachmentDisplacedViewScene"/>. Identifies the set's cache key, worn index and part ID.</summary>
    OutfitLiftUnsupported,

    /// <summary>A footprint for this body has a part with a different triangle count from its mesh.
    /// Identifies the set's cache key, worn index, part ID and both triangle counts.</summary>
    FootprintTriangleCountMismatch,
}
