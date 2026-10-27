using DemandDiagnostics = KhaozEngine.Diagnostics.CapacityDemandDiagnostics;
using KhaozEngine.Diagnostics;
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

    /// <summary>Resolve scene inheritance before certifying the scope motion padding. FFT displacement
    /// certification belongs to R8b and refuses explicitly.</summary>
    public static double RequiredMotionPadding(WaterLook look, WaterSettings settings)
        => CertifiedMotionPadding(ResolveLook(look, settings));

    static double CertifiedMotionPadding(WaterLook look)
    {
        try { return checked(WaterSurfaceDisplacement.MaximumHorizontal(look) + ClipEpsilonMetres); }
        catch (NotSupportedException error) { throw new MapRenderAssetRefusal(MapRenderAssetStatus.Unsupported, error.Message); }
        catch (Exception error) when (error is ArgumentException or OverflowException)
        { throw new MapRenderAssetRefusal(MapRenderAssetStatus.Invalid, error.Message); }
    }

    /// <summary>Freeze the renderer's effective look against the caller's scene water settings before
    /// deriving bounded clip resources. Later scene-setting changes require preparation again.</summary>
    public static MapWaterRenderBatch Prepare(MapScopedWaterResult water, WaterLook look, WaterSettings settings)
    {
        ArgumentNullException.ThrowIfNull(water); ArgumentNullException.ThrowIfNull(look); ArgumentNullException.ThrowIfNull(settings);
        if (water.Capacity is { } capacity) throw new MapSceneCapacityException(capacity.LimitName, capacity.Requested, capacity.Allowance);
        if (water.Status is not (MapSceneStatus.CompleteForScope or MapSceneStatus.WholeWorldComplete) ||
            !water.RequestedWitness.CompleteForScope || !water.AcquiredCellWitness.CompleteForScope)
            throw new MapRenderAssetRefusal(MapRenderAssetStatus.Missing, "Water requires complete requested and canonical-cell coverage.");
        var admission = MapSceneAdmission.Current ?? new MapSceneAdmission(water.Limits);
        using var admitted = admission.Enter();
        using var preparation = admission.BeginPreparation();
        MapPreparationInventory? canonicalOwnership = null;
        try
        {
            using var canonicalWork = MapPreparationInventory.Acquire(admission,
                water.AcquiredCells.Concat(water.Cells).Distinct<MapWaterResourceCell>(System.Collections.Generic.ReferenceEqualityComparer.Instance)
                    .Select(c => ((object)c, MapScopedWaterResult.CellInventoryBytes(c))));
            // Admit the scratch settings, explicit effective look and frozen frame copy before allocation.
            admission.ReserveBytes(2048);
            look = ResolveLook(look, settings);
            double halo = CertifiedMotionPadding(look);
            if (!double.IsFinite(water.MotionPaddingMetres) || water.MotionPaddingMetres < 0 ||
                water.Cells.Count > 0 && water.MotionPaddingMetres < halo)
                throw new MapRenderAssetRefusal(MapRenderAssetStatus.Invalid, "Scene MotionPaddingMetres does not cover this water look's certified displacement and clip epsilon.");
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
                using var cellPreparation = admission.BeginPreparation();
                canonical.Add(cell);
                long triangleCount = cell.Regions.Where(r => r.Kind == MapWaterRegionKind.FreeSurface).Sum(r => (long)r.Triangles.Count);
                if (DemandDiagnostics.Check(admission, "NativeMapWater.MaxEmittedVertices.1", "MaxEmittedVertices", emitted, 3m * triangleCount, MapWaterScopedCompiler.MaxEmittedVertices, 3L * triangleCount > MapWaterScopedCompiler.MaxEmittedVertices - emitted, CapacityDemandLifetime.Inventory)) throw new MapSceneCapacityException(nameof(MapWaterScopedCompiler.MaxEmittedVertices), checked(emitted + 3L * triangleCount), MapWaterScopedCompiler.MaxEmittedVertices);
                admission.ReserveBytes(checked(triangleCount * 8));
                var triangles = cell.Regions.Where(r => r.Kind == MapWaterRegionKind.FreeSurface).SelectMany(r => r.Triangles).ToArray();
                if (cell.Regions.Any(r => r.Kind == MapWaterRegionKind.Unresolved))
                    throw new MapRenderAssetRefusal(MapRenderAssetStatus.Missing, "Unresolved water cannot be drawn.");
                if (triangles.Length == 0) continue;
                emitted = checked(emitted + 3L * triangles.Length);
                if (DemandDiagnostics.Check(admission, "NativeMapWater.MaxEmittedVertices.2", "MaxEmittedVertices", 0, emitted, MapWaterScopedCompiler.MaxEmittedVertices, emitted > MapWaterScopedCompiler.MaxEmittedVertices, CapacityDemandLifetime.Inventory))
                    throw new MapSceneCapacityException(nameof(MapWaterScopedCompiler.MaxEmittedVertices), emitted, MapWaterScopedCompiler.MaxEmittedVertices);
                if (DemandDiagnostics.Check(admission, "NativeMapWater.MaxCells.1", "MaxCells", parts.Count, 1, MapWaterScopedCompiler.MaxCells, parts.Count >= MapWaterScopedCompiler.MaxCells, CapacityDemandLifetime.Inventory))
                    throw new MapSceneCapacityException(nameof(MapWaterScopedCompiler.MaxCells), (long)parts.Count + 1, MapWaterScopedCompiler.MaxCells);
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
                cellPreparation.Complete(checked(512L + ModelVertex.SizeInBytes * (long)payload.BorrowMesh.Vertices.Length +
                    4L * payload.BorrowMesh.Indices32.Length + payload.BorrowClipData.Length));
            }
            admission.ReserveBytes(checked(8L * (parts.Count + water.Cells.Count)));
            var result = new MapWaterRenderBatch(canonical, parts, MapFrozenWaterLook.Capture(look), 0);
            canonicalOwnership = MapPreparationInventory.Acquire(admission,
                canonical.Select(c => ((object)c, MapScopedWaterResult.CellInventoryBytes(c))));
            result.Own(preparation.Complete(result.OwnRetainedBytes));
            result.OwnCanonical(canonicalOwnership); canonicalOwnership = null;
            return result;
        }
        catch (Exception error) when (error is MapSceneCapacityException or MapWaterCapacityException)
        { throw new MapRenderAssetRefusal(MapRenderAssetStatus.CapacityExceeded, error.Message, error); }
        catch (Exception error) when (error is MapExactOverflowException or OverflowException)
        { throw new MapRenderAssetRefusal(MapRenderAssetStatus.Invalid, "Water geometry is not representable."); }
        catch (ArgumentException error) { throw new MapRenderAssetRefusal(MapRenderAssetStatus.Invalid, error.Message); }
        finally { canonicalOwnership?.Dispose(); }
    }

    static WaterLook ResolveLook(WaterLook look, WaterSettings settings)
    {
        ArgumentNullException.ThrowIfNull(look); ArgumentNullException.ThrowIfNull(settings);
        // Use the released renderer's resolution rule, then detach every per-body field from scene defaults.
        var effective = look.ResolveInto(new WaterSettings(), settings);
        return new()
        {
            WaveSource = effective.WaveSource,
            DeepColor = effective.DeepColor,
            ShallowColor = effective.ShallowColor,
            AbsorptionPerMetre = effective.AbsorptionPerMetre,
            ShallowDepth = effective.ShallowDepth,
            Opacity = effective.Opacity,
            SwellAmplitude = effective.SwellAmplitude,
            SwellWavelength = effective.SwellWavelength,
            SwellDirectionDegrees = effective.SwellDirectionDegrees,
            SwellSpreadDegrees = effective.SwellSpreadDegrees,
            SwellSteepness = effective.SwellSteepness,
            SwellSpeed = effective.SwellSpeed,
            SwellSeed = effective.SwellSeed,
            SwellComponents = effective.SwellComponents,
            ShoalingStrength = effective.ShoalingStrength,
            SurfStrength = effective.SurfStrength,
            WaveScale = effective.WaveScale,
            WaveSpeed = effective.WaveSpeed,
            NormalStrength = effective.NormalStrength,
            WaveWarpStrength = effective.WaveWarpStrength,
            RippleComponents = effective.RippleComponents,
            RippleLacunarity = effective.RippleLacunarity,
            RippleGain = effective.RippleGain,
            RippleSeed = effective.RippleSeed,
            VarianceToRoughness = effective.VarianceToRoughness,
            DetailFadeDistance = effective.DetailFadeDistance,
            DistantDetailScale = effective.DistantDetailScale,
            GlintStrength = effective.GlintStrength,
            GlintRoughness = effective.GlintRoughness,
            GlintDistantRoughness = effective.GlintDistantRoughness,
            GlintExponent = effective.GlintExponent,
            FoamColor = effective.FoamColor,
            FoamStrength = effective.FoamStrength,
            FoamCrestCoverage = effective.FoamCrestCoverage,
            FoamShoreWidth = effective.FoamShoreWidth,
            FoamPatternScale = effective.FoamPatternScale,
            ShoreFadeDistance = effective.ShoreFadeDistance
        };
    }
}
