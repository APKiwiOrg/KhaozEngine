using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.Render3D;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>Today's WaterLook over canonical bounded geometry. R8b replaces optical inputs behind this
/// geometry/domain contract. This path makes no body-local optical-depth or horizon-continuation claim.</summary>
public static class NativeMapWater
{
    public const double ClipEpsilonMetres = .001;

    /// <summary>Scope motion padding must cover this value. A look with inherited displacement parameters or
    /// FFT sea-state inputs cannot certify a bounded clip domain and refuses explicitly.</summary>
    public static double RequiredMotionPadding(WaterLook look)
    {
        try { return checked(WaterSurfaceDisplacement.MaximumHorizontal(look) + ClipEpsilonMetres); }
        catch (NotSupportedException error) { throw new MapRenderAssetRefusal(MapRenderAssetStatus.Unsupported, error.Message); }
        catch (Exception error) when (error is ArgumentException or OverflowException)
        { throw new MapRenderAssetRefusal(MapRenderAssetStatus.Invalid, error.Message); }
    }

    public static MapWaterRenderBatch Prepare(MapScopedWaterResult water, WaterLook look)
    {
        ArgumentNullException.ThrowIfNull(water); ArgumentNullException.ThrowIfNull(look);
        if (water.Status is not (MapSceneStatus.CompleteForScope or MapSceneStatus.WholeWorldComplete) ||
            !water.RequestedWitness.CompleteForScope || !water.AcquiredCellWitness.CompleteForScope)
            throw new MapRenderAssetRefusal(MapRenderAssetStatus.Missing, "Water requires complete requested and canonical-cell coverage.");
        double halo = RequiredMotionPadding(look);
        if (!double.IsFinite(water.MotionPaddingMetres) || water.MotionPaddingMetres < halo)
            throw new MapRenderAssetRefusal(MapRenderAssetStatus.Invalid, "Scene MotionPaddingMetres does not cover this water look's certified displacement and clip epsilon.");
        var admission = new MapSceneAdmission(water.Limits);
        using var admitted = admission.Enter();
        try
        {
            admission.ReserveBytes(512);
            var clips = new MapWaterClipHalo(water, halo, admission);
            var parts = new List<MapWaterRenderPart>();
            var canonical = new List<MapWaterResourceCell>();
            var seen = new Dictionary<MapWaterCellKey, string>();
            long emitted = 0;
            foreach (var cell in water.Cells)
            {
                if (seen.TryGetValue(cell.Key, out string? digest))
                {
                    if (digest != cell.PayloadDigest) throw new MapRenderAssetRefusal(MapRenderAssetStatus.Invalid, "Duplicate water cell payloads disagree.");
                    continue;
                }
                admission.ReserveBytes(256); seen.Add(cell.Key, cell.PayloadDigest);
                canonical.Add(cell);
                long triangleCount = cell.Regions.Where(r => r.Kind == MapWaterRegionKind.FreeSurface).Sum(r => (long)r.Triangles.Count);
                if (3L * triangleCount > MapWaterScopedCompiler.MaxEmittedVertices - emitted) throw new MapSceneCapacityException();
                admission.ReserveBytes(checked(triangleCount * 8));
                var triangles = cell.Regions.Where(r => r.Kind == MapWaterRegionKind.FreeSurface).SelectMany(r => r.Triangles).ToArray();
                if (cell.Regions.Any(r => r.Kind == MapWaterRegionKind.Unresolved))
                    throw new MapRenderAssetRefusal(MapRenderAssetStatus.Missing, "Unresolved water cannot be drawn.");
                if (triangles.Length == 0) continue;
                emitted = checked(emitted + 3L * triangles.Length);
                if (emitted > MapWaterScopedCompiler.MaxEmittedVertices || parts.Count >= MapWaterScopedCompiler.MaxCells)
                    throw new MapSceneCapacityException();
                // List, dictionary, final arrays and the detached typed registration are all admitted before copies.
                admission.ReserveCompilation(triangles.Length);
                admission.ReserveBytes(checked(512L + 3L * triangles.Length * (192L + ModelVertex.SizeInBytes * 2L + 16L * 3)));
                var points = triangles.SelectMany(t => new[] { t.A, t.B, t.C }).ToArray();
                var anchor = new MapSubmissionAnchor(points.Min(p => p.X).Floor(), MapExactValue.FromSingle(cell.SurfaceY).Floor(), points.Min(p => p.Z).Floor());
                var vertices = new List<ModelVertex>(); var indices = new List<uint>();
                var byPoint = new Dictionary<MapExactPoint, uint>();
                for (int i = 0; i < points.Length; i++)
                {
                    var offset = MapSubmissionGeometry.Offset(points[i], anchor);
                    if (!byPoint.TryGetValue(points[i], out uint index))
                    {
                        index = (uint)vertices.Count; byPoint.Add(points[i], index);
                        vertices.Add(new(offset, Vector3.UnitY, Vector4.One));
                    }
                    indices.Add(index);
                }
                var mesh = new GltfMesh(vertices.ToArray(), indices.ToArray());
                var clip = clips.Build(cell, anchor);
                var payload = new MapWaterResource(mesh, clip.Data);
                var key = new MapRenderKey(MapRenderKind.Water, cell.Key.BodyId, MapWaterScopedCompiler.CellId(cell.Key), 0);
                double vertical = Math.Max(0, look.SwellAmplitude!.Value) + ClipEpsilonMetres;
                var bounds = new MapBox3(points.Min(p => p.X.ToDouble()) - halo, cell.SurfaceY - vertical, points.Min(p => p.Z.ToDouble()) - halo,
                    points.Max(p => p.X.ToDouble()) + halo, cell.SurfaceY + vertical, points.Max(p => p.Z.ToDouble()) + halo);
                parts.Add(new(key, anchor, bounds, payload, clip.Digest));
            }
            admission.ReserveBytes(checked(8L * (parts.Count + water.Cells.Count)));
            return new(canonical, parts, MapFrozenWaterLook.Capture(look), admission.RetainedBytes);
        }
        catch (Exception error) when (error is MapSceneCapacityException or MapWaterCapacityException)
        { throw new MapRenderAssetRefusal(MapRenderAssetStatus.CapacityExceeded, "Water preparation exceeds its scene allowance."); }
        catch (Exception error) when (error is MapExactOverflowException or OverflowException)
        { throw new MapRenderAssetRefusal(MapRenderAssetStatus.Invalid, "Water geometry is not representable."); }
        catch (ArgumentException error) { throw new MapRenderAssetRefusal(MapRenderAssetStatus.Invalid, error.Message); }
    }
}
