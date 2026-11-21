using System;

namespace KhaozEngine.Render3D;

/// <summary>A structured refusal of a skinned mesh view, or of unloading a source a live view still draws from.
/// Derives from <see cref="ArgumentException"/>, which skinned unload threw for a bad handle before.</summary>
public sealed class SkinnedMeshViewArgumentException : ArgumentException
{
    /// <summary>Create a refusal with a stable reason and developer-facing message.</summary>
    public SkinnedMeshViewArgumentException(SkinnedMeshViewRefusal reason, string message, string? paramName)
        : base(message, paramName)
    {
        Reason = reason;
    }

    /// <summary>The stable reason the view or the unload was refused.</summary>
    public SkinnedMeshViewRefusal Reason { get; }

    /// <summary>The source handle's slot index, set by every reason.</summary>
    public int SourceIndex { get; init; }

    /// <summary>The source handle's generation, set by every reason.</summary>
    public int SourceGeneration { get; init; }

    /// <summary>The refused index count, for <see cref="SkinnedMeshViewRefusal.IndexCountNotTriangles"/>.</summary>
    public int? IndexCount { get; init; }

    /// <summary>The position of the first index past the source's vertices, for
    /// <see cref="SkinnedMeshViewRefusal.IndexOutOfRange"/>.</summary>
    public int? IndexPosition { get; init; }

    /// <summary>The source's vertex count, for <see cref="SkinnedMeshViewRefusal.IndexOutOfRange"/>.</summary>
    public int? VertexCount { get; init; }

    /// <summary>How many views of the source still live, for <see cref="SkinnedMeshViewRefusal.SourceHasLiveViews"/>.
    /// </summary>
    public int? LiveViews { get; init; }
}
