using System;

namespace KhaozEngine.Attachments.Render3D;

/// <summary>A structured refusal of AttachmentBinding input or access.</summary>
public sealed class AttachmentBindingArgumentException : ArgumentException
{
    /// <summary>Create a refusal with a stable reason and developer-facing message.</summary>
    public AttachmentBindingArgumentException(AttachmentBindingRefusal reason, string message, string? paramName)
        : base(message, paramName)
    {
        Reason = reason;
    }

    /// <summary>Create a refusal preserving the original error.</summary>
    public AttachmentBindingArgumentException(AttachmentBindingRefusal reason, string message, string? paramName, Exception inner)
        : base(message, paramName, inner)
    {
        Reason = reason;
    }

    /// <summary>The stable reason the input or access was refused.</summary>
    public AttachmentBindingRefusal Reason { get; }

    /// <summary>The attachment part involved in the refusal.</summary>
    public uint? PartId { get; init; }

    /// <summary>The supplied output capacity.</summary>
    public int? ActualCapacity { get; init; }

    /// <summary>The output capacity required by the binding or submission.</summary>
    public int? RequiredCapacity { get; init; }

    /// <summary>The joint involved in the refusal.</summary>
    public string? JointName { get; init; }

    /// <summary>The supplied host palette length.</summary>
    public int? ActualCount { get; init; }

    /// <summary>The required host palette length.</summary>
    public int? RequiredCount { get; init; }

    /// <summary>The skin-bone index involved in the refusal.</summary>
    public int? BoneIndex { get; init; }
}
