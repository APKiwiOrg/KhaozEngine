namespace KhaozEngine.Render3D;

/// <summary>The effective opaque ambient occlusion path chosen by the scene.</summary>
public enum AmbientOcclusionStatus
{
    Disabled,
    Active,
    RequiresHdr,
    RequiresSingleSample,
    InsufficientColorAttachments,
    ResourceUnavailable,
}
