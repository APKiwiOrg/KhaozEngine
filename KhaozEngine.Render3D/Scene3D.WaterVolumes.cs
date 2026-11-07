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
        ReadOnlySpan<WaterPlane> planes = RelativeWaterPlanes();
        for (int planeIndex = 0; planeIndex < planes.Length; planeIndex++)
        {
            ref readonly WaterPlane plane = ref planes[planeIndex];
            if (plane.Look is { } look) look.ResolveInto(_waterVolumeScratch, Post.Water);
            else _waterVolumeScratch.CopyFrom(Post.Water);
            if (!_waterVolumeScratch.UnderwaterSunlight && !_waterVolumeScratch.WaterColumnLight
                && !WaterCausticPlan.Participates(plane, _waterVolumeScratch)) continue;
            Vector3 attenuation;
            if (_waterVolumeScratch.WaterColumnLight)
                attenuation = WaterColumnOptics.Derive(_waterVolumeScratch.WaterColor, _waterVolumeScratch.Clarity).Extinction;
            else
            {
                var absorption = _waterVolumeScratch.AbsorptionPerMetre;
                attenuation = new Vector3(absorption.R, absorption.G, absorption.B);
            }
            _waterVolumeRecords.Add(new WaterVolumeRecord(
                plane.CenterX - plane.HalfExtentX, plane.CenterZ - plane.HalfExtentZ,
                plane.CenterX + plane.HalfExtentX, plane.CenterZ + plane.HalfExtentZ,
                plane.SurfaceY, plane.FloorY ?? float.MinValue,
                attenuation, _waterVolumeScratch.UnderwaterPenumbraPerMetre,
                _waterCausticPlan.LayerOfPlane(planeIndex), _waterCausticPlan.DepthOfPlane(planeIndex)));
        }
        return CollectionsMarshal.AsSpan(_waterVolumeRecords);
    }
}
