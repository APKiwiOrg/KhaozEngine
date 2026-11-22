namespace KhaozEngine.Attachments;

/// <summary>The authored attachment-set admission rule that refused an input.</summary>
public enum AttachmentSetRefusal
{
    /// <summary>Refuses a set with no parts.</summary>
    PartsEmpty,

    /// <summary>Refuses a null part in the set.</summary>
    PartNull,

    /// <summary>Refuses a zero or repeated part id.</summary>
    PartIdInvalidOrDuplicate,

    /// <summary>Refuses an unsupported attachment-part runtime kind.</summary>
    PartKindUnsupported,

    /// <summary>Refuses an unsafe or malformed mesh asset key.</summary>
    MeshKeyInvalid,

    /// <summary>Refuses a mesh hash that is not lowercase SHA-256.</summary>
    MeshHashInvalid,

    /// <summary>Refuses a rigid part without a joint name.</summary>
    JointNameEmpty,

    /// <summary>Refuses a rigid joint name with malformed UTF-16.</summary>
    JointNameMalformedUtf16,

    /// <summary>Refuses a non-finite non-affine or singular local transform.</summary>
    LocalTransformInvalid,

    /// <summary>Refuses one mesh key declared with conflicting hashes.</summary>
    MeshHashConflict,

    /// <summary>Refuses a joint outside the provided ordinal reference set.</summary>
    JointReferenceUnresolved,

    /// <summary>Refuses a mesh absent from the reference closure or with a different hash.</summary>
    MeshReferenceMissingOrHashMismatch,

    /// <summary>Refuses an unsupported attachment JSON version.</summary>
    FormatVersionUnsupported,

    /// <summary>Refuses a parts member that is not an array.</summary>
    PartsNotArray,

    /// <summary>Refuses an unsupported authored attachment kind.</summary>
    JsonPartKindUnsupported,

    /// <summary>Refuses a part id outside the unsigned integer representation.</summary>
    JsonPartIdInvalid,

    /// <summary>Refuses a local transform with other than sixteen numeric entries.</summary>
    LocalTransformLengthMismatch,

    /// <summary>Refuses a non-finite or non-numeric local transform component.</summary>
    LocalTransformComponentInvalid,

    /// <summary>Refuses a schema value that is not an object.</summary>
    JsonObjectRequired,

    /// <summary>Refuses a repeated property in an attachment JSON object.</summary>
    JsonPropertyDuplicate,

    /// <summary>Refuses an unknown property at an attachment JSON schema location.</summary>
    JsonPropertyUnknown,

    /// <summary>Refuses a required property absent from attachment JSON.</summary>
    JsonPropertyMissing,

    /// <summary>Refuses a non-string attachment schema member.</summary>
    JsonStringRequired,

    /// <summary>Refuses malformed JSON syntax while retaining the caller source name.</summary>
    JsonMalformed,

    /// <summary>Refuses a JSON value shape rejected by a framework accessor, or a covers entry or region run of the wrong shape.</summary>
    JsonValueShapeInvalid,

    /// <summary>Refuses a region-tagged format without a layer, or region tags on a set that has no layer.</summary>
    LayerMissing,

    /// <summary>Refuses a layer that is not one of the defined layers.</summary>
    LayerUnknown,

    /// <summary>Refuses a covered region listed more than once.</summary>
    CoveredRegionDuplicate,

    /// <summary>Refuses a part without its region tag in a set with a layer.</summary>
    PartRegionMissing,

    /// <summary>Refuses a skinned part's region run of fewer than one triangle.</summary>
    PartRegionRunEmpty,

    /// <summary>Refuses an empty or blank region name.</summary>
    RegionNameEmpty,

    /// <summary>Refuses a region name with malformed UTF-16, which canonical JSON cannot write.</summary>
    RegionNameMalformedUtf16,

    /// <summary>Refuses a skinned part whose region runs add up to more than <see cref="int.MaxValue"/> triangles.</summary>
    RegionTriangleCountOverflow,

    /// <summary>Refuses an unsupported authored or programmatic motion kind.</summary>
    MotionKindUnsupported,

    /// <summary>Refuses chain motion without any chains.</summary>
    ChainsEmpty,

    /// <summary>Refuses a part with more than 32 chains.</summary>
    TooManyChains,

    /// <summary>Refuses a chain without any joints.</summary>
    ChainJointsEmpty,

    /// <summary>Refuses a chain with more than 16 joints.</summary>
    TooManyChainJoints,

    /// <summary>Refuses a blank or absent chain joint name.</summary>
    ChainJointNameEmpty,

    /// <summary>Refuses a chain joint name with malformed UTF-16.</summary>
    ChainJointNameMalformedUtf16,

    /// <summary>Refuses a joint named more than once in the chains of a part.</summary>
    ChainJointDuplicate,

    /// <summary>Refuses a chain tip without three finite components or with all components zero.</summary>
    ChainTipInvalid,

    /// <summary>Refuses a chain setting outside its allowed range.</summary>
    ChainSettingOutOfRange,

    /// <summary>Refuses a link that does not identify two distinct chains of the part.</summary>
    ChainLinkInvalid,

    /// <summary>Refuses a link that repeats a pair of chains in either direction.</summary>
    ChainLinkDuplicate,

    /// <summary>Refuses a part with more than 64 links.</summary>
    TooManyLinks,

    /// <summary>Refuses a blank or absent worn set collider joint name.</summary>
    ColliderJointNameEmpty,

    /// <summary>Refuses a worn set collider joint name with malformed UTF-16.</summary>
    ColliderJointNameMalformedUtf16,

    /// <summary>Refuses a worn set collider with a non-finite endpoint.</summary>
    ColliderPointInvalid,

    /// <summary>Refuses a worn set collider without a finite positive radius.</summary>
    ColliderRadiusInvalid,

    /// <summary>Refuses a worn set with more than eight colliders.</summary>
    TooManyColliders,

    /// <summary>Refuses motion or colliders in a set with no layer.</summary>
    MotionWithoutLayer,
}
