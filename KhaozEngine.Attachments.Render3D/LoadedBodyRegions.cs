using System.Collections.Generic;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

/// <summary>A verified body region map checked against one uploaded body. Borrows its cache.</summary>
/// <remarks>Part <c>i</c> of the map tags the triangles of body handle <c>i</c>. The body's handles stay owned by
/// the caller, and an outfit's views over them must be disposed before the body unloads.</remarks>
public sealed class LoadedBodyRegions
{
    readonly BodyRegionMap _map;

    internal LoadedBodyRegions(AttachmentSetCache owner, BodyRegionMap map, SkinnedMeshHandle[] parts, uint[][] indices)
    {
        Owner = owner;
        _map = map;
        Parts = parts;
        Indices = indices;
    }

    internal AttachmentSetCache Owner { get; }

    /// <summary>The body's material part handles, in the map's part order.</summary>
    internal IReadOnlyList<SkinnedMeshHandle> Parts { get; }

    /// <summary>Each body part's index list, copied at load so a later change to the caller's mesh cannot move it.</summary>
    internal IReadOnlyList<uint[]> Indices { get; }

    /// <summary>The verified region map.</summary>
    public BodyRegionMap Map
    {
        get { Owner.ThrowIfDisposed(); return _map; }
    }
}
