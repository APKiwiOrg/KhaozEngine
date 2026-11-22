using System;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

/// <summary>Opt-in views with their own rest positions for lifted skinned attachments.</summary>
public interface IAttachmentDisplacedViewScene : IAttachmentViewScene
{
    /// <summary>Create a view with one new rest position per source vertex, keeping the source's other attributes
    /// and materials. Release it through <see cref="IAttachmentViewScene.ReleaseSkinnedView"/>.</summary>
    SkinnedMeshHandle CreateSkinnedView(SkinnedMeshHandle source, ReadOnlySpan<uint> indices, ReadOnlySpan<Vector3> restPositions);
}
