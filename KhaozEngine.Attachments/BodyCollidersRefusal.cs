namespace KhaozEngine.Attachments;

/// <summary>The body collider admission rule that refused an input.</summary>
public enum BodyCollidersRefusal
{
    /// <summary>Refuses a body collider JSON version other than 1.</summary>
    FormatVersionUnsupported,

    /// <summary>Refuses an unknown property in body collider JSON.</summary>
    JsonPropertyUnknown,

    /// <summary>Refuses a repeated property in body collider JSON.</summary>
    JsonPropertyDuplicate,

    /// <summary>Refuses a required property absent from body collider JSON.</summary>
    JsonPropertyMissing,

    /// <summary>Refuses a value of the wrong JSON shape.</summary>
    JsonValueShapeInvalid,

    /// <summary>Refuses malformed JSON or invalid UTF-8.</summary>
    JsonMalformed,

    /// <summary>Refuses a rig that is not a complete lowercase rig-v1 identity.</summary>
    RigIdentityInvalid,

    /// <summary>Refuses a missing, empty or blank joint name in a collider definition.</summary>
    JointNameEmpty,

    /// <summary>Refuses a joint name with malformed UTF-16.</summary>
    JointNameMalformedUtf16,

    /// <summary>Refuses a point with a nonfinite component.</summary>
    PointInvalid,

    /// <summary>Refuses a radius that is nonfinite or not positive.</summary>
    RadiusInvalid,

    /// <summary>Refuses more than <see cref="BodyColliderSet.MaxColliders"/> colliders.</summary>
    TooManyColliders,
}
