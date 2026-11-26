namespace KhaozEngine.Attachments.Render3D;

/// <summary>The stable reasons AttachmentSubmission input or access is refused.</summary>
public enum AttachmentSubmissionRefusal
{
    /// <summary>Refuses output shorter than the attachment draw count.</summary>
    OutputTooSmall,

    /// <summary>Refuses rigid descriptors for a set containing skinned parts.</summary>
    MixedDrawRequired,

    /// <summary>Refuses motion created by a different bound set or outfit.</summary>
    MotionMismatch,

    /// <summary>Refuses the second occurrence of a motion in a batch and identifies its entry index.</summary>
    DuplicateMotion,

    /// <summary>Refuses the second claim of a reservation in one batch.</summary>
    DuplicateClothSlot,

    /// <summary>The selected part is absent or has no cloth definition.</summary>
    ClothPartUnavailable,
}
