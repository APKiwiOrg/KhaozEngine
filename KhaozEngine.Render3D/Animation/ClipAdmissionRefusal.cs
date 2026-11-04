namespace KhaozEngine.Render3D;

/// <summary>Stable reasons clip admission inputs are refused.</summary>
public enum ClipAdmissionRefusal
{
    /// <summary>Refuses taking a second clip of one name.</summary>
    DuplicateClipName,
    /// <summary>Refuses a required clip-policy joint absent from the skeleton.</summary>
    RequiredJointMissing,
}
