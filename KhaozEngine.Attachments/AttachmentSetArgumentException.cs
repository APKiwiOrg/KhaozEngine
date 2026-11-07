using System;

namespace KhaozEngine.Attachments;

/// <summary>A structured refusal of a programmatic attachment-set definition.</summary>
public sealed class AttachmentSetArgumentException : ArgumentException, IAttachmentDataException
{
    /// <summary>Create a refusal retaining the original parameter name.</summary>
    public AttachmentSetArgumentException(AttachmentSetRefusal reason, string message, string? paramName)
        : base(message, paramName)
    {
        Reason = reason;
    }

    /// <summary>The stable reason the input was refused.</summary>
    public AttachmentSetRefusal Reason { get; }

    /// <summary>The number of parts in an empty set.</summary>
    public int? PartCount { get; init; }

    /// <summary>The zero-based input index of the refused part.</summary>
    public int? PartIndex { get; init; }

    /// <summary>The stable identifier of the refused part.</summary>
    public uint? PartId { get; init; }

    /// <summary>The unsupported runtime part type.</summary>
    public Type? ActualType { get; init; }

    /// <summary>The refused mesh asset key.</summary>
    public string? MeshKey { get; init; }

    /// <summary>The hash declared for the refused mesh.</summary>
    public string? DeclaredHash { get; init; }

    /// <summary>The previously declared hash for a conflicting mesh key.</summary>
    public string? ExistingHash { get; init; }

    /// <summary>The malformed rigid joint name.</summary>
    public string? JointName { get; init; }

    MalformedDataException IAttachmentDataException.WithSource(string sourceName) => new AttachmentSetDataException(Reason, $"{sourceName}: {Message}", this)
    {
        SourceName = sourceName,
        ParamName = ParamName,
        PartCount = PartCount,
        PartIndex = PartIndex,
        PartId = PartId,
        ActualType = ActualType,
        MeshKey = MeshKey,
        DeclaredHash = DeclaredHash,
        ExistingHash = ExistingHash,
        JointName = JointName,
    };
}
