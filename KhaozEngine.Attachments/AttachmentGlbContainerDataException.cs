using System;

namespace KhaozEngine.Attachments;

/// <summary>A structured refusal of AttachmentGlbContainer input data.</summary>
public sealed class AttachmentGlbContainerDataException : MalformedDataException, IAttachmentDataException
{
    /// <summary>Create a refusal with a stable reason and developer-facing message.</summary>
    public AttachmentGlbContainerDataException(AttachmentGlbContainerRefusal reason, string message) : base(message)
    {
        Reason = reason;
    }

    /// <summary>Create a refusal preserving the original error.</summary>
    public AttachmentGlbContainerDataException(AttachmentGlbContainerRefusal reason, string message, Exception inner) : base(message, inner)
    {
        Reason = reason;
    }

    /// <summary>The stable reason the input was refused.</summary>
    public AttachmentGlbContainerRefusal Reason { get; }

    /// <summary>The caller-supplied source name added at the public GLB boundary.</summary>
    public string? SourceName { get; init; }

    /// <summary>The byte length involved in the refusal, when applicable.</summary>
    public int? ByteLength { get; init; }

    /// <summary>The declared GLB, chunk or embedded-buffer byte length, when available.</summary>
    public uint? DeclaredLength { get; init; }

    /// <summary>The version involved in the refusal, when applicable.</summary>
    public uint? Version { get; init; }

    /// <summary>The offset involved in the refusal, when applicable.</summary>
    public int? Offset { get; init; }

    /// <summary>The bytes remaining for the refused header or chunk payload, when applicable.</summary>
    public int? AvailableBytes { get; init; }

    /// <summary>The chunk type involved in the refusal, when applicable.</summary>
    public uint? ChunkType { get; init; }

    /// <summary>The chunk length involved in the refusal, when applicable.</summary>
    public uint? ChunkLength { get; init; }

    /// <summary>The asset version involved in the refusal, when applicable.</summary>
    public string? AssetVersion { get; init; }

    /// <summary>The BIN payload length, or -1 when the BIN chunk is missing, when applicable.</summary>
    public int? BinaryLength { get; init; }

    /// <summary>The buffer count involved in the refusal, when applicable.</summary>
    public int? BufferCount { get; init; }

    /// <summary>The property name involved in the refusal, when applicable.</summary>
    public string? PropertyName { get; init; }

    /// <summary>The entry index involved in the refusal, when applicable.</summary>
    public int? EntryIndex { get; init; }

    /// <summary>The resource kind involved in the refusal, when applicable.</summary>
    public string? ResourceKind { get; init; }

    /// <summary>The canonical JSON schema location of the refused value.</summary>
    public string? JsonPath { get; init; }

    MalformedDataException IAttachmentDataException.WithSource(string sourceName) => new AttachmentGlbContainerDataException(Reason, $"{sourceName}: {Message}", this)
    {
        SourceName = sourceName,
        ByteLength = ByteLength,
        DeclaredLength = DeclaredLength,
        Version = Version,
        Offset = Offset,
        AvailableBytes = AvailableBytes,
        ChunkType = ChunkType,
        ChunkLength = ChunkLength,
        AssetVersion = AssetVersion,
        BinaryLength = BinaryLength,
        BufferCount = BufferCount,
        PropertyName = PropertyName,
        EntryIndex = EntryIndex,
        ResourceKind = ResourceKind,
        JsonPath = JsonPath,
    };
}
