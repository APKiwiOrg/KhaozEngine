using System;

namespace KhaozEngine.Attachments;

/// <summary>A structured refusal of AttachmentRigidGeometry input data.</summary>
public sealed class AttachmentRigidGeometryDataException : MalformedDataException, IAttachmentDataException
{
    /// <summary>Create a refusal with a stable reason and developer-facing message.</summary>
    public AttachmentRigidGeometryDataException(AttachmentRigidGeometryRefusal reason, string message) : base(message)
    {
        Reason = reason;
    }

    /// <summary>Create a refusal preserving the original error.</summary>
    public AttachmentRigidGeometryDataException(AttachmentRigidGeometryRefusal reason, string message, Exception inner) : base(message, inner)
    {
        Reason = reason;
    }

    /// <summary>The stable reason the input was refused.</summary>
    public AttachmentRigidGeometryRefusal Reason { get; }

    /// <summary>The caller-supplied source name added at the public GLB boundary.</summary>
    public string? SourceName { get; init; }

    /// <summary>The node index involved in the refusal, when applicable.</summary>
    public int? NodeIndex { get; init; }

    /// <summary>Whether the refused rigid node declares a skin.</summary>
    public bool? HasSkin { get; init; }

    /// <summary>Whether the refused rigid node declares morph weights.</summary>
    public bool? HasWeights { get; init; }

    /// <summary>The mesh index involved in the refusal, when applicable.</summary>
    public int? MeshIndex { get; init; }

    /// <summary>The primitive index involved in the refusal, when applicable.</summary>
    public int? PrimitiveIndex { get; init; }

    /// <summary>The attribute name involved in the refusal, when applicable.</summary>
    public string? AttributeName { get; init; }

    MalformedDataException IAttachmentDataException.WithSource(string sourceName) => new AttachmentRigidGeometryDataException(Reason, $"{sourceName}: {Message}", this)
    {
        SourceName = sourceName,
        NodeIndex = NodeIndex,
        HasSkin = HasSkin,
        HasWeights = HasWeights,
        MeshIndex = MeshIndex,
        PrimitiveIndex = PrimitiveIndex,
        AttributeName = AttributeName,
    };
}
