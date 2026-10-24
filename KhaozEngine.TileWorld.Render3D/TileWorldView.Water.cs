using System;
using System.Collections.Generic;
using KhaozEngine.Render3D;

namespace KhaozEngine.TileWorld;

public sealed partial class TileWorldView
{
    // Empty preparations retain ground provenance, but own no water cache entry or field.
    internal int WaterCacheCount
    {
        get
        {
            int count = 0;
            foreach (var entry in _publication.Resident)
                if (entry.Value.Planes.Length > 0) count++;
            return count;
        }
    }

    /// <summary>Submit the water of complete prepared region generations. Call <see cref="Flush()"/>
    /// after changing a look or source data and before submitting a frame. This walk never reads the
    /// source document, prepares resources or performs I/O.</summary>
    public void DrawWaterPlanes()
    {
        ObjectDisposedException.ThrowIf(_disposed, this);
        foreach (var entry in _publication.Resident)
        {
            WaterPlane[] planes = entry.Value.Planes;
            for (int i = 0; i < planes.Length; i++) _scene.DrawWater(planes[i]);
        }
    }

    IReadOnlyList<WaterPlane> PreparedWaterOf(RegionCoord region, int plane) =>
        _publication.Resident.TryGetValue((region, plane), out TileWaterGeneration? generation)
            ? generation.Planes : Array.Empty<WaterPlane>();

    void ReleaseWater(RegionCoord region, int plane)
    {
        _publication.Release((region, plane));
        _waterAttempts.Remove((region, plane));
    }

    void ReleaseAllWater()
    {
        _publication.ReleaseAll();
        _waterAttempts.Clear();
        _waterSourceRefusals.Clear();
        _pendingWater.Clear();
    }
}
