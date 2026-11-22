using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D;

namespace KhaozEngine.Attachments.Render3D;

/// <summary>
/// Shares skinned mesh views between outfits on one scene. A view is keyed by its source handle, the exact index
/// list it draws and any displaced rest positions. Each view is
/// counted by user and released once, when its last outfit lets go. The scene owns frame lifetime, so a released
/// view keeps drawing until the frame ends and nothing here defers.
/// </summary>
internal sealed class OutfitViewCache
{
    readonly IAttachmentViewScene _scene;
    readonly Dictionary<ViewKey, SharedView> _views = [];

    internal OutfitViewCache(IAttachmentViewScene scene) => _scene = scene;

    /// <summary>Take one use of the view drawing <paramref name="key"/>, creating it on first use.
    /// Return the retained key so all callers release the view using the same backing arrays.</summary>
    internal SkinnedMeshHandle Acquire(ViewKey key, out ViewKey canonicalKey)
    {
        if (_views.TryGetValue(key, out SharedView? shared))
        {
            shared.Users++;
            canonicalKey = shared.Key;
            return shared.View;
        }
        SkinnedMeshHandle view = key.RestPositions is { } restPositions
            ? ((IAttachmentDisplacedViewScene)_scene).CreateSkinnedView(key.Source, key.Indices.Span, restPositions.Span)
            : _scene.CreateSkinnedView(key.Source, key.Indices.Span);
        _views.Add(key, new SharedView(key, view));
        canonicalKey = key;
        return view;
    }

    /// <summary>Give back one use taken by <see cref="Acquire"/>, releasing the view with its last user.</summary>
    /// <remarks>The entry leaves the cache before the scene is asked, so a view whose release throws is never handed
    /// out again. A throwing release means the scene no longer treats the handle as a view this cache may free, such
    /// as one the caller already unloaded, so keeping it for reuse would hand out a dead handle.</remarks>
    internal void Release(ViewKey key)
    {
        SharedView shared = _views[key];
        if (--shared.Users > 0) return;
        _views.Remove(key);
        _scene.ReleaseSkinnedView(shared.View);
    }

    sealed class SharedView(ViewKey key, SkinnedMeshHandle view)
    {
        internal ViewKey Key { get; } = key;
        internal SkinnedMeshHandle View { get; } = view;
        internal int Users { get; set; } = 1;
    }
}

/// <summary>A view's identity: the source mesh, its visible triangles and optional rest positions by content.</summary>
internal readonly struct ViewKey : IEquatable<ViewKey>
{
    readonly int _hash;

    internal ViewKey(SkinnedMeshHandle source, ReadOnlyMemory<uint> indices, ReadOnlyMemory<Vector3>? restPositions = null)
    {
        Source = source;
        Indices = indices;
        RestPositions = restPositions;
        var hash = new HashCode();
        hash.Add(source);
        hash.AddBytes(MemoryMarshal.AsBytes(indices.Span));
        hash.Add(restPositions.HasValue);
        if (restPositions is { } positions)
            foreach (Vector3 position in positions.Span)
                hash.Add(position);
        _hash = hash.ToHashCode();
    }

    internal SkinnedMeshHandle Source { get; }
    internal ReadOnlyMemory<uint> Indices { get; }
    internal ReadOnlyMemory<Vector3>? RestPositions { get; }

    public bool Equals(ViewKey other) =>
        _hash == other._hash && Source.Equals(other.Source) && Indices.Span.SequenceEqual(other.Indices.Span) &&
        RestPositions.HasValue == other.RestPositions.HasValue &&
        RestPositions.GetValueOrDefault().Span.SequenceEqual(other.RestPositions.GetValueOrDefault().Span);

    public override bool Equals(object? obj) => obj is ViewKey other && Equals(other);

    public override int GetHashCode() => _hash;
}
