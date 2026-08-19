using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;

namespace KhaozEngine.TileWorld;

public sealed partial class TileWorldView
{
    // One region-plane's collected water, plus the two things it was collected against. A rebuild is detected by
    // the MESH HANDLE rather than announced: Flush builds the new mesh before it frees the old one, so a
    // remeshed region-plane always comes back with a fresh generation, and any edit that could move a water
    // tile or a corner height is exactly an edit that remeshes. That keeps the cache honest without the water
    // path having to be wired into the rebuild path itself.
    readonly record struct WaterCache(int MeshIndex, int MeshGeneration, WaterLook? Look, IReadOnlyList<WaterPlane> Planes);

    readonly Dictionary<(RegionCoord Region, int Plane), WaterCache> _water = new();

    /// <summary>The look every water plane this view queues carries. Defaults to
    /// <see cref="TileWaterLooks.River"/>. Null draws the planes with the scene's own water settings instead,
    /// which is what a world whose scene look is already a river wants. Changing it re-collects on the next
    /// <see cref="DrawWaterPlanes"/>.</summary>
    public WaterLook? WaterLook { get; set; } = TileWaterLooks.River;

    /// <summary>How many region-planes the water cache is holding, for the tests that assert an unload drops
    /// what it collected.</summary>
    internal int WaterCacheCount => _water.Count;

    /// <summary>Queues every loaded region-plane's water surfaces for this frame, through
    /// <see cref="ITileWorldScene.DrawWater"/>.
    /// <para>Call it right after <see cref="Draw"/>, which is where the pending rebuilds are flushed. It is a
    /// separate call rather than part of <see cref="Draw"/> so a caller that runs its own water pass can leave
    /// it out. The planes themselves are collected once per region-plane mesh and cached, so a frame that
    /// changed nothing is a walk over the loaded regions and one submit per plane.</para></summary>
    public void DrawWaterPlanes()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);

        foreach (KeyValuePair<RegionCoord, RegionHandles> entry in _loaded)
        {
            MeshHandle?[] meshes = entry.Value.Meshes;
            for (int plane = 0; plane < _planes; plane++)
            {
                IReadOnlyList<WaterPlane> planes = WaterOf(entry.Key, plane, meshes[plane]);
                for (int i = 0; i < planes.Count; i++) _scene.DrawWater(planes[i]);
            }
        }

        PruneWater();
    }

    // The cached planes of one region-plane, re-collected when the mesh behind it changed or the look did.
    IReadOnlyList<WaterPlane> WaterOf(RegionCoord region, int plane, MeshHandle? mesh)
    {
        int index = mesh?.Index ?? -1, generation = mesh?.Generation ?? 0;
        if (_water.TryGetValue((region, plane), out WaterCache cached)
            && cached.MeshIndex == index && cached.MeshGeneration == generation
            && ReferenceEquals(cached.Look, WaterLook))
            return cached.Planes;

        // No mesh means the region-plane has no drawable tile at all, and a water tile is drawable by
        // definition (it carries an underlay), so there is nothing to scan 4096 tiles for.
        IReadOnlyList<WaterPlane> planes = mesh is null
            ? Array.Empty<WaterPlane>()
            : TileWaterPlanes.Collect(_doc, _catalogs, region, plane, WaterLook);
        _water[(region, plane)] = new WaterCache(index, generation, WaterLook, planes);
        return planes;
    }

    // Drops what an unloaded region left behind. The unload path lives in the view's main body and does not
    // reach in here, so the drop happens at the end of the submit instead, where a full pass has just refreshed
    // every entry a loaded region owns. The count test is the whole fast path: after that pass the cache holds
    // exactly one entry per loaded region-plane unless something was unloaded.
    void PruneWater()
    {
        if (_water.Count == _loaded.Count * _planes) return;

        List<(RegionCoord Region, int Plane)>? drop = null;
        foreach ((RegionCoord Region, int Plane) key in _water.Keys)
            if (!_loaded.ContainsKey(key.Region)) (drop ??= new List<(RegionCoord, int)>()).Add(key);
        if (drop is null) return;
        foreach ((RegionCoord Region, int Plane) key in drop) _water.Remove(key);
    }
}
