using System;

namespace KhaozEngine.Attachments.Render3D;

/// <summary>A structured refusal of AttachmentSubmission input or access.</summary>
public sealed class AttachmentSubmissionArgumentException : ArgumentException
{
    /// <summary>Create a refusal with a stable reason and developer-facing message.</summary>
    public AttachmentSubmissionArgumentException(AttachmentSubmissionRefusal reason, string message, string? paramName)
        : base(message, paramName)
    {
        Reason = reason;
    }

    /// <summary>Create a refusal preserving the original error.</summary>
    public AttachmentSubmissionArgumentException(AttachmentSubmissionRefusal reason, string message, string? paramName, Exception inner)
        : base(message, paramName, inner)
    {
        Reason = reason;
    }

    /// <summary>The stable reason the input or access was refused.</summary>
    public AttachmentSubmissionRefusal Reason { get; }

    /// <summary>The supplied output capacity.</summary>
    public int ActualCapacity { get; init; }

    /// <summary>The output capacity required by the binding or submission.</summary>
    public int RequiredCapacity { get; init; }

    /// <summary>The zero-based index of the repeated motion in a batch, when applicable.</summary>
    public int? EntryIndex { get; init; }

    public int? WornIndex { get; init; }
    public uint? PartId { get; init; }
}
