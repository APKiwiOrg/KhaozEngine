using System;

namespace KhaozEngine.Render3D;

/// <summary>A structured refusal of MeshIndex inputs.</summary>
public sealed class MeshIndexStateException : InvalidOperationException
{
    /// <summary>Create a refusal with a stable reason and developer-facing message.</summary>
    public MeshIndexStateException(MeshIndexRefusal reason, string message)
        : base(message)
    {
        Reason = reason;
    }

    /// <summary>Create a refusal preserving the original error.</summary>
    public MeshIndexStateException(MeshIndexRefusal reason, string message, Exception inner)
        : base(message, inner)
    {
        Reason = reason;
    }

    /// <summary>The stable reason the input was refused.</summary>
    public MeshIndexRefusal Reason { get; }

    /// <summary>The raw asset path or caller-supplied asset identity.</summary>
    public string? Identity { get; init; }

    /// <summary>The source index that was refused.</summary>
    public long? Index { get; init; }

    /// <summary>The number of vertices in the source primitive.</summary>
    public int? VertexCount { get; init; }

    /// <summary>The vertex offset used by a refused rebase.</summary>
    public long? BaseIndex { get; init; }

    /// <summary>The manifest entry id, when loaded through a prop entry.</summary>
    public string? EntryId { get; init; }

    /// <summary>The material part index, when a part is known.</summary>
    public int? PartIndex { get; init; }
}
