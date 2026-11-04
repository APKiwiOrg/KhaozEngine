using System;

namespace KhaozEngine.Attachments;

/// <summary>A structured refusal of AttachmentSkinnedGeometry input data.</summary>
public sealed class AttachmentSkinnedGeometryDataException : MalformedDataException, IAttachmentDataException
{
    /// <summary>Create a refusal with a stable reason and developer-facing message.</summary>
    public AttachmentSkinnedGeometryDataException(AttachmentSkinnedGeometryRefusal reason, string message) : base(message)
    {
        Reason = reason;
    }

    /// <summary>Create a refusal preserving the original error.</summary>
    public AttachmentSkinnedGeometryDataException(AttachmentSkinnedGeometryRefusal reason, string message, Exception inner) : base(message, inner)
    {
        Reason = reason;
    }

    /// <summary>The stable reason the input was refused.</summary>
    public AttachmentSkinnedGeometryRefusal Reason { get; }

    /// <summary>The caller-supplied source name added at the public GLB boundary.</summary>
    public string? SourceName { get; init; }

    /// <summary>The buffer count involved in the refusal, when applicable.</summary>
    public int? BufferCount { get; init; }

    /// <summary>The skin count involved in the refusal, when applicable.</summary>
    public int? SkinCount { get; init; }

    /// <summary>The canonical JSON schema location of the refused value.</summary>
    public string? JsonPath { get; init; }

    /// <summary>The element index involved in the refusal, when applicable.</summary>
    public int? ElementIndex { get; init; }

    /// <summary>The node index involved in the refusal, when applicable.</summary>
    public int? NodeIndex { get; init; }

    /// <summary>The skin index involved in the refusal, when applicable.</summary>
    public int? SkinIndex { get; init; }

    /// <summary>The joint count involved in the refusal, when applicable.</summary>
    public int? JointCount { get; init; }

    /// <summary>The maximum joint count involved in the refusal, when applicable.</summary>
    public int? MaximumJointCount { get; init; }

    /// <summary>The joint ordinal involved in the refusal, when applicable.</summary>
    public int? JointOrdinal { get; init; }

    /// <summary>The joint name involved in the refusal, when applicable.</summary>
    public string? JointName { get; init; }

    /// <summary>The bind count involved in the refusal, when applicable.</summary>
    public int? BindCount { get; init; }

    /// <summary>The component index involved in the refusal, when applicable.</summary>
    public int? ComponentIndex { get; init; }

    /// <summary>The attribute name involved in the refusal, when applicable.</summary>
    public string? AttributeName { get; init; }

    /// <summary>The position count involved in the refusal, when applicable.</summary>
    public int? PositionCount { get; init; }

    /// <summary>The weight count involved in the refusal, when applicable.</summary>
    public int? WeightCount { get; init; }

    /// <summary>The vertex index involved in the refusal, when applicable.</summary>
    public int? VertexIndex { get; init; }

    /// <summary>The lane involved in the refusal, when applicable.</summary>
    public int? Lane { get; init; }

    /// <summary>The index involved in the refusal, when applicable.</summary>
    public float? Index { get; init; }

    /// <summary>The weight involved in the refusal, when applicable.</summary>
    public float? Weight { get; init; }

    MalformedDataException IAttachmentDataException.WithSource(string sourceName) => new AttachmentSkinnedGeometryDataException(Reason, $"{sourceName}: {Message}", this)
    {
        SourceName = sourceName,
        BufferCount = BufferCount,
        SkinCount = SkinCount,
        JsonPath = JsonPath,
        ElementIndex = ElementIndex,
        NodeIndex = NodeIndex,
        SkinIndex = SkinIndex,
        JointCount = JointCount,
        MaximumJointCount = MaximumJointCount,
        JointOrdinal = JointOrdinal,
        JointName = JointName,
        BindCount = BindCount,
        ComponentIndex = ComponentIndex,
        AttributeName = AttributeName,
        PositionCount = PositionCount,
        WeightCount = WeightCount,
        VertexIndex = VertexIndex,
        Lane = Lane,
        Index = Index,
        Weight = Weight,
    };
}
