using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;

namespace KhaozEngine.TileWorld;

public sealed partial class TileWorldView
{
    // Scene-depth water is collected lazily, once per region-plane mesh, exactly as before optical fields. A
    // rebuild is detected by the MESH HANDLE rather than announced: a remeshed region-plane comes back under a
    // handle whose generation the scene bumped when it freed the old one, and any edit that could move a water
    // tile or a corner height is exactly an edit that remeshes. Authored-bed water instead draws the planes of its
    // resident publication generations.
    readonly record struct WaterCache(int MeshIndex, int MeshGeneration, WaterLook? Look, IReadOnlyList<WaterPlane> Planes);

    readonly Dictionary<(RegionCoord Region, int Plane), WaterCache> _water = new();

    /// <summary>Body collections over a region-plane's tiles, for the tests that pin when water is scanned.</summary>
    internal int WaterCollections { get; private set; }

    // Region-planes holding water: the scene-depth cache, or resident generations with planes.
    internal int WaterCacheCount
    {
        get
        {
            if (_waterDepthMode != TileWaterDepthMode.AuthoredBed) return _water.Count;
            int count = 0;
            foreach (var entry in _publication.Resident)
                if (entry.Value.Planes.Length > 0) count++;
            return count;
        }
    }

    /// <summary>Submit this frame's water. Scene-depth planes are collected once per region-plane mesh and
    /// cached. Authored-bed planes are those of complete prepared region generations: call
    /// <see cref="Flush()"/> after changing a look or source data and before submitting a frame. Neither walk
    /// prepares resources or performs I/O.</summary>
    public void DrawWaterPlanes()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        if (_waterDepthMode != TileWaterDepthMode.AuthoredBed)
        {
            foreach (KeyValuePair<RegionCoord, RegionHandles> entry in _loaded)
            {
                MeshHandle?[] meshes = entry.Value.Meshes;
                for (int plane = 0; plane < _planes; plane++)
                {
                    IReadOnlyList<WaterPlane> collected = WaterOf(entry.Key, plane, meshes[plane]);
                    for (int i = 0; i < collected.Count; i++) _scene.DrawWater(collected[i]);
                }
            }
            return;
        }
        foreach (var entry in _publication.Resident)
        {
            WaterPlane[] planes = entry.Value.Planes;
            for (int i = 0; i < planes.Length; i++) _scene.DrawWater(planes[i]);
        }
    }

    IReadOnlyList<WaterPlane> PreparedWaterOf(RegionCoord region, int plane)
    {
        if (_waterDepthMode != TileWaterDepthMode.AuthoredBed)
            return _loaded.TryGetValue(region, out RegionHandles? handles)
                ? WaterOf(region, plane, handles.Meshes[plane]) : Array.Empty<WaterPlane>();
        return _publication.Resident.TryGetValue((region, plane), out TileWaterGeneration? generation)
            ? generation.Planes : Array.Empty<WaterPlane>();
    }

    // The cached scene-depth planes of one region-plane, re-collected when the mesh behind it changed or the
    // look did.
    IReadOnlyList<WaterPlane> WaterOf(RegionCoord region, int plane, MeshHandle? mesh)
    {
        int index = mesh?.Index ?? -1, generation = mesh?.Generation ?? 0;
        if (_water.TryGetValue((region, plane), out WaterCache cached)
            && cached.MeshIndex == index && cached.MeshGeneration == generation
            && ReferenceEquals(cached.Look, _options.WaterLook))
            return cached.Planes;

        // No mesh means the region-plane has no drawable tile at all, and a water tile is drawable by
        // definition (it carries an underlay), so there is nothing to scan 4096 tiles for.
        IReadOnlyList<WaterPlane> planes = Array.Empty<WaterPlane>();
        if (mesh is not null)
        {
            WaterCollections++;
            planes = TileWaterPlanes.Collect(_doc, _catalogs, region, plane, _options.WaterLook);
        }
        _water[(region, plane)] = new WaterCache(index, generation, _options.WaterLook, planes);
        return planes;
    }

    void ReleaseWater(RegionCoord region, int plane)
    {
        _water.Remove((region, plane));
        _publication.Release((region, plane));
        _waterAttempts.Remove((region, plane));
    }

    void ReleaseAllWater()
    {
        _water.Clear();
        _publication.ReleaseAll();
        _waterAttempts.Clear();
        _waterSourceRefusals.Clear();
        _pendingWater.Clear();
    }
}
