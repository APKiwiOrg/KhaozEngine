using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Render3D;

namespace KhaozEngine.TileWorld.Render3D;

/// <summary>
/// The model box per archetype, measured once from the resolved mesh vertices and cached by archetype id: the
/// <see cref="TileObjectRaycast.BoundsSource"/> a game hands the object picker, backed by the SAME resolver its
/// view draws through, so the box tested is the model drawn. A greybox fallback resolves like any other mesh
/// and measures to its own box, which is what keeps a world with missing art clickable.
/// </summary>
/// <remarks>Measuring walks every vertex once per archetype per process, and the resolver's own per-archetype
/// part cache makes the resolve behind it a lookup, so the steady-state cost of the source is one dictionary
/// probe. Not thread-safe, exactly like the resolver it wraps: one view, one cache.</remarks>
public sealed class TileObjectBoundsCache
{
    readonly ITileMeshResolver _meshes;
    readonly Dictionary<string, (Vector3 Min, Vector3 Max)?> _byArchetype = new();

    /// <summary>Wraps the resolver the view draws with.</summary>
    /// <param name="meshes">The mesh resolver, from the same wiring the view was built on.</param>
    public TileObjectBoundsCache(ITileMeshResolver meshes) =>
        _meshes = meshes ?? throw new ArgumentNullException(nameof(meshes));

    /// <summary>The <see cref="TileObjectRaycast.BoundsSource"/> form: the archetype's model box, measured on
    /// first ask and cached. False when the resolver produced no parts at all, which drops the object from
    /// picking rather than inventing a box.</summary>
    /// <param name="archetype">The archetype being asked about.</param>
    /// <param name="min">The merged vertex minimum, relative to the anchor.</param>
    /// <param name="max">The merged vertex maximum.</param>
    public bool TryGetBounds(TileObjectArchetype archetype, out Vector3 min, out Vector3 max)
    {
        min = default;
        max = default;
        if (archetype is null) return false;
        if (!_byArchetype.TryGetValue(archetype.Id, out (Vector3 Min, Vector3 Max)? cached))
        {
            cached = Measure(archetype);
            _byArchetype[archetype.Id] = cached;
        }
        if (cached is not { } box) return false;
        (min, max) = box;
        return true;
    }

    (Vector3 Min, Vector3 Max)? Measure(TileObjectArchetype archetype)
    {
        IReadOnlyList<GltfMeshPart>? parts = _meshes.Resolve(archetype);
        if (parts is not { Count: > 0 }) return null;
        var min = new Vector3(float.MaxValue);
        var max = new Vector3(float.MinValue);
        bool any = false;
        foreach (GltfMeshPart part in parts)
        {
            foreach (ModelVertex v in part.Mesh.Vertices)
            {
                min = Vector3.Min(min, v.Position);
                max = Vector3.Max(max, v.Position);
                any = true;
            }
        }
        return any ? (min, max) : null;
    }
}
