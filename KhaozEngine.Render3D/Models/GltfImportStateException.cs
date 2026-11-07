using System;

namespace KhaozEngine.Render3D;

/// <summary>A structured refusal of GltfImport inputs.</summary>
public sealed class GltfImportStateException : InvalidOperationException
{
    /// <summary>Create a refusal with a stable reason and developer-facing message.</summary>
    public GltfImportStateException(GltfImportRefusal reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    /// <summary>Create a refusal preserving the original error.</summary>
    public GltfImportStateException(GltfImportRefusal reason, string message, Exception inner)
        : base(message, inner)
    {
        Reason = reason;
    }

    /// <summary>The stable reason the input was refused.</summary>
    public GltfImportRefusal Reason { get; }

    /// <summary>The raw asset path or caller-supplied asset identity.</summary>
    public string? Identity { get; init; }

    /// <summary>The number of joints in the refused skin.</summary>
    public int? JointCount { get; init; }

    /// <summary>The maximum admitted joint count.</summary>
    public int? MaximumJointCount { get; init; }

    /// <summary>The refused vertex influence indices.</summary>
    public System.Numerics.Vector4? BoneIndices { get; init; }

    /// <summary>The refused vertex index within its decoded part.</summary>
    public int? VertexIndex { get; init; }

    /// <summary>The decoded material part index containing the refused vertex.</summary>
    public int? PartIndex { get; init; }
}
