using System;

namespace KhaozEngine.Attachments;

/// <summary>A structured refusal of AttachmentHierarchy input data.</summary>
public sealed class AttachmentHierarchyDataException : MalformedDataException, IAttachmentDataException
{
    /// <summary>Create a refusal with a stable reason and developer-facing message.</summary>
    public AttachmentHierarchyDataException(AttachmentHierarchyRefusal reason, string message) : base(message)
    {
        Reason = reason;
    }

    /// <summary>Create a refusal preserving the original error.</summary>
    public AttachmentHierarchyDataException(AttachmentHierarchyRefusal reason, string message, Exception inner) : base(message, inner)
    {
        Reason = reason;
    }

    /// <summary>The stable reason the input was refused.</summary>
    public AttachmentHierarchyRefusal Reason { get; }

    /// <summary>The caller-supplied source name added at the public GLB boundary.</summary>
    public string? SourceName { get; init; }

    /// <summary>The canonical JSON schema location of the refused value.</summary>
    public string? JsonPath { get; init; }

    /// <summary>The node index involved in the refusal, when applicable.</summary>
    public int? NodeIndex { get; init; }

    /// <summary>The child index involved in the refusal, when applicable.</summary>
    public int? ChildIndex { get; init; }

    /// <summary>The node count involved in the refusal, when applicable.</summary>
    public int? NodeCount { get; init; }

    /// <summary>The parent whose child reference was refused, when applicable.</summary>
    public int? ParentIndex { get; init; }

    /// <summary>The expected root index involved in the refusal, when applicable.</summary>
    public int? ExpectedRootIndex { get; init; }

    /// <summary>The actual root index involved in the refusal, when applicable.</summary>
    public int? ActualRootIndex { get; init; }

    MalformedDataException IAttachmentDataException.WithSource(string sourceName) => new AttachmentHierarchyDataException(Reason, $"{sourceName}: {Message}", this)
    {
        SourceName = sourceName,
        JsonPath = JsonPath,
        NodeIndex = NodeIndex,
        ChildIndex = ChildIndex,
        NodeCount = NodeCount,
        ParentIndex = ParentIndex,
        ExpectedRootIndex = ExpectedRootIndex,
        ActualRootIndex = ActualRootIndex,
    };
}
