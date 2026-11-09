using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.Versioning;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Terrain;
using KhaozEngine.TileWorld;
using static KhaozEngine.Tests.MapDocOracle.ShippedSceneComparison;

namespace KhaozEngine.Tests.MapDocOracle;

internal sealed record SceneSourceFoliageRow(MapFoliageSampleKey Key, GroundCoverInstance Instance);
internal sealed record SceneSourceFoliageGeneration(FoliageLayerBinding Binding, MapFoliageChunkKey Chunk,
    IReadOnlyList<SceneSourceFoliageRow> Rows)
{
    internal string Owner => FormattableString.Invariant($"{Binding.Row.NativeLayerId}/{Chunk.X}/{Chunk.Z}");
    internal MapRenderKey Key => new(MapRenderKind.Foliage, Owner, "instances", 0);
}

/// <summary>Scope-local source roster. No native generations, addresses, rows or bounds select expected work.</summary>
[UnsupportedOSPlatform("windows")]
internal static partial class ShippedSceneFoliageRoster
{
    internal static IReadOnlyList<SceneSourceFoliageGeneration> Capture(ShippedSceneInputs inputs, MapSceneScope scope)
    {
        var result = new List<SceneSourceFoliageGeneration>();
        if (!scope.Channels.HasFlag(MapSceneChannels.Foliage)) return result;
        var b = scope.DrawBounds;
        long examined = 0;
        int allowance = (inputs.Manifest.Limits?.Scene.Preparation?.FoliageContext ?? new MapFoliageContextLimits()).MaxChunkDescriptors;
        if (b.MinX >= b.MaxX || b.MinZ >= b.MaxZ) return result;
        foreach (var binding in inputs.FoliageBindings)
        {
            var layer = binding.Source;
            double pad = scope.MotionPaddingMetres + ModelReach(inputs, layer);
            int x0 = Owner(layer.OriginX, layer.CellSize, Outward(b.MinX - pad, true));
            int x1 = Owner(layer.OriginX, layer.CellSize, MathF.BitDecrement(Outward(b.MaxX + pad, false)));
            int z0 = Owner(layer.OriginZ, layer.CellSize, Outward(b.MinZ - pad, true));
            int z1 = Owner(layer.OriginZ, layer.CellSize, MathF.BitDecrement(Outward(b.MaxZ + pad, false)));
            (x0, x1) = RasterOwners(layer.OriginX, layer.CellSize, layer.Width, x0, x1);
            (z0, z1) = RasterOwners(layer.OriginZ, layer.CellSize, layer.Height, z0, z1);
            var surface = new TileFoliageSurface(inputs.World, inputs.Catalogs, layer);
            for (long z = z0; z <= z1; z++)
                for (long x = x0; x <= x1; x++)
                {
                    var area = new RectArea(Edge(layer.OriginX, layer.CellSize, x), Edge(layer.OriginZ, layer.CellSize, z),
                        Edge(layer.OriginX, layer.CellSize, x + 1), Edge(layer.OriginZ, layer.CellSize, z + 1));
                    if (area.MinX >= area.MaxX || area.MinZ >= area.MaxZ) continue;
                    // Match the legacy sampler's closed raster domain, including its binary32 arithmetic.
                    // Empty on-raster generations still belong to the roster, regardless of density or eligibility.
                    if ((MathF.BitDecrement(area.MaxX) - layer.OriginX) / layer.CellSize < 0 ||
                        (area.MinX - layer.OriginX) / layer.CellSize > layer.Width - 1 ||
                        (MathF.BitDecrement(area.MaxZ) - layer.OriginZ) / layer.CellSize < 0 ||
                        (area.MinZ - layer.OriginZ) / layer.CellSize > layer.Height - 1) continue;
                    if (++examined > allowance) throw new MapSceneCapacityException(nameof(MapFoliageContextLimits.MaxChunkDescriptors), examined, allowance);
                    var decisions = ShippedFoliageComparison.Expected(surface, ShippedFoliageComparison.Settings(layer), area, out _);
                    var rows = decisions.Values.Where(r => r.Decision.Instance.HasValue).Select(r => new SceneSourceFoliageRow(
                        new(new(binding.Row.NativeLayerId, binding.Row.SupportSurfaceId, r.Decision.Candidate.X, r.Decision.Candidate.Z), 1,
                            new("legacy-tile", 1, 0)), r.Decision.Instance!.Value)).ToArray();
                    result.Add(new(binding, new((int)x, (int)z), rows));
                }
        }
        return result;
    }
    static double ModelReach(ShippedSceneInputs inputs, TileFoliageLayer layer)
    {
        double radius = Math.Abs(layer.RootOffset);
        foreach (var model in layer.Archetypes)
        {
            var extent = inputs.SourcePolicy.ModelExtents[model.Id];
            var bounds = extent.BoundsInSourceUnits;
            double x = Math.Max(Math.Abs(bounds.Min.X), Math.Abs(bounds.Max.X));
            double y = Math.Max(Math.Abs(bounds.Min.Y), Math.Abs(bounds.Max.Y));
            double z = Math.Max(Math.Abs(bounds.Min.Z), Math.Abs(bounds.Max.Z));
            // The source extent sphere covers yaw and support-aligned orientation without any native owner input.
            radius = Math.Max(radius, Math.Sqrt(x * x + y * y + z * z) * extent.SourceUnitsToMetres * layer.ScaleMax + Math.Abs(layer.RootOffset));
        }
        return radius;
    }
    static float Edge(float origin, float cell, long index) => (float)(origin + index * (32d * cell));
    static int Owner(float origin, float cell, float value)
    {
        int result = checked((int)Math.Floor(((double)value - origin) / (32d * cell)));
        while (value < Edge(origin, cell, result)) result = checked(result - 1);
        while (value >= Edge(origin, cell, (long)result + 1)) result = checked(result + 1);
        return result;
    }
    static float Outward(double value, bool lower)
    {
        float result = (float)value;
        return lower && result > value ? MathF.BitDecrement(result) : !lower && result < value ? MathF.BitIncrement(result) : result;
    }

    internal static void Compare(ShippedSceneInputs inputs, IReadOnlyList<SceneSourceFoliageGeneration> expected,
        SceneOracleObservation actual, PrivateOracleReport report)
    {
        var generations = actual.Generations.GroupBy(g => (g.LayerId, g.Chunk)).ToDictionary(g => g.Key, g => g.Count());
        var required = expected.ToDictionary(g => (g.Binding.Row.NativeLayerId, g.Chunk));
        CheckResource(report, "scene-foliage-key", generations.Count == required.Count && generations.All(g => g.Value == 1 && required.ContainsKey(g.Key)),
            new { expected = required.Count, actual = generations.Count }, MapRenderKind.Foliage, "generation-set", "inventory");
        var sourceRows = expected.SelectMany(g => g.Rows.Select(r => (Generation: g, Row: r))).ToDictionary(r => r.Row.Key);
        var ids = actual.Identities.GroupBy(k => k).ToDictionary(g => g.Key, g => g.Count());
        CheckResource(report, "scene-foliage-key", sourceRows.Count == actual.Identities.Count && ids.All(k => k.Value == 1 && sourceRows.ContainsKey(k.Key)),
            new { expected = sourceRows.Count, actual = actual.Identities.Count }, MapRenderKind.Foliage, "sample-set", "identities");
        var resources = actual.DrawSet.Resources;
        var originals = sourceRows.Values.Select(r => r.Row.Instance.ModelId).Distinct(StringComparer.Ordinal).ToDictionary(id => id,
            id => KhaozEngine.Render3D.GltfLoader.LoadPartsWithMaterials(inputs.OriginalMeshes[inputs.SourceModelPaths[id]], "attested source foliage model"));
        var comparedModels = new HashSet<(string, MapFoliageModelBinding)>();
        foreach (var generation in expected)
        {
            var group = resources.Where(r => r.Reference.Key == generation.Key && r.Payload is MapFoliageResource).ToArray();
            var rowIds = actual.Identities.Where(k => sourceRows.TryGetValue(k, out var r) && ReferenceEquals(r.Generation, generation)).ToArray();
            bool complete = generations.ContainsKey((generation.Binding.Row.NativeLayerId, generation.Chunk)) && rowIds.Length == generation.Rows.Count;
            CheckResource(report, "scene-foliage-key", complete, new { generation.Owner, expected = generation.Rows.Count, actual = rowIds.Length }, generation.Key, "sample-identities");
            if (generation.Rows.Count == 0)
            {
                CheckResource(report, "scene-foliage-key", group.Length == 0, new { generation.Owner }, generation.Key, "resource-count");
                continue;
            }
            CheckResource(report, "scene-foliage-key", group.Length == 1, new { generation.Owner }, generation.Key, "resource-count");
            if (group.Length != 1) continue;
            var payload = (MapFoliageResource)group[0].Payload;
            if (payload.TransformSpace != MapFoliageTransformSpace.World)
            { CheckResource(report, "scene-foliage", false, new { generation.Owner }, generation.Key, "transform-space"); continue; }
            var transforms = payload.WorldTransforms.ToArray(); var indices = payload.ModelIndices.ToArray(); var ranks = payload.Ranks.ToArray();
            CheckResource(report, "scene-foliage-key", transforms.Length == generation.Rows.Count && rowIds.Length == transforms.Length,
                new { generation.Owner, expected = generation.Rows.Count, actual = transforms.Length }, generation.Key, "row-count");
            MapBox3? bounds = null;
            foreach (var row in generation.Rows)
            {
                var extent = inputs.SourcePolicy.ModelExtents[row.Instance.ModelId];
                var next = ShippedSceneSourceChecks.Bounds(extent, row.Instance.Transform);
                bounds = bounds?.Union(next) ?? next;
            }
            CheckResource(report, "scene-foliage-bounds", bounds is { } want && ShippedSceneSourceChecks.BoundsNear(want, group[0].Bounds), new { generation.Owner }, generation.Key, "bounds");
            for (int i = 0; i < Math.Min(transforms.Length, rowIds.Length); i++)
            {
                var row = sourceRows[rowIds[i]].Row.Instance; var model = payload.Models[indices[i]];
                CheckResource(report, "scene-foliage", generation.Binding.Models[row.ModelId] == model.ModelId,
                    new { generation.Owner, row = i }, generation.Key, "model-binding");
                CheckResource(report, "scene-foliage", Bits(row.Transform, transforms[i]), new { generation.Owner, row = i }, generation.Key, "transform");
                CheckResource(report, "scene-foliage", Bits(row.ThinningRank, ranks[i]), new { generation.Owner, row = i }, generation.Key, "rank");
                if (comparedModels.Add((row.ModelId, model))) CheckModel(inputs, row.ModelId, model, originals[row.ModelId], resources, report);
            }
        }
    }
    internal static void CheckModel(ShippedSceneInputs inputs, string sourceModel, MapFoliageModelBinding model,
        IReadOnlyList<KhaozEngine.Render3D.GltfMeshPart> parts, IReadOnlyList<MapPreparedResource> resources, PrivateOracleReport report)
    {
        CheckResource(report, "scene-foliage-registration", parts.Count == model.Parts.Count, new { model.ModelId }, MapRenderKind.Foliage, "model", "part-count");
        for (int i = 0; i < Math.Min(parts.Count, model.Parts.Count); i++)
        {
            var found = resources.Where(r => r.Reference == model.Parts[i]).ToArray();
            var key = new MapRenderKey(MapRenderKind.Foliage, model.ModelId, "model-part/" + i, 0);
            CheckResource(report, "scene-foliage-registration", model.Parts[i].Key == key && found.Length == 1 && found[0].Payload is MapRigidResource,
                new { model.ModelId, part = i }, key, "registration");
            if (found.Length != 1 || found[0].Payload is not MapRigidResource mesh) continue;
            CheckResource(report, "scene-foliage-registration", MeshEqual(parts[i].Mesh, mesh.Mesh), new { model.ModelId, part = i }, key, "mesh");
            CheckResource(report, "scene-foliage-registration", MapsEqual(parts[i].Maps, mesh.Maps), new { model.ModelId, part = i }, key, "material-maps");
        }
        var stable = GroundCoverModelPolicy.Resolve(inputs.SourcePolicy.Foliage, sourceModel);
        CheckResource(report, "scene-foliage-registration", Bits(stable.Motion, model.Motion) && stable.CastShadows == model.CastShadows, new { model.ModelId }, MapRenderKind.Foliage, "model", "policy");
    }
}
