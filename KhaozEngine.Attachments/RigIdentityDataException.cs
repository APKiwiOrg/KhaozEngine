using System;

namespace KhaozEngine.Attachments;

/// <summary>A structured refusal of RigIdentity input data.</summary>
public sealed class RigIdentityDataException : MalformedDataException, IAttachmentDataException
{
    /// <summary>Create a refusal with a stable reason and developer-facing message.</summary>
    public RigIdentityDataException(RigIdentityRefusal reason, string message) : base(message)
    {
        Reason = reason;
    }

    /// <summary>Create a refusal preserving the original error.</summary>
    public RigIdentityDataException(RigIdentityRefusal reason, string message, Exception inner) : base(message, inner)
    {
        Reason = reason;
    }

    /// <summary>The stable reason the input was refused.</summary>
    public RigIdentityRefusal Reason { get; }

    /// <summary>The caller-supplied source name added at the public GLB boundary.</summary>
    public string? SourceName { get; init; }

    /// <summary>The buffer count involved in the refusal, when applicable.</summary>
    public int? BufferCount { get; init; }

    /// <summary>The skin count involved in the refusal, when applicable.</summary>
    public int? SkinCount { get; init; }

    /// <summary>The node index involved in the refusal, when applicable.</summary>
    public int? NodeIndex { get; init; }

    /// <summary>The skin index involved in the refusal, when applicable.</summary>
    public int? SkinIndex { get; init; }

    /// <summary>The canonical JSON schema location of the refused value.</summary>
    public string? JsonPath { get; init; }

    /// <summary>The element index involved in the refusal, when applicable.</summary>
    public int? ElementIndex { get; init; }

    /// <summary>The mesh index involved in the refusal, when applicable.</summary>
    public int? MeshIndex { get; init; }

    /// <summary>The primitive count involved in the refusal, when applicable.</summary>
    public int? PrimitiveCount { get; init; }

    /// <summary>The primitive index involved in the refusal, when applicable.</summary>
    public int? PrimitiveIndex { get; init; }

    /// <summary>The extension name involved in the refusal, when applicable.</summary>
    public string? ExtensionName { get; init; }

    /// <summary>The mode involved in the refusal, when applicable.</summary>
    public int? Mode { get; init; }

    /// <summary>The attribute name involved in the refusal, when applicable.</summary>
    public string? AttributeName { get; init; }

    /// <summary>The vertex index involved in the refusal, when applicable.</summary>
    public int? VertexIndex { get; init; }

    /// <summary>The index involved in the refusal, when applicable.</summary>
    public uint? Index { get; init; }

    /// <summary>The vertex count involved in the refusal, when applicable.</summary>
    public int? VertexCount { get; init; }

    /// <summary>The lane involved in the refusal, when applicable.</summary>
    public int? Lane { get; init; }

    /// <summary>The joint index involved in the refusal, when applicable.</summary>
    public float? JointIndex { get; init; }

    /// <summary>The weight involved in the refusal, when applicable.</summary>
    public float? Weight { get; init; }

    /// <summary>The joint count involved in the refusal, when applicable.</summary>
    public int? JointCount { get; init; }

    /// <summary>The actual count involved in the refusal, when applicable.</summary>
    public int? ActualCount { get; init; }

    /// <summary>The position count involved in the refusal, when applicable.</summary>
    public int? PositionCount { get; init; }

    /// <summary>The component index involved in the refusal, when applicable.</summary>
    public int? ComponentIndex { get; init; }

    /// <summary>The expected count involved in the refusal, when applicable.</summary>
    public int? ExpectedCount { get; init; }

    MalformedDataException IAttachmentDataException.WithSource(string sourceName) => new RigIdentityDataException(Reason, $"{sourceName}: {Message}", this)
    {
        SourceName = sourceName,
        BufferCount = BufferCount,
        SkinCount = SkinCount,
        NodeIndex = NodeIndex,
        SkinIndex = SkinIndex,
        JsonPath = JsonPath,
        ElementIndex = ElementIndex,
        MeshIndex = MeshIndex,
        PrimitiveCount = PrimitiveCount,
        PrimitiveIndex = PrimitiveIndex,
        ExtensionName = ExtensionName,
        Mode = Mode,
        AttributeName = AttributeName,
        VertexIndex = VertexIndex,
        Index = Index,
        VertexCount = VertexCount,
        Lane = Lane,
        JointIndex = JointIndex,
        Weight = Weight,
        JointCount = JointCount,
        ActualCount = ActualCount,
        PositionCount = PositionCount,
        ComponentIndex = ComponentIndex,
        ExpectedCount = ExpectedCount,
    };
}
