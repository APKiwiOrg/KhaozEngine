namespace KhaozEngine.Attachments.Render3D;

/// <summary>The stable reasons AttachmentSubmission input or access is refused.</summary>
public enum AttachmentSubmissionRefusal
{
    /// <summary>Refuses output shorter than the attachment draw count.</summary>
    OutputTooSmall,

    /// <summary>Refuses rigid descriptors for a set containing skinned parts.</summary>
    MixedDrawRequired,
}
