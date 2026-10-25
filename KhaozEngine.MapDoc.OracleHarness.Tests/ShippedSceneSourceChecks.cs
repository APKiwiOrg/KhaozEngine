using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.Versioning;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Identity;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.MapDoc.Water;
using KhaozEngine.Terrain;
using KhaozEngine.Tests.MapDocCompatibility;
using KhaozEngine.TileWorld;
using static KhaozEngine.Tests.MapDocOracle.ShippedSceneComparison;

namespace KhaozEngine.Tests.MapDocOracle;

/// <summary>Expected geometry and sample inputs come from released source routes, never from a native recorder.</summary>
[UnsupportedOSPlatform("windows")]
internal static class ShippedSceneSourceChecks
{
    internal static void Storage(MapDocument tiled, MapDocument? monolithic, MapAssetClosure assets)
    {
        ShippedFoliageManifest.Require(tiled.FormatVersion == 9 && tiled.Tiles is { IsPartial: false },
            "scene comparison requires a complete format nine tiled document");
        if (monolithic is null) return;
        ShippedFoliageManifest.Require(monolithic.FormatVersion == 9 && monolithic.Tiles is null,
            "scene comparison requires a complete format nine document");
        var options = new MapResolveOptions("shipped-scene-storage", 1, "authored", 2);
        ShippedFoliageManifest.Require(MapAuthoredIdentityV2.Compute(tiled, assets, options) ==
            MapAuthoredIdentityV2.Compute(monolithic, assets, options), "scene storage forms disagree");
    }

    internal static void Policy(ShippedSceneInputs inputs, PrivateOracleReport report)
    {
        var a = inputs.SourcePolicy; var b = inputs.NativePolicy;
        string[] Mapped(IEnumerable<string> ids) => ids.SelectMany(id => inputs.AssetMappings.TryGetValue(id, out var values) ? values
            : throw new System.IO.InvalidDataException("missing scene asset correspondence")).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
        Check(report, "scene-policy", Bits(a.Placements.DefaultDrawRadius, b.Placements.DefaultDrawRadius) && Bits(a.Water, b.Water) && Bits(a.Lights, b.Lights), new { policy = "placement and water" });
        Check(report, "scene-policy", Mapped(a.AnimatedAssets).SequenceEqual(b.AnimatedAssets.Order(StringComparer.Ordinal)), new { policy = "animated selection" });
        Check(report, "scene-policy", a.Placements.Layers.Count == b.Placements.Layers.Count, new { policy = "layer count" });
        foreach (var layer in a.Placements.Layers)
        {
            var native = b.Placements.Layers.SingleOrDefault(l => l.Id == layer.Id);
            Check(report, "scene-policy", native is not null && Mapped(layer.AssetIds).SequenceEqual(native.AssetIds.Order(StringComparer.Ordinal)) &&
                Bits(layer.LodDistances, native.LodDistances) && Bits(layer.LodCrossfadeWidth, native.LodCrossfadeWidth) && Bits(layer.DrawRadius, native.DrawRadius) &&
                Bits(layer.HlodDistance, native.HlodDistance) && Bits(layer.HlodCrossfadeWidth, native.HlodCrossfadeWidth) && Bits(layer.HlodWeldCell, native.HlodWeldCell) && layer.CastsShadows == native.CastsShadows, new { layer.Id });
        }
        var source = System.Text.Json.JsonSerializer.SerializeToNode(a.Foliage, ShippedScenePolicy.Json)!.AsObject();
        foreach (string property in new[] { "motionProfiles", "modelCastsShadows" })
        {
            var mapped = new System.Text.Json.Nodes.JsonObject();
            foreach (var pair in source[property]!.AsObject()) foreach (string id in Mapped([pair.Key])) mapped[id] = pair.Value?.DeepClone();
            source[property] = mapped;
        }
        foreach (var extent in a.ModelExtents.Values)
            foreach (string id in Mapped([extent.AssetId]))
            {
                var found = b.ModelExtents.GetValueOrDefault(id);
                bool same = found is not null && Bits(extent.MotionPaddingMetres, found.MotionPaddingMetres);
                if (found is not null)
                    foreach (var pair in new[] { (extent.BoundsInSourceUnits.Min, found.BoundsInSourceUnits.Min), (extent.BoundsInSourceUnits.Max, found.BoundsInSourceUnits.Max) })
                        same &= Vector3.Distance(pair.Item1 * extent.SourceUnitsToMetres, pair.Item2 * found.SourceUnitsToMetres) <= GeometryTolerance;
                Check(report, "scene-foliage-bounds", same, new { model = extent.AssetId });
            }
        var nativeOptions = System.Text.Json.JsonSerializer.SerializeToNode(b.Foliage, ShippedScenePolicy.Json);
        Check(report, "scene-policy", System.Text.Json.Nodes.JsonNode.DeepEquals(source, nativeOptions), new { policy = "foliage options" });
    }

    internal static IReadOnlyDictionary<MapRenderKey, MapSubmissionAnchor> Terrain(ShippedSceneInputs inputs, MapRenderPlan plan, PrivateOracleReport report,
        out IReadOnlyDictionary<MapFaceKey, SceneTerrainFace> sourceFaces)
    {
        var expectedFaces = new Dictionary<MapFaceKey, SceneTerrainFace>(); sourceFaces = expectedFaces;
        var recorder = new MapDrawRecorder(); plan.Register(recorder);
        var registrations = recorder.Snapshot().Resources;
        var partsByFace = registrations.Where(r => r.Payload is MapTerrainResource).SelectMany(r =>
            ((MapTerrainResource)r.Payload).FaceKeys.Select(face => (Face: face, Resource: r.Reference.Key)))
            .ToLookup(p => p.Face, p => p.Resource);
        var required = new Dictionary<MapRenderKey, MapSubmissionAnchor>();
        var faces = plan.Inventory.Terrain.CanonicalFaces.GroupBy(f =>
            ((int)Math.Floor((f.Triangle.A.X.ToDouble() + f.Triangle.B.X.ToDouble() + f.Triangle.C.X.ToDouble()) / 3),
             (int)Math.Floor(-(f.Triangle.A.Z.ToDouble() + f.Triangle.B.Z.ToDouble() + f.Triangle.C.Z.ToDouble()) / 3)))
            .ToDictionary(g => g.Key, g => g.ToArray());
        foreach (var region in inputs.World.Regions.Keys)
            for (int plane = 0; plane < inputs.World.PlaneCount; plane++)
            {
                var source = TileGroundTriangles.Build(inputs.World, region, plane);
                var origin = new Vector3(region.OriginX, 0, -region.OriginZ);
                for (int i = 0; i < source.Indices.Length; i += 3)
                {
                    Vector3 a = origin + source.Positions[source.Indices[i]], b = origin + source.Positions[source.Indices[i + 1]], c = origin + source.Positions[source.Indices[i + 2]];
                    var bounds = plan.Scene.Scope.DrawBounds;
                    bool whollyInside = new[] { a, b, c }.All(v => v.X >= bounds.MinX && v.X <= bounds.MaxX && v.Z >= bounds.MinZ && v.Z <= bounds.MaxZ && v.Y >= bounds.MinY && v.Y <= bounds.MaxY);
                    var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
                    int x = (int)Math.Floor(((double)a.X + b.X + c.X) / 3), z = (int)Math.Floor(-((double)a.Z + b.Z + c.Z) / 3);
                    var matches = (faces.GetValueOrDefault((x, z)) ?? []).Where(f => SameTriangle(a, b, c, f.Triangle)).ToArray();
                    if (!whollyInside && matches.Length == 0) continue;
                    Check(report, "scene-terrain", matches.Length == 1 && matches.All(f => Math.Abs(f.Face.Normal.X - normal.X) <= NormalTolerance && Math.Abs(f.Face.Normal.Y - normal.Y) <= NormalTolerance && Math.Abs(f.Face.Normal.Z - normal.Z) <= NormalTolerance), new { region, plane, triangle = i / 3 });
                    foreach (var face in matches)
                    {
                        expectedFaces.Add(face.Face.Key, ShippedSceneTerrainCoverage.Source(inputs.World, a, b, c, x, z, plane));
                        var parts = partsByFace[face.Face.Key].ToArray();
                        Check(report, "scene-terrain-frame", parts.Length > 0, new { face.Face.Key });
                        foreach (var part in parts) required[part] = face.Anchor;
                        ushort underlay = inputs.World.GetUnderlay(x, z, plane), overlay = inputs.World.GetOverlay(x, z, plane);
                        Check(report, "scene-terrain-material", face.Paint.Underlay == underlay && face.Paint.Overlay == overlay, new { region, plane, x, z });
                    }
                }
            }
        foreach (var resource in plan.Inventory.Terrain.Partitions)
            Check(report, "scene-terrain-material", resource.Paint.Underlay == 0 || inputs.Manifest.Materials.Any(m => m.SourceMaterialId == resource.Paint.Underlay), new { resource.Face });
        foreach (var payload in registrations.Select(r => r.Payload).OfType<MapTerrainResource>())
            foreach (var layer in payload.Materials.Layers)
            {
                var source = inputs.Catalogs.Material(layer.MaterialId);
                var correspondence = inputs.Manifest.Materials.SingleOrDefault(m => m.SourceMaterialId == layer.MaterialId);
                bool same = source is not null && correspondence?.NativeResourceId == layer.ResourceId;
                if (source?.Texture is { } texture)
                {
                    string path = System.IO.Path.GetFullPath(System.IO.Path.Combine(System.IO.Path.GetDirectoryName(inputs.Catalogs.MaterialSource(source.Id)!)!, texture));
                    var original = inputs.SourceTexture(path); var actual = layer.Albedo;
                    same &= actual is { } image && image.Width == original.Width && image.Height == original.Height && image.Rgba.SequenceEqual(original.Pixels) && Bits(layer.Tint, Vector4.One);
                }
                else if (source is not null) same &= Bits(TileColors.Parse(source.Color), layer.Tint);
                Check(report, "scene-terrain-material", same, new { layer.MaterialId });
            }
        return required;
    }
    internal static long TerrainFaces(TileWorldDocument world) => world.Regions.Keys.Sum(region => Enumerable.Range(0, world.PlaneCount).Sum(plane => (long)TileGroundTriangles.Build(world, region, plane).Indices.Length / 3));
    static bool SameTriangle(Vector3 a, Vector3 b, Vector3 c, MapExactTriangle actual) =>
        Same(a, actual.A) && Same(b, actual.B) && Same(c, actual.C) || Same(a, actual.B) && Same(b, actual.C) && Same(c, actual.A) || Same(a, actual.C) && Same(b, actual.A) && Same(c, actual.B);
    static bool Same(Vector3 a, MapExactPoint b) => Math.Abs(a.X - b.X.ToDouble()) <= GeometryTolerance && Math.Abs(a.Y - b.Y.ToDouble()) <= GeometryTolerance && Math.Abs(a.Z - b.Z.ToDouble()) <= GeometryTolerance;

    internal static void ExplicitPlant(LegacyScenePlacement source, string owner, IReadOnlyList<MapPreparedResource> resources, ShippedSceneInputs inputs, PrivateOracleReport report)
    {
        var registration = resources.SingleOrDefault(r => r.Reference.Key.OwnerId == owner && r.Payload is MapFoliageResource);
        Check(report, "scene-foliage", registration?.Payload is MapFoliageResource, new { owner });
        if (registration?.Payload is not MapFoliageResource payload) return;
        if (payload.TransformSpace != MapFoliageTransformSpace.AnchorLocal)
        { Check(report, "scene-foliage", false, new { owner }); return; }
        var transforms = payload.AnchorLocalTransforms.ToArray();
        Check(report, "scene-foliage", payload.TransformSpace == MapFoliageTransformSpace.AnchorLocal && transforms.Length == 1 && payload.ModelIndices.Length == 1 && payload.Models.Count == 1, new { owner });
        if (transforms.Length != 1 || payload.Models.Count != 1) return;
        var world = transforms[0] * Matrix4x4.CreateTranslation((float)payload.Anchor.X, (float)payload.Anchor.Y, (float)payload.Anchor.Z);
        Check(report, "scene-transform", MatrixNear(world, source.World), new { owner });
        var stable = GroundCoverModelPolicy.Resolve(inputs.SourcePolicy.Foliage, source.ArchetypeId);
        Check(report, "scene-foliage-registration", Bits(stable.Motion, payload.Models[0].Motion) && stable.CastShadows == payload.Models[0].CastShadows, new { owner });
        var parts = payload.Models[0].Parts;
        Check(report, "scene-placement-part", parts.Count == source.Tiers[0].Count, new { owner });
        for (int i = 0; i < Math.Min(parts.Count, source.Tiers[0].Count); i++)
        {
            var part = resources.SingleOrDefault(r => r.Reference == parts[i]);
            Check(report, "scene-foliage-registration", part?.Payload is MapRigidResource mesh && MeshEqual(source.Tiers[0][i].Mesh, mesh.Mesh) && MapsEqual(source.Tiers[0][i].Maps, mesh.Maps), new { owner, part = i });
        }
    }

    internal static KhaozEngine.MapDoc.Editing.MapBox3 Bounds(MapFoliageModelExtent extent, Matrix4x4 transform)
    {
        var min = new Vector3(float.PositiveInfinity); var max = new Vector3(float.NegativeInfinity);
        for (int corner = 0; corner < 8; corner++)
        {
            var a = extent.BoundsInSourceUnits;
            var p = Vector3.Transform(new Vector3((corner & 1) == 0 ? a.Min.X : a.Max.X,
                (corner & 2) == 0 ? a.Min.Y : a.Max.Y, (corner & 4) == 0 ? a.Min.Z : a.Max.Z) * extent.SourceUnitsToMetres, transform);
            min = Vector3.Min(min, p); max = Vector3.Max(max, p);
        }
        double pad = extent.MotionPaddingMetres;
        return new(min.X - pad, min.Y - pad, min.Z - pad, max.X + pad, max.Y + pad, max.Z + pad);
    }
    internal static bool BoundsNear(KhaozEngine.MapDoc.Editing.MapBox3 a, KhaozEngine.MapDoc.Editing.MapBox3 b) =>
        new[] { a.MinX - b.MinX, a.MinY - b.MinY, a.MinZ - b.MinZ, a.MaxX - b.MaxX, a.MaxY - b.MaxY, a.MaxZ - b.MaxZ }.All(d => Math.Abs(d) <= GeometryTolerance);

    internal static void Water(ShippedSceneInputs inputs, MapRenderPlan plan, PrivateOracleReport report)
    {
        var bodies = new Dictionary<string, TileWaterBody>(StringComparer.Ordinal);
        foreach (var region in inputs.World.Regions.Keys)
            for (int plane = 0; plane < inputs.World.PlaneCount; plane++)
            {
                var source = TileWaterBodies.Collect(inputs.World, inputs.Catalogs, region, plane);
                for (int i = 0; i < source.Count; i++) bodies.Add(LegacyWaterConverter.BodyId(region, plane, i), source[i]);
            }
        foreach (var cell in plan.Inventory.Water.Cells)
        {
            var mapping = inputs.Manifest.Water.SingleOrDefault(w => w.NativeBodyId == cell.Key.BodyId);
            bool bound = mapping is not null && bodies.TryGetValue(mapping.SourceBodyKey, out _);
            Check(report, "scene-water", bound, new { cell.Key });
            if (!bound) continue;
            var body = bodies[mapping!.SourceBodyKey];
            Check(report, "scene-water", Bits(cell.SurfaceY, body.SurfaceY) && mapping.NativeSpaceIds.Contains(cell.Key.SpaceId, StringComparer.Ordinal), new { cell.Key });
            foreach (var region in cell.Regions)
            {
                Check(report, "scene-water", region.Kind != MapWaterRegionKind.Unresolved, new { cell.Key, region.Kind });
                foreach (var triangle in region.Triangles)
                {
                    double x = (triangle.A.X.ToDouble() + triangle.B.X.ToDouble() + triangle.C.X.ToDouble()) / 3;
                    double z = -(triangle.A.Z.ToDouble() + triangle.B.Z.ToDouble() + triangle.C.Z.ToDouble()) / 3;
                    bool inside = body.Rects.Any(rect => x >= rect.X && x <= rect.X + rect.Width && z >= rect.Z && z <= rect.Z + rect.Height);
                    Check(report, "scene-water", inside, new { cell.Key });
                }
            }
        }
    }
}
