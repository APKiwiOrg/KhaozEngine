using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.Versioning;
using KhaozEngine.MapDoc.Foliage;
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
    internal static void Policy(ShippedSceneInputs inputs, PrivateOracleReport report)
    {
        var a = inputs.SourcePolicy; var b = inputs.NativePolicy;
        string[] Mapped(IEnumerable<string> ids) => ids.SelectMany(id => inputs.AssetMappings.TryGetValue(id, out var values) ? values : [id]).Distinct(StringComparer.Ordinal).Order(StringComparer.Ordinal).ToArray();
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

    internal static void Terrain(ShippedSceneInputs inputs, MapRenderPlan plan, PrivateOracleReport report)
    {
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
                    if (!whollyInside) continue;
                    var normal = Vector3.Normalize(Vector3.Cross(b - a, c - a));
                    int x = (int)Math.Floor(((double)a.X + b.X + c.X) / 3), z = (int)Math.Floor(-((double)a.Z + b.Z + c.Z) / 3);
                    var matches = (faces.GetValueOrDefault((x, z)) ?? []).Where(f => SameTriangle(a, b, c, f.Triangle)).ToArray();
                    Check(report, "scene-terrain", matches.Length == 1 && matches.All(f => Math.Abs(f.Face.Normal.X - normal.X) <= NormalTolerance && Math.Abs(f.Face.Normal.Y - normal.Y) <= NormalTolerance && Math.Abs(f.Face.Normal.Z - normal.Z) <= NormalTolerance), new { region, plane, triangle = i / 3 });
                    foreach (var face in matches)
                    {
                        ushort underlay = inputs.World.GetUnderlay(x, z, plane), overlay = inputs.World.GetOverlay(x, z, plane);
                        Check(report, "scene-terrain-material", face.Paint.Underlay == underlay && face.Paint.Overlay == overlay, new { region, plane, x, z });
                    }
                }
            }
        foreach (var resource in plan.Inventory.Terrain.Partitions)
            Check(report, "scene-terrain-material", resource.Paint.Underlay == 0 || inputs.Manifest.Materials.Any(m => m.SourceMaterialId == resource.Paint.Underlay), new { resource.Face });
        var recorder = new MapDrawRecorder(); plan.Register(recorder);
        foreach (var payload in recorder.Snapshot().Resources.Select(r => r.Payload).OfType<MapTerrainResource>())
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
            Check(report, "scene-placement-part", part?.Payload is MapRigidResource mesh && MeshEqual(source.Tiers[0][i].Mesh, mesh.Mesh), new { owner, part = i });
        }
    }

    internal static void Foliage(ShippedSceneInputs inputs, MapRenderPlan plan, PrivateOracleReport report)
    {
        var recorder = new MapDrawRecorder(); plan.Register(recorder);
        var resources = recorder.Snapshot().Resources;
        var registered = resources.ToDictionary(r => r.Reference);
        var sourceParts = new Dictionary<string, IReadOnlyList<KhaozEngine.Render3D.GltfMeshPart>>(StringComparer.Ordinal);
        int offset = 0;
        foreach (var generation in plan.Inventory.Foliage.GenerationKeys)
        {
            var binding = inputs.FoliageBindings.Single(b => b.Row.NativeLayerId == generation.LayerId);
            string owner = FormattableString.Invariant($"{generation.LayerId}/{generation.Chunk.X}/{generation.Chunk.Z}");
            var resource = resources.SingleOrDefault(r => r.Reference.Key.OwnerId == owner && r.Payload is MapFoliageResource);
            if (resource?.Payload is not MapFoliageResource payload) continue;
            var transforms = payload.WorldTransforms.ToArray(); var indices = payload.ModelIndices.ToArray(); var ranks = payload.Ranks.ToArray();
            var sampler = new TileFoliageSurface(inputs.World, inputs.Catalogs, binding.Source);
            var evaluator = GroundCoverDistribution.Prepare(ShippedFoliageComparison.Settings(binding.Source));
            KhaozEngine.MapDoc.Editing.MapBox3? sourceBounds = null;
            for (int i = 0; i < transforms.Length; i++)
            {
                if (offset + i >= plan.Inventory.Foliage.GeneratedIdentities.Count)
                { Check(report, "scene-foliage-key", false, new { owner, row = i }); continue; }
                var key = plan.Inventory.Foliage.GeneratedIdentities[offset + i];
                var decision = evaluator.Evaluate(key.Address.X, key.Address.Z, c => new(sampler.Sample(c.WorldX, c.WorldZ)));
                Check(report, "scene-foliage-key", key.Address.LayerId == binding.Row.NativeLayerId && key.Address.SupportOwnerId == binding.Row.SupportSurfaceId && key.DistributionVersion == 1 && key.Policy.Id == "legacy-tile" && key.Policy.Version == 1, new { owner, row = i });
                if (decision.Instance is not { } source)
                { Check(report, "scene-foliage", false, new { owner, row = i }); continue; }
                Check(report, "scene-foliage", binding.Models[source.ModelId] == payload.Models[indices[i]].ModelId && Bits(source.Transform, transforms[i]) && Bits(source.ThinningRank, ranks[i]), new { owner, row = i });
                if (inputs.SourcePolicy.ModelExtents.TryGetValue(source.ModelId, out var extent))
                {
                    var bounds = Bounds(extent, source.Transform);
                    sourceBounds = sourceBounds?.Union(bounds) ?? bounds;
                }
                var model = payload.Models[indices[i]];
                if (!sourceParts.TryGetValue(source.ModelId, out var original))
                {
                    original = KhaozEngine.Render3D.GltfLoader.LoadPartsWithMaterials(inputs.OriginalMeshes[inputs.SourceModelPaths[source.ModelId]], "attested source foliage model");
                    sourceParts.Add(source.ModelId, original);
                }
                Check(report, "scene-foliage-registration", original.Count == model.Parts.Count, new { owner, row = i });
                for (int partIndex = 0; partIndex < Math.Min(original.Count, model.Parts.Count); partIndex++)
                {
                    var part = registered.GetValueOrDefault(model.Parts[partIndex]);
                    Check(report, "scene-foliage-registration", part?.Payload is MapRigidResource mesh && MeshEqual(original[partIndex].Mesh, mesh.Mesh), new { owner, partIndex });
                }
                var stable = GroundCoverModelPolicy.Resolve(inputs.SourcePolicy.Foliage, source.ModelId);
                Check(report, "scene-foliage-registration", Bits(stable.Motion, model.Motion) && stable.CastShadows == model.CastShadows, new { owner, row = i });
            }
            Check(report, "scene-foliage-bounds", sourceBounds is { } expectedBounds && BoundsNear(expectedBounds, resource.Bounds), new { owner });
            offset += transforms.Length;
        }
        Check(report, "scene-foliage-key", offset == plan.Inventory.Foliage.GeneratedIdentities.Count, new { offset, keys = plan.Inventory.Foliage.GeneratedIdentities.Count });
    }
    static KhaozEngine.MapDoc.Editing.MapBox3 Bounds(MapFoliageModelExtent extent, Matrix4x4 transform)
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
    static bool BoundsNear(KhaozEngine.MapDoc.Editing.MapBox3 a, KhaozEngine.MapDoc.Editing.MapBox3 b) =>
        new[] { a.MinX - b.MinX, a.MinY - b.MinY, a.MinZ - b.MinZ, a.MaxX - b.MaxX, a.MaxY - b.MaxY, a.MaxZ - b.MaxZ }.All(d => Math.Abs(d) <= GeometryTolerance);

    internal static void DynamicFrames(ShippedSceneInputs inputs, LegacySceneFrame expected, SceneProbe probe, MapDrawSet actual, PrivateOracleReport report)
    {
        var options = inputs.SourcePolicy.Foliage;
        var settings = inputs.SourcePolicy.Water.ResolveInto(new KhaozEngine.Render3D.WaterSettings(), new KhaozEngine.Render3D.WaterSettings());
        var resolvedLook = new KhaozEngine.Render3D.WaterLook();
        foreach (var property in typeof(KhaozEngine.Render3D.WaterLook).GetFields(System.Reflection.BindingFlags.Public | System.Reflection.BindingFlags.Instance))
        {
            object? value = typeof(KhaozEngine.Render3D.WaterSettings).GetProperty(property.Name)?.GetValue(settings)
                ?? typeof(KhaozEngine.Render3D.WaterSettings).GetField(property.Name)?.GetValue(settings);
            property.SetValue(resolvedLook, value ?? throw new InvalidOperationException("unknown source water policy member"));
        }
        var water = MapFrozenWaterLook.Capture(resolvedLook);
        foreach (var draw in actual.Draws)
        {
            var f = expected.Focus;
            float Local(short frame, float value, long anchor) => new MapExactValue(checked(frame * 128L - anchor), 1).Add(MapExactValue.FromSingle(value)).ToSingle();
            var focus = new Vector3(Local(f.Frame.X, f.Local.X, draw.Anchor.X), Local(0, f.Local.Y, draw.Anchor.Y), Local(f.Frame.Z, f.Local.Z, draw.Anchor.Z));
            Check(report, "scene-focus", Bits(focus, draw.LocalFocus), new { draw.Resource.Key });
            if (draw.Water is { } actualWater) Check(report, "scene-water-frame", Bits(water, actualWater.Look), new
            {
                draw.Resource.Key,
                differences = typeof(MapFrozenWaterLook).GetProperties().Where(p => !Equals(p.GetValue(water), p.GetValue(actualWater.Look)))
                    .Select(p => new { p.Name, expected = p.GetValue(water), actual = p.GetValue(actualWater.Look) })
            });
            if (draw.Foliage is not { } foliage) continue;
            bool explicitlyPlaced = inputs.Manifest.Placements.Any(p => p.NativePlacementId == draw.Resource.Key.OwnerId);
            var wanted = new MapFoliageFrameOptions(explicitlyPlaced ? inputs.SourcePolicy.Placements.DefaultDrawRadius : options.DrawRadius,
                options.DensityRadius, options.FadeBandWidth, explicitlyPlaced ? 1 : options.QualityDensity, explicitlyPlaced ? 1 : options.DistantDensity,
                options.InstanceFadeBandWidth, options.FadeMode, options.WindFadeBladePixels);
            KhaozEngine.Render3D.FoliageInteractor Interactor(int i)
            {
                if (i >= options.Interactors.Count) return default;
                var item = options.Interactors[i];
                return item with { Position = new((float)((double)item.Position.X - draw.Anchor.X), (float)((double)item.Position.Y - draw.Anchor.Y), (float)((double)item.Position.Z - draw.Anchor.Z)) };
            }
            var state = new MapFoliageFrameState(options.WindDirection, options.WindStrength, options.WindSpeed, options.WindSpatialFrequency,
                options.Interactors.Count, Interactor(0), Interactor(1), Interactor(2), Interactor(3));
            Check(report, "scene-foliage-frame", Bits(wanted, foliage.Options) && Bits(state, foliage.State) && Bits(probe.TimeSeconds, foliage.TimeSeconds) && probe.WindSeed == foliage.WindSeed, new { draw.Resource.Key });
        }
    }

    internal static void LightFrames(ShippedSceneInputs inputs, LegacySceneInventory source, MapDrawSet actual, PrivateOracleReport report)
    {
        foreach (var draw in actual.Draws.Where(d => d.Light.HasValue))
        {
            var correspondence = inputs.Manifest.Lights.Single(l => l.NativeLightId == draw.Resource.Key.OwnerId);
            var expected = source.Lights.Single(l => l.SourceObjectId == correspondence.SourceObjectId && l.SourceProfileId == correspondence.SourceProfileId);
            var frame = draw.Light!.Value;
            string owner = inputs.Manifest.Placements.Single(p => p.SourceObjectId == expected.SourceObjectId).NativePlacementId;
            Check(report, "scene-light-frame", frame.FixtureOwnerId == owner && frame.Shadow.Requested == expected.CastShadows &&
                Bits(frame.Colour, new KhaozEngine.Primitives.Color(expected.Colour.X, expected.Colour.Y, expected.Colour.Z)) && Bits(frame.Radius, expected.Radius) && Bits(frame.Intensity, expected.Intensity), new { correspondence.NativeLightId });
            if (!expected.CastShadows) continue;
            var placement = source.Placements.Single(p => p.Id == expected.SourceObjectId);
            var local = placement.World;
            local.M41 = (float)((double)local.M41 - draw.Anchor.X); local.M42 = (float)((double)local.M42 - draw.Anchor.Y); local.M43 = (float)((double)local.M43 - draw.Anchor.Z);
            var min = new Vector3(float.PositiveInfinity); var max = new Vector3(float.NegativeInfinity);
            foreach (var vertex in placement.Tiers.SelectMany(t => t).SelectMany(p => p.Mesh.Vertices))
            { var point = Vector3.Transform(vertex.Position, local); min = Vector3.Min(min, point); max = Vector3.Max(max, point); }
            var shadow = KhaozEngine.Render3D.LightShadow.Static(1).WithExclusionBox(min, max);
            Check(report, "scene-light-frame", shadow.HasExclusionBox == frame.Shadow.HasExclusionBox && (!shadow.HasExclusionBox ||
                Vector3.Distance(shadow.ExclusionMin, frame.Shadow.ExclusionMin) <= GeometryTolerance && Vector3.Distance(shadow.ExclusionMax, frame.Shadow.ExclusionMax) <= GeometryTolerance), new { correspondence.NativeLightId });
        }
    }

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
