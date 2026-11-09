using DemandDiagnostics = KhaozEngine.Diagnostics.CapacityDemandDiagnostics;
using KhaozEngine.Diagnostics;
using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Security.Cryptography;
using System.Text.Json;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Render3D;

namespace KhaozEngine.MapDoc.Render3D;

/// <summary>Prepares acquired canonical faces at every distance. Paint changes render coverage only. Materials
/// come exclusively from the selected input lease, and no preparation path creates a device or uploads a resource.</summary>
public static class NativeMapTerrain
{
    internal static long CanonicalRowBytes => System.Runtime.CompilerServices.Unsafe.SizeOf<MapCompiledFace>() +
        System.Runtime.CompilerServices.Unsafe.SizeOf<MapExactTriangle>() + System.Runtime.CompilerServices.Unsafe.SizeOf<MapPaintCoverage>() +
        System.Runtime.CompilerServices.Unsafe.SizeOf<MapSubmissionAnchor>() + IntPtr.Size;
    public static MapTerrainRenderBatch Prepare(MapScopedScene scene, MapPreparedInputLease input,
        IReadOnlyDictionary<ushort, string> materialIds) => Prepare(scene, input, materialIds, TileGroundMaterialConfig.MaxMaterials);

    /// <summary>A smaller layer allowance may narrow the renderer's fixed capacity, never widen it.</summary>
    public static MapTerrainRenderBatch Prepare(MapScopedScene scene, MapPreparedInputLease input,
        IReadOnlyDictionary<ushort, string> materialIds, int maxMaterials)
    {
        ArgumentNullException.ThrowIfNull(scene); ArgumentNullException.ThrowIfNull(input); ArgumentNullException.ThrowIfNull(materialIds);
        if (maxMaterials is < 1 or > TileGroundMaterialConfig.MaxMaterials) throw new ArgumentOutOfRangeException(nameof(maxMaterials));
        if (!scene.Witness.CompleteForScope) throw new MapRenderAssetRefusal(MapRenderAssetStatus.Missing, "Terrain requires complete acquired surfaces.");
        if (input.ClosureHash != scene.Witness.ClosureHash) throw new MapRenderAssetRefusal(MapRenderAssetStatus.Stale, "Terrain and prepared input closures differ.");
        var admission = MapSceneAdmission.Current ?? new MapSceneAdmission(scene.Source.Data.Limits);
        using var admitted = admission.Enter();
        using var preparation = admission.BeginPreparation();
        try
        {
            admission.ReserveBytes(512);
            var canonical = Capture(scene, admission);
            var regionLookup = scene.PaintRegions.Values.SelectMany(p => p.Regions).ToLookup(r => r.Face);
            using var partitionScratch = admission.Workspace();
            var partitions = canonical.SelectMany(f => MapTerrainPaint.Partition(f, regionLookup[f.Face.Key], admission, partitionScratch)).ToArray();
            var palettes = Palettes(canonical, partitions, maxMaterials, admission);
            var required = palettes.Sets.SelectMany(s => s).Distinct().Order().ToArray();
            var layers = new Dictionary<ushort, MapTerrainLayer>();
            var findings = new List<MapTerrainFinding>();
            foreach (ushort id in required)
            {
                if (!materialIds.TryGetValue(id, out string? resourceId) || resourceId is null || !input.Materials.TryGetValue(resourceId, out var material))
                    throw new MapRenderAssetRefusal(MapRenderAssetStatus.Missing, "Terrain material has no selected verified input: " + id.ToString(CultureInfo.InvariantCulture));
                admission.ReserveBytes(128);
                layers.Add(id, new(id, material));
                if (material.BorrowMaps.Normal.HasValue) findings.Add(new(MapTerrainFindingKind.NormalMapNotConsumed, id, resourceId));
                if (material.BorrowMaps.Roughness.HasValue) findings.Add(new(MapTerrainFindingKind.RoughnessMapNotConsumed, id, resourceId));
            }
            var materials = palettes.Sets.Select(ids => new MapTerrainMaterialSet(ids.Order().Select(id => layers[id]))).ToArray();
            var byKey = canonical.ToDictionary(f => f.Face.Key);
            MapTerrainMesh.Admit(partitions, byKey, admission);
            var boundaries = MapTerrainPaint.Boundaries(partitions, scene.SurfaceView, admission);
            var parts = new List<MapTerrainPart>();
            foreach (var group in partitions.Where(p => palettes.ByFace.ContainsKey(p.Face)).GroupBy(p =>
                (p.Face.OwnerId, p.Face.Patch, byKey[p.Face].Anchor, Palette: palettes.ByFace[p.Face])))
            {
                using var groupPreparation = admission.BeginPreparation();
                admission.ReserveBytes(MapTerrainMesh.WorkspaceBytes(group, byKey));
                var material = materials[group.Key.Palette];
                var mesh = MapTerrainMesh.Build(group, byKey, group.Key.Anchor, material, boundaries);
                if (mesh.Mesh.TriangleCount == 0) continue;
                string partId = group.Key.Patch is { } patch
                    ? FormattableString.Invariant($"patch/{patch.SlotX}/{patch.SlotZ}/set/{palettes.LocalOrdinals[group.Key.Palette]}")
                    : "strip/set/" + palettes.LocalOrdinals[group.Key.Palette].ToString(CultureInfo.InvariantCulture);
                var key = new MapRenderKey(MapRenderKind.Terrain, group.Key.OwnerId, partId, 0);
                parts.Add(new(key, group.Key.Anchor, Bounds(mesh.Mesh, group.Key.Anchor), mesh,
                    Digest(mesh, input.ClosureHash, group.Key.Anchor)));
                groupPreparation.Complete(checked(512L + ModelVertex.SizeInBytes * (long)mesh.BorrowMesh.Vertices.Length +
                    4L * mesh.BorrowMesh.Indices32.Length + 80L * mesh.FaceKeys.Count));
            }
            admission.ReserveBytes(checked(8L * (canonical.Count + partitions.Length + parts.Count + findings.Count)));
            var result = new MapTerrainRenderBatch(input, canonical, partitions, parts, findings, 0);
            result.Own(preparation.Complete(result.RetainedBytes));
            return result;
        }
        catch (MapSceneCapacityException error) { throw new MapRenderAssetRefusal(MapRenderAssetStatus.CapacityExceeded, error.Message, error); }
        catch (Exception ex) when (ex is MapExactOverflowException or OverflowException)
        { throw new MapRenderAssetRefusal(MapRenderAssetStatus.Invalid, "Terrain geometry is not exactly representable."); }
    }

    static List<MapTerrainCanonicalFace> Capture(MapScopedScene scene, MapSceneAdmission admission)
    {
        using var preparation = admission.BeginPreparation();
        var result = new List<MapTerrainCanonicalFace>();
        if (!scene.Scope.Channels.HasFlag(MapSceneChannels.Terrain)) return result;
        var view = scene.SurfaceView;
        var surfaces = scene.Surfaces;
        foreach (var surface in surfaces.Refs.Where(s => s.Role != MapSurfaceRole.PaintOverride).OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            var paint = view.Surfaces.Where(s => s.Role == MapSurfaceRole.PaintOverride && s.PaintTargetSurfaceId == surface.Id &&
                view.EffectiveContext?.IsPartialPaint(s) != true).OrderBy(s => s.PaintPriority).ThenByDescending(s => s.Id, StringComparer.Ordinal).ToArray();
            foreach (var patch in surfaces.Patches.Values.Where(p => p.Key.SurfaceId == surface.Id))
            {
                using var patchPreparation = admission.BeginPreparation();
                int start = result.Count;
                var compiled = MapSurfaceCompiler.Compile(surface, patch);
                foreach (var source in paint)
                    foreach (var key in view.Witness.Present.Select(p => p.Key).Where(k => k.SurfaceId == source.Id).Order())
                        if (view.TryAcquiredPatch(key, out var overridePatch, out _) && overridePatch is not null)
                            compiled = Replace(compiled, MapSurfaceCompiler.ApplyPaintOverride(compiled, source, overridePatch));
                compiled = Replace(compiled, MapEffectiveGeometry.TransformPatch(view, compiled));
                for (int i = 0; i < compiled.Faces.Count; i++)
                {
                    admission.ReserveBytes(CanonicalRowBytes);
                    var face = compiled.Faces[i];
                    result.Add(new(face, compiled.ExactTriangle(face), compiled.Paint[i], compiled.Anchor));
                }
                patchPreparation.Complete(checked(CanonicalRowBytes * (result.Count - start)));
            }
        }
        foreach (var strip in surfaces.AllRecords().OfType<MapWallStrip>().OrderBy(s => s.Id, StringComparer.Ordinal))
        {
            if (!surfaces.TryGetRecord(strip.LowerChain, out var lower) || lower is not MapBoundaryChain low ||
                !surfaces.TryGetRecord(strip.UpperChain, out var upper) || upper is not MapBoundaryChain high)
                throw new MapRenderAssetRefusal(MapRenderAssetStatus.Missing, "Terrain wall requires both acquired boundary chains.");
            using var stripPreparation = admission.BeginPreparation();
            int start = result.Count;
            var compiled = MapWallStripCompiler.Compile(strip, MapBoundaryGeometry.ResolveChain(low, view), MapBoundaryGeometry.ResolveChain(high, view));
            foreach (var face in compiled.Faces)
            {
                admission.ReserveBytes(CanonicalRowBytes);
                result.Add(new(face, new(compiled.ExactVertices[face.A], compiled.ExactVertices[face.B], compiled.ExactVertices[face.C]),
                    new(face.Key, strip.MaterialId, 0, false, false), compiled.Anchor));
            }
            stripPreparation.Complete(checked(CanonicalRowBytes * (result.Count - start)));
        }
        admission.ReserveBytes(checked(8L * result.Count));
        var ordered = result.OrderBy(f => f.Face.Key).ToList();
        preparation.Complete(checked(CanonicalRowBytes * ordered.Count));
        return ordered;
    }

    static MapCompiledPatch Replace(MapCompiledPatch previous, MapCompiledPatch next)
    { if (!ReferenceEquals(previous, next)) previous.PreparationOwnership?.Dispose(); return next; }

    internal static ushort[] LayerIds(MapPaintCoverage paint) => paint.OverlayCovers && paint.Overlay != 0
        ? paint.Feather && paint.Underlay != 0 && paint.Underlay != paint.Overlay
            ? new[] { paint.Underlay, paint.Overlay } : new[] { paint.Overlay }
        : paint.Underlay == 0 ? Array.Empty<ushort>() : new[] { paint.Underlay };

    sealed record PalettePlan(IReadOnlyList<SortedSet<ushort>> Sets, IReadOnlyDictionary<MapFaceKey, int> ByFace,
        IReadOnlyList<int> LocalOrdinals);
    static PalettePlan Palettes(IReadOnlyList<MapTerrainCanonicalFace> canonical, IReadOnlyList<MapTerrainPartition> partitions,
        int maximum, MapSceneAdmission admission)
    {
        using var preparation = admission.BeginPreparation();
        admission.ReserveBytes(checked(512L + 128L * canonical.Count + 32L * partitions.Count));
        using var scratch = admission.Workspace();
        using var indexStorage = admission.Workspace();
        long indexBytes = 0;
        var sets = new List<SortedSet<ushort>>();
        var byFace = new Dictionary<MapFaceKey, int>();
        var localOrdinals = new List<int>();
        var coverage = partitions.ToLookup(p => p.Face);
        foreach (var owner in canonical.GroupBy(f => (f.Face.Key.OwnerId, f.Face.Key.Patch, f.Anchor)))
        {
            int current = -1, ordinal = 0;
            foreach (var face in owner)
            {
                long bytes = checked(256L + 128L * coverage[face.Face.Key].Count() + 64L * maximum);
                scratch.Reserve(bytes);
                try
                {
                    var needed = coverage[face.Face.Key].SelectMany(p => LayerIds(p.Paint)).Distinct().Order().ToArray();
                    if (needed.Length == 0) continue;
                    if (DemandDiagnostics.Check(admission, "NativeMapTerrain.MaxMaterials", "MaxMaterials", 0, needed.Length, maximum, needed.Length > maximum, CapacityDemandLifetime.Inventory)) throw new MapRenderAssetRefusal(MapRenderAssetStatus.CapacityExceeded, "One canonical terrain face exceeds material-set capacity.");
                    int union = current < 0 ? 0 : sets[current].Union(needed).Count();
                    if (current < 0 || DemandDiagnostics.Check(admission, "NativeMapTerrain.MaterialUnion", "MaxMaterials", sets[current].Count, union - sets[current].Count, maximum, union > maximum, CapacityDemandLifetime.Inventory))
                    {
                        admission.ReserveBytes(128);
                        current = sets.Count;
                        sets.Add(new()); localOrdinals.Add(ordinal++);
                    }
                    int added = 0;
                    foreach (ushort id in needed) if (!sets[current].Contains(id)) added++;
                    admission.ReserveBytes(checked(32L * added));
                    sets[current].UnionWith(needed);
                }
                finally { scratch.Release(bytes); }
                MapPreparationCollections.Dictionary(byFace, indexStorage, ref indexBytes);
                byFace.Add(face.Face.Key, current);
            }
        }
        preparation.AdoptWorkspace(indexStorage, indexBytes);
        preparation.Complete(checked(128L + indexBytes + 8L * sets.Capacity + 4L * localOrdinals.Capacity +
            64L * sets.Count + sets.Sum(set => 32L * set.Count)));
        return new(sets, byFace, localOrdinals);
    }
    static MapBox3 Bounds(GltfMesh mesh, MapSubmissionAnchor anchor) => new(
        anchor.X + (double)mesh.Vertices.Min(v => v.Position.X), anchor.Y + (double)mesh.Vertices.Min(v => v.Position.Y),
        anchor.Z + (double)mesh.Vertices.Min(v => v.Position.Z), anchor.X + (double)mesh.Vertices.Max(v => v.Position.X),
        anchor.Y + (double)mesh.Vertices.Max(v => v.Position.Y), anchor.Z + (double)mesh.Vertices.Max(v => v.Position.Z));

    static string Digest(MapTerrainResource resource, string closure, MapSubmissionAnchor anchor)
    {
        using var hash = SHA256.Create();
        using var stream = new CryptoStream(Stream.Null, hash, CryptoStreamMode.Write);
        using (var writer = new Utf8JsonWriter(stream, MapJsonTextOptions.IndentedWriter))
        {
            writer.WriteStartArray(); writer.WriteStringValue("kemap/native-terrain/1"); writer.WriteStringValue(closure);
            writer.WriteNumberValue(anchor.X); writer.WriteNumberValue(anchor.Y); writer.WriteNumberValue(anchor.Z);
            foreach (var layer in resource.Materials.Layers) { writer.WriteNumberValue(layer.MaterialId); writer.WriteStringValue(layer.ResourceId); }
            foreach (var vertex in resource.BorrowMesh.Vertices)
            {
                foreach (float value in new[] { vertex.Position.X, vertex.Position.Y, vertex.Position.Z, vertex.Normal.X,
                    vertex.Normal.Y, vertex.Normal.Z, vertex.Color.X, vertex.Color.Y, vertex.Color.Z, vertex.Color.W,
                    vertex.Uv.X, vertex.Uv.Y, vertex.Tangent.X, vertex.Tangent.Y, vertex.Tangent.Z, vertex.Tangent.W })
                    writer.WriteNumberValue(BitConverter.SingleToInt32Bits(value));
            }
            foreach (uint index in resource.BorrowMesh.Indices32) writer.WriteNumberValue(index);
            foreach (var face in resource.FaceKeys)
            {
                writer.WriteStringValue(face.OwnerId); writer.WriteStringValue(face.Patch?.SurfaceId);
                if (face.Patch is { } patch) { writer.WriteNumberValue(patch.SlotX); writer.WriteNumberValue(patch.SlotZ); }
                writer.WriteNumberValue(face.Primitive); writer.WriteNumberValue(face.ParentTriangle);
                writer.WriteNumberValue(face.Child); writer.WriteNumberValue((int)face.Side);
            }
            writer.WriteEndArray();
        }
        stream.FlushFinalBlock();
        return Convert.ToHexStringLower(hash.Hash!);
    }
}
