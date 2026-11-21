using System;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

/// <summary>Opt-in skinned mesh views for outfits. Ordinary attachment adapters need no new capability.</summary>
/// <remarks>A view draws part of a skinned mesh's triangles with the mesh's vertices and materials in every render
/// pass. The scene owns frame lifetime: a released view keeps drawing until the frame ends.</remarks>
public interface IAttachmentViewScene : IAttachmentScene
{
    /// <summary>Create a view drawing <paramref name="indices"/> over <paramref name="source"/>'s vertices.</summary>
    SkinnedMeshHandle CreateSkinnedView(SkinnedMeshHandle source, ReadOnlySpan<uint> indices);

    /// <summary>Release a view created by <see cref="CreateSkinnedView"/>. Each view is released once.</summary>
    void ReleaseSkinnedView(SkinnedMeshHandle view);
}
