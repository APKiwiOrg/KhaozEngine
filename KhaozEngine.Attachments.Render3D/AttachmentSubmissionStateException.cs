using System;

namespace KhaozEngine.Attachments.Render3D;

/// <summary>A structured refusal of AttachmentSubmission input or access.</summary>
public sealed class AttachmentSubmissionStateException : InvalidOperationException
{
    /// <summary>Create a refusal with a stable reason and developer-facing message.</summary>
    public AttachmentSubmissionStateException(AttachmentSubmissionRefusal reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    /// <summary>Create a refusal preserving the original error.</summary>
    public AttachmentSubmissionStateException(AttachmentSubmissionRefusal reason, string message, Exception inner)
        : base(message, inner)
    {
        Reason = reason;
    }

    /// <summary>The stable reason the input or access was refused.</summary>
    public AttachmentSubmissionRefusal Reason { get; }

    /// <summary>The number of skinned attachment parts requiring mixed draws.</summary>
    public int SkinnedPartCount { get; init; }
}
