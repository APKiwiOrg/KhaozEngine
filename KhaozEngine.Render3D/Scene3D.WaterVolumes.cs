using System;
using System.Collections.Generic;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D;

public sealed partial class Scene3D
{
    readonly WaterSettings _waterVolumeScratch = new();
    readonly List<WaterVolumeRecord> _waterVolumeRecords = new();

    /// <summary>Diagnostics for the water volume clusters uploaded for the latest rendered frame.</summary>
    public WaterVolumeClusterDiagnostics WaterVolumeClusters { get; private set; }

    ReadOnlySpan<WaterVolumeRecord> CollectWaterVolumeRecords()
    {
        _waterVolumeRecords.Clear();
        foreach (WaterPlane plane in RelativeWaterPlanes())
        {
            if (plane.Look is { } look) look.ResolveInto(_waterVolumeScratch, Post.Water);
            else _waterVolumeScratch.CopyFrom(Post.Water);
            if (!_waterVolumeScratch.UnderwaterSunlight) continue;
            var absorption = _waterVolumeScratch.AbsorptionPerMetre;
            _waterVolumeRecords.Add(new WaterVolumeRecord(
                plane.CenterX - plane.HalfExtentX, plane.CenterZ - plane.HalfExtentZ,
                plane.CenterX + plane.HalfExtentX, plane.CenterZ + plane.HalfExtentZ,
                plane.SurfaceY, plane.FloorY ?? float.MinValue,
                new Vector3(absorption.R, absorption.G, absorption.B), _waterVolumeScratch.UnderwaterPenumbraPerMetre));
        }
        return CollectionsMarshal.AsSpan(_waterVolumeRecords);
    }
}
