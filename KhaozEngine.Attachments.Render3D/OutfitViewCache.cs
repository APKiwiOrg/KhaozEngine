using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

/// <summary>
/// Shares skinned mesh views between outfits on one scene. A view is keyed by its source handle and the exact index
/// list it draws, so two outfits that hide the same triangles of one mesh draw through one view. Each view is
/// counted by user and released once, when its last outfit lets go. The scene owns frame lifetime, so a released
/// view keeps drawing until the frame ends and nothing here defers.
/// </summary>
internal sealed class OutfitViewCache
{
    readonly IAttachmentViewScene _scene;
    readonly Dictionary<ViewKey, SharedView> _views = [];

    internal OutfitViewCache(IAttachmentViewScene scene) => _scene = scene;

    /// <summary>Take one use of the view drawing <paramref name="key"/>, creating it on first use.</summary>
    internal SkinnedMeshHandle Acquire(ViewKey key)
    {
        if (_views.TryGetValue(key, out SharedView? shared))
        {
            shared.Users++;
            return shared.View;
        }
        SkinnedMeshHandle view = _scene.CreateSkinnedView(key.Source, key.Indices);
        _views.Add(key, new SharedView(view));
        return view;
    }

    /// <summary>Give back one use taken by <see cref="Acquire"/>, releasing the view with its last user.</summary>
    internal void Release(ViewKey key)
    {
        SharedView shared = _views[key];
        if (--shared.Users > 0) return;
        _views.Remove(key);
        _scene.ReleaseSkinnedView(shared.View);
    }

    sealed class SharedView(SkinnedMeshHandle view)
    {
        internal SkinnedMeshHandle View { get; } = view;
        internal int Users { get; set; } = 1;
    }
}

/// <summary>A view's identity: the source mesh and the triangle indices it keeps, compared by content.</summary>
internal readonly struct ViewKey : IEquatable<ViewKey>
{
    readonly int _hash;

    internal ViewKey(SkinnedMeshHandle source, uint[] indices)
    {
        Source = source;
        Indices = indices;
        var hash = new HashCode();
        hash.Add(source);
        hash.AddBytes(System.Runtime.InteropServices.MemoryMarshal.AsBytes(indices.AsSpan()));
        _hash = hash.ToHashCode();
    }

    internal SkinnedMeshHandle Source { get; }
    internal uint[] Indices { get; }

    public bool Equals(ViewKey other) =>
        _hash == other._hash && Source.Equals(other.Source) && Indices.AsSpan().SequenceEqual(other.Indices);

    public override bool Equals(object? obj) => obj is ViewKey other && Equals(other);

    public override int GetHashCode() => _hash;
}
