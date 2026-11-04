using System;

namespace KhaozEngine.Attachments;

/// <summary>A structured refusal of AttachmentAccessor input data.</summary>
public sealed class AttachmentAccessorDataException : MalformedDataException, IAttachmentDataException
{
    /// <summary>Create a refusal with a stable reason and developer-facing message.</summary>
    public AttachmentAccessorDataException(AttachmentAccessorRefusal reason, string message) : base(message)
    {
        Reason = reason;
    }

    /// <summary>Create a refusal preserving the original error.</summary>
    public AttachmentAccessorDataException(AttachmentAccessorRefusal reason, string message, Exception inner) : base(message, inner)
    {
        Reason = reason;
    }

    /// <summary>The stable reason the input was refused.</summary>
    public AttachmentAccessorRefusal Reason { get; }

    /// <summary>The caller-supplied source name added at the public GLB boundary.</summary>
    public string? SourceName { get; init; }

    /// <summary>The canonical JSON schema location of the refused value.</summary>
    public string? JsonPath { get; init; }

    /// <summary>The accessor index involved in the refusal, when applicable.</summary>
    public int? AccessorIndex { get; init; }

    /// <summary>The expected type involved in the refusal, when applicable.</summary>
    public string? ExpectedType { get; init; }

    /// <summary>The actual type involved in the refusal, when applicable.</summary>
    public string? ActualType { get; init; }

    /// <summary>The component type involved in the refusal, when applicable.</summary>
    public int? ComponentType { get; init; }

    /// <summary>The normalized involved in the refusal, when applicable.</summary>
    public bool? Normalized { get; init; }

    /// <summary>The count involved in the refusal, when applicable.</summary>
    public int? Count { get; init; }

    /// <summary>The byte offset involved in the refusal, when applicable.</summary>
    public int? ByteOffset { get; init; }

    /// <summary>The sparse count involved in the refusal, when applicable.</summary>
    public int? SparseCount { get; init; }

    /// <summary>The accessor count involved in the refusal, when applicable.</summary>
    public int? AccessorCount { get; init; }

    /// <summary>The entry index involved in the refusal, when applicable.</summary>
    public int? EntryIndex { get; init; }

    /// <summary>The refused table reference or unsigned sparse index.</summary>
    public long? Index { get; init; }

    /// <summary>The previous index involved in the refusal, when applicable.</summary>
    public int? PreviousIndex { get; init; }

    /// <summary>The buffer view index involved in the refusal, when applicable.</summary>
    public int? BufferViewIndex { get; init; }

    /// <summary>The extension name involved in the refusal, when applicable.</summary>
    public string? ExtensionName { get; init; }

    /// <summary>The buffer index involved in the refusal, when applicable.</summary>
    public int? BufferIndex { get; init; }

    /// <summary>The byte length involved in the refusal, when applicable.</summary>
    public int? ByteLength { get; init; }

    /// <summary>The buffer length involved in the refusal, when applicable.</summary>
    public int? BufferLength { get; init; }

    /// <summary>The byte stride involved in the refusal, when applicable.</summary>
    public int? ByteStride { get; init; }

    /// <summary>The element size involved in the refusal, when applicable.</summary>
    public int? ElementSize { get; init; }

    /// <summary>The stride allowed involved in the refusal, when applicable.</summary>
    public bool? StrideAllowed { get; init; }

    /// <summary>The required bytes involved in the refusal, when applicable.</summary>
    public long? RequiredBytes { get; init; }

    /// <summary>The view length involved in the refusal, when applicable.</summary>
    public int? ViewLength { get; init; }

    /// <summary>The alignment involved in the refusal, when applicable.</summary>
    public int? Alignment { get; init; }

    /// <summary>The property name involved in the refusal, when applicable.</summary>
    public string? PropertyName { get; init; }

    /// <summary>The table name involved in the refusal, when applicable.</summary>
    public string? TableName { get; init; }

    /// <summary>The entry count involved in the refusal, when applicable.</summary>
    public int? EntryCount { get; init; }

    MalformedDataException IAttachmentDataException.WithSource(string sourceName) => new AttachmentAccessorDataException(Reason, $"{sourceName}: {Message}", this)
    {
        SourceName = sourceName,
        JsonPath = JsonPath,
        AccessorIndex = AccessorIndex,
        ExpectedType = ExpectedType,
        ActualType = ActualType,
        ComponentType = ComponentType,
        Normalized = Normalized,
        Count = Count,
        ByteOffset = ByteOffset,
        SparseCount = SparseCount,
        AccessorCount = AccessorCount,
        EntryIndex = EntryIndex,
        Index = Index,
        PreviousIndex = PreviousIndex,
        BufferViewIndex = BufferViewIndex,
        ExtensionName = ExtensionName,
        BufferIndex = BufferIndex,
        ByteLength = ByteLength,
        BufferLength = BufferLength,
        ByteStride = ByteStride,
        ElementSize = ElementSize,
        StrideAllowed = StrideAllowed,
        RequiredBytes = RequiredBytes,
        ViewLength = ViewLength,
        Alignment = Alignment,
        PropertyName = PropertyName,
        TableName = TableName,
        EntryCount = EntryCount,
    };
}
