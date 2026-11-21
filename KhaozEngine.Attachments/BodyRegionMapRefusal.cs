namespace KhaozEngine.Attachments;

/// <summary>The body region map admission rule that refused an input.</summary>
public enum BodyRegionMapRefusal
{
    /// <summary>Refuses a region map JSON version other than 1.</summary>
    FormatVersionUnsupported,

    /// <summary>Refuses an unknown property at a region map JSON location.</summary>
    JsonPropertyUnknown,

    /// <summary>Refuses a repeated property in a region map JSON object.</summary>
    JsonPropertyDuplicate,

    /// <summary>Refuses a rig that is not a complete lowercase rig-v1 identity.</summary>
    RigIdentityInvalid,

    /// <summary>Refuses an empty or blank region name.</summary>
    RegionNameEmpty,

    /// <summary>Refuses a region name already used by an earlier region.</summary>
    RegionNameDuplicate,

    /// <summary>Refuses more than <see cref="RegionSet.MaxRegions"/> regions.</summary>
    TooManyRegions,

    /// <summary>Refuses a run naming a region the map does not declare.</summary>
    RegionIndexOutOfRange,

    /// <summary>Refuses a run of fewer than one triangle.</summary>
    RunEmpty,

    /// <summary>Refuses a part whose runs do not add up to its declared triangle count.</summary>
    TriangleCountMismatch,

    /// <summary>Refuses a region name with malformed UTF-16, which canonical JSON cannot write.</summary>
    RegionNameMalformedUtf16,

    /// <summary>Refuses a required property absent from region map JSON.</summary>
    JsonPropertyMissing,

    /// <summary>Refuses a value of the wrong JSON shape, such as a run that is not a pair of integers.</summary>
    JsonValueShapeInvalid,

    /// <summary>Refuses malformed JSON syntax.</summary>
    JsonMalformed,
}
