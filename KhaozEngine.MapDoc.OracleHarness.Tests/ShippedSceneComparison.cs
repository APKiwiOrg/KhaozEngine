using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Runtime.Versioning;
using System.Text.Json;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Render3D;
using static KhaozEngine.Tests.MapDocOracle.ShippedFoliageManifest;

namespace KhaozEngine.Tests.MapDocOracle;

/// <summary>Full typed comparisons. Digests are transport evidence, never a substitute for payload inspection.</summary>
[UnsupportedOSPlatform("windows")]
internal static partial class ShippedSceneComparison
{
    internal const double GeometryTolerance = .00001, NormalTolerance = .000001, AngleTolerance = .000001;
    internal static void Compare(ShippedSceneInputs inputs, PrivateOracleReport report,
        Func<SceneOracleObservation, SceneOracleObservation>? observe = null,
        Func<IMapDrawSink, IMapDrawSink>? decorate = null)
    {
        ShippedSceneSourceChecks.Policy(inputs, report);
        var expected = LegacySceneRecorder.Record(inputs.World, inputs.Catalogs, inputs);
        var manifest = inputs.Manifest;
        var counts = manifest.Expectations;
        Check(report, "scene-expectation", counts.TerrainFaces == ShippedSceneSourceChecks.TerrainFaces(inputs.World) && counts.Placements == expected.Placements.Count && counts.Parts == expected.Placements.Sum(p => p.Tiers.Sum(t => t.Count)) &&
            counts.Lods == expected.Placements.Sum(p => p.Tiers.Count) && counts.Lights == expected.Lights.Count && counts.Roofs == expected.Placements.Count(p => p.Roof) &&
            counts.WaterRegions == expected.Water.Count && counts.FoliageSamples == expected.Foliage.Values.Sum() && counts.Windows == manifest.Windows.Count && counts.SequenceSteps == manifest.Sequence.Count,
            new { counts });
        if (inputs.Buildings is { } buildings)
        {
            var result = ShippedBuildingComparison.Compare(buildings);
            report.Record("scene-buildings", result.DetailJson);
            report.Check("scene-building", result.Failures.Count == 0, result.DetailJson);
        }
        if (inputs.Markers is { } markers)
        {
            var result = ShippedMarkerComparison.Compare(markers);
            report.Record("scene-markers", result.DetailJson);
            report.Check("scene-marker", result.Failures.Count == 0, result.DetailJson);
        }
        var heads = new Dictionary<(string Window, string Probe), string>();
        int compared = 0;
        foreach (bool tiled in manifest.NativeDocumentPath is null ? new[] { true } : new[] { true, false })
            for (int load = 0; load < 2; load++)
            {
                var document = inputs.Reload(tiled); var closure = inputs.Assets(document);
                using var documentSource = MapDocumentSource.FromDocument(document);
                var capture = CaptureSource(documentSource, closure, manifest);
                Require(capture.Source is not null, "scene source capture refused " + string.Join("\n", capture.Findings));
                var scratch = new GltfDecodeBudget(manifest.Limits?.Decode ?? new());
                var proof = manifest.Limits?.MaxProofBytes is { } proofBytes
                    ? MapRenderExtentValidator.Validate(closure, capture.Source!.RequiredAssetIds, scratch, proofBytes)
                    : MapRenderExtentValidator.Validate(closure, capture.Source!.RequiredAssetIds, scratch);
                Require(proof.Proofs is not null, "scene extent proof refused " + proof.Detail);
                var bound = capture.Source.WithRenderExtents(proof.Proofs!);
                Require(bound.Source is not null, "scene extent binding refused " + string.Join("\n", bound.Findings));
                var source = bound.Source!;
                var policy = inputs.NativePolicy.Native(closure, manifest);
                var windows = manifest.Windows.ToDictionary(w => w.Id, w => ShippedSceneInputs.Scope(w, manifest.Limits?.Queries), StringComparer.Ordinal);
                var completeWindows = new Dictionary<string, IReadOnlyDictionary<MapRenderKey, string>>(StringComparer.Ordinal);
                foreach (var window in manifest.Windows)
                {
                    var acquired = source.Acquire(windows[window.Id]);
                    Check(report, "scene-completeness", acquired.Scene is not null && acquired.Witness.CompleteForScope && acquired.Witness.Unavailable.Count == 0, new { window.Id, acquired.Status, acquired.Capacity, acquired.Findings });
                    CheckCoverage(acquired.Witness, report);
                    if (acquired.Scene is not { } scene) continue;
                    var scheduler = new SceneOracleScheduler(); var device = new SceneOracleDevice();
                    using var residency = new MapRenderResidency(new(source, closure, proof.Proofs!, policy, [windows[window.Id]]) { DecodeLimits = manifest.Limits?.Decode, AssetLimits = manifest.Limits?.Assets }, device, manifest.Limits?.Residency ?? new(MaxPlanBytes: 67108864), scheduler);
                    Complete(residency, scheduler);
                    var plan = residency.AcceptedPlans.Single();
                    var inventory = new MapDrawRecorder(); plan.Register(inventory);
                    completeWindows.Add(window.Id, inventory.Snapshot().Resources.ToDictionary(r => r.Reference.Key, Fingerprint));
                    CompareSource(expected, inputs, plan, report);
                    var terrain = ShippedSceneSourceChecks.Terrain(inputs, plan, report, out var terrainFaces);
                    var foliage = LegacySceneRecorder.RecordFoliage(inputs, scene.Scope);
                    ShippedSceneFoliageRoster.Compare(inputs, foliage, new(inventory.Snapshot(), plan.Inventory.Foliage.GenerationKeys, plan.Inventory.Foliage.GeneratedIdentities), report);
                    ShippedSceneSourceChecks.Water(inputs, plan, report);
                    var water = ShippedSceneWaterCoverage.Compare(inputs, plan, report);
                    foreach (var probe in manifest.Policy.Probes.Where(p => p.WindowId == window.Id))
                    {
                        var recorder = new MapDrawRecorder(); plan.Register(recorder);
                        var frame = Frame(probe, plan, source, closure, policy);
                        residency.Record(frame, decorate?.Invoke(recorder) ?? recorder);
                        var observation = new SceneOracleObservation(recorder.Snapshot(), plan.Inventory.Foliage.GenerationKeys, plan.Inventory.Foliage.GeneratedIdentities);
                        observation = observe?.Invoke(observation) ?? observation;
                        var actual = observation.DrawSet;
                        CheckStamp(actual, plan.Key, report);
                        ShippedSceneTerrainCoverage.Compare(terrainFaces, actual, report);
                        ShippedSceneWaterCoverage.ComparePayloads(inputs, plan, actual, report);
                        ShippedSceneFoliageRoster.Compare(inputs, foliage, observation, report);
                        foreach (var placement in expected.Placements.Where(p => inputs.SourcePolicy.AnimatedAssets.Contains(p.ArchetypeId) && ShippedSceneExpectedFrames.InScope(p, scene.Scope)))
                            ShippedSceneSourceChecks.ExplicitPlant(placement, manifest.Placements.Single(p => p.SourceObjectId == placement.Id).NativePlacementId, actual.Resources, inputs, report);
                        var expectedFrame = expected.Frames.Single(f => f.ProbeId == probe.Id);
                        CompareDrawSets(ShippedSceneExpectedFrames.Capture(inputs, expected, expectedFrame, probe, scene.Scope, foliage, terrain, water), actual, report);
                        string fingerprint = DrawFingerprint(actual);
                        if (heads.TryGetValue((window.Id, probe.Id), out var first)) Check(report, "scene-head", first == fingerprint, new { window = window.Id, probe = probe.Id });
                        else heads.Add((window.Id, probe.Id), fingerprint);
                        compared++;
                    }
                }
                Stream(source, closure, proof.Proofs!, policy, inputs, completeWindows, report);
            }
        report.Record("scene-summary", JsonSerializer.Serialize(new { heads = manifest.NativeDocumentPath is null ? 2 : 4, compared, scope = "draw sets and streaming, separate from appearance and import acceptance" }, ShippedScenePolicy.Json));
    }

    static void CompareSource(LegacySceneInventory source, ShippedSceneInputs inputs, MapRenderPlan plan, PrivateOracleReport report)
    {
        var recorder = new MapDrawRecorder(); plan.Register(recorder); var resources = recorder.Snapshot().Resources;
        foreach (var mapping in inputs.Manifest.Placements)
        {
            var placement = source.Placements.Single(p => p.Id == mapping.SourceObjectId);
            var scope = plan.Scene.Scope; var b = scope.DrawBounds; double pad = scope.MotionPaddingMetres + .0001;
            bool required = placement.Bounds.MaxX >= b.MinX - pad && placement.Bounds.MinX <= b.MaxX + pad && placement.Bounds.MaxZ >= b.MinZ - pad && placement.Bounds.MinZ <= b.MaxZ + pad;
            bool present = plan.Inventory.Placements.Inventory.Any(p => p.PlacementId == mapping.NativePlacementId);
            Check(report, "scene-placement-scope", !required || plan.Scene.Placements.Any(p => p.PlacementId == mapping.NativePlacementId), new { mapping.SourceObjectId });
            if (!present) continue;
            if (plan.Routing.Entries.TryGetValue(mapping.NativePlacementId, out var route) && route == MapPlacementRoute.Animated)
            {
                ShippedSceneSourceChecks.ExplicitPlant(placement, mapping.NativePlacementId, resources, inputs, report);
                continue;
            }
            var found = resources.Where(r => r.Reference.Key.OwnerId == mapping.NativePlacementId && r.Payload is MapRigidResource).ToArray();
            for (int tier = 0; tier < placement.Tiers.Count; tier++)
                for (int part = 0; part < placement.Tiers[tier].Count; part++)
                {
                    var rows = found.Where(r => r.Reference.Key.Lod == tier && r.Reference.Key.PartId == "part/" + part).ToArray();
                    Check(report, "scene-placement-part", rows.Length == 1 && rows[0].Payload is MapRigidResource rigid &&
                        MeshEqual(placement.Tiers[tier][part].Mesh, rigid.Mesh) && MapsEqual(placement.Tiers[tier][part].Maps, rigid.Maps), new { mapping.SourceObjectId, tier, part });
                }
            var actual = plan.Inventory.Placements.Inventory.Single(p => p.PlacementId == mapping.NativePlacementId);
            Check(report, "scene-placement", actual.NumericId == mapping.SourceObjectId && actual.Kind == placement.ArchetypeId && actual.Tags.SequenceEqual(placement.Tags), new { mapping.SourceObjectId, actual.NumericId });
        }
        var canonical = plan.Inventory.Terrain.CanonicalFaces;
        // The source ground pipeline and native paint tessellation may triangulate differently. Area and each
        // canonical face are checked separately from render-only refinement.
        double sourceArea = source.Ground.Sum(g => Area(g.Mesh));
        double nativeArea = canonical.Sum(f => TriangleArea(f.Triangle));
        if (plan.Scene.Witness.WholeWorldComplete || inputs.Manifest.Windows.Count == 1)
            Check(report, "scene-terrain", Math.Abs(sourceArea - nativeArea) <= GeometryTolerance, new { sourceArea, nativeArea });
        foreach (var light in source.Lights)
        {
            var mapping = inputs.Manifest.Lights.Single(l => l.SourceObjectId == light.SourceObjectId && l.SourceProfileId == light.SourceProfileId);
            var native = plan.Inventory.Lights.SingleOrDefault(l => l.Id == mapping.NativeLightId);
            var owner = source.Placements.Single(p => p.Id == light.SourceObjectId);
            var position = Vector3.Transform(light.Offset, owner.World);
            var b = plan.Scene.Scope.DrawBounds; double pad = plan.Scene.Scope.MotionPaddingMetres;
            bool required = position.X + light.Radius >= b.MinX - pad && position.X - light.Radius <= b.MaxX + pad && position.Z + light.Radius >= b.MinZ - pad && position.Z - light.Radius <= b.MaxZ + pad;
            Check(report, "scene-light", !required || native is not null, new { mapping.NativeLightId });
            if (native is null) continue;
            Check(report, "scene-light", Near(position.X, native.Position.X.ToDouble()) && Near(position.Y, native.Position.Y.ToDouble()) && Near(position.Z, native.Position.Z.ToDouble()) &&
                Bits(light.Colour, native.Colour) && Bits(light.Intensity, native.Intensity) && Bits(light.Radius, native.Radius) && light.CastShadows == native.CastShadows &&
                Bits(light.ShadowExclusion, native.ShadowExclusion) && native.OwnerPlacementId == inputs.Manifest.Placements.Single(p => p.SourceObjectId == light.SourceObjectId).NativePlacementId, new { mapping.NativeLightId });
        }
    }
    internal static void CompareDrawSets(MapDrawSet expected, MapDrawSet actual, PrivateOracleReport report)
    {
        var expectedResources = expected.Resources.GroupBy(r => r.Reference.Key).ToDictionary(g => g.Key, g => g.ToArray());
        var actualResources = actual.Resources.GroupBy(r => r.Reference.Key).ToDictionary(g => g.Key, g => g.ToArray());
        foreach (var group in actualResources)
            Check(report, "scene-resource-duplicate", group.Value.Length == 1, new { key = group.Key });
        foreach (var key in expectedResources.Keys.Union(actualResources.Keys))
        {
            bool one = expectedResources.TryGetValue(key, out var a) && actualResources.TryGetValue(key, out var b) && a.Length == 1 && b.Length == 1;
            Check(report, Category(key.Kind), one, new { key });
            if (!one) continue;
            var left = expectedResources[key][0]; var right = actualResources[key][0];
            Check(report, Category(key.Kind), Bits(left.Bounds, right.Bounds) && PayloadEqual(left.Payload, right.Payload), new { key });
        }
        var submissions = expected.Draws.Select(draw => new SceneExpectedSubmission(draw,
            expected.Resources.Single(r => r.Reference == draw.Resource).Payload as MapRigidResource)).ToArray();
        CompareDrawSets(submissions, actual, report);
    }
    internal static bool PayloadEqual(MapResourcePayload a, MapResourcePayload b) => (a, b) switch
    {
        (MapRigidResource x, MapRigidResource y) => MeshEqual(x.Mesh, y.Mesh) && MapsEqual(x.Maps, y.Maps),
        (MapLightResource x, MapLightResource y) => Bits(x.Light, y.Light),
        (MapWaterResource x, MapWaterResource y) => MeshEqual(x.Mesh, y.Mesh) && x.ClipData.Span.SequenceEqual(y.ClipData.Span),
        (MapFoliageResource x, MapFoliageResource y) => FoliageEqual(x, y),
        (MapTerrainResource x, MapTerrainResource y) => MeshEqual(x.Mesh, y.Mesh) && x.FaceKeys.SequenceEqual(y.FaceKeys) &&
            x.Materials.Width == y.Materials.Width && x.Materials.Height == y.Materials.Height && x.Materials.Layers.Count == y.Materials.Layers.Count &&
            x.Materials.Layers.Zip(y.Materials.Layers).All(p => p.First.MaterialId == p.Second.MaterialId && p.First.ResourceId == p.Second.ResourceId && Bits(p.First.Tint, p.Second.Tint) && Bits(p.First.Albedo, p.Second.Albedo)),
        _ => false
    };
    static bool FoliageEqual(MapFoliageResource a, MapFoliageResource b)
    {
        if (a.Anchor != b.Anchor || a.TransformSpace != b.TransformSpace || !a.ModelIndices.Span.SequenceEqual(b.ModelIndices.Span) ||
            !a.Ranks.Span.SequenceEqual(b.Ranks.Span) || a.Models.Count != b.Models.Count) return false;
        var x = a.TransformSpace == MapFoliageTransformSpace.World ? a.WorldTransforms : a.AnchorLocalTransforms;
        var y = b.TransformSpace == MapFoliageTransformSpace.World ? b.WorldTransforms : b.AnchorLocalTransforms;
        if (!Bits(x.ToArray(), y.ToArray())) return false;
        return a.Models.Zip(b.Models).All(p => p.First.ModelId == p.Second.ModelId && Bits(p.First.Motion, p.Second.Motion) &&
            p.First.CastShadows == p.Second.CastShadows && p.First.Parts.Select(r => r.Key).SequenceEqual(p.Second.Parts.Select(r => r.Key)));
    }
    internal static bool MeshEqual(GltfMesh a, GltfMesh b)
    {
        if (!a.Indices32.SequenceEqual(b.Indices32) || a.Vertices.Length != b.Vertices.Length) return false;
        return a.Vertices.Zip(b.Vertices).All(p => Near(p.First.Position.X, p.Second.Position.X) && Near(p.First.Position.Y, p.Second.Position.Y) && Near(p.First.Position.Z, p.Second.Position.Z) &&
            Math.Abs(p.First.Normal.X - p.Second.Normal.X) <= NormalTolerance && Math.Abs(p.First.Normal.Y - p.Second.Normal.Y) <= NormalTolerance && Math.Abs(p.First.Normal.Z - p.Second.Normal.Z) <= NormalTolerance &&
            VectorBits(p.First.Color, p.Second.Color) && VectorBits(p.First.Uv, p.Second.Uv) && VectorBits(p.First.Tangent, p.Second.Tangent));
    }
    static bool FloatBits(float a, float b) => BitConverter.SingleToInt32Bits(a) == BitConverter.SingleToInt32Bits(b);
    static bool VectorBits(Vector2 a, Vector2 b) => FloatBits(a.X, b.X) && FloatBits(a.Y, b.Y);
    static bool VectorBits(Vector4 a, Vector4 b) => FloatBits(a.X, b.X) && FloatBits(a.Y, b.Y) && FloatBits(a.Z, b.Z) && FloatBits(a.W, b.W);
    internal static bool MapsEqual(GltfMaterialMaps a, GltfMaterialMaps b) => Bits(a.Albedo, b.Albedo) && Bits(a.Normal, b.Normal) && Bits(a.Roughness, b.Roughness) && Bits(a.AlphaCutoff, b.AlphaCutoff);
    internal static bool Bits<T>(T a, T b) => JsonSerializer.Serialize(a, ShippedScenePolicy.Json) == JsonSerializer.Serialize(b, ShippedScenePolicy.Json);
    internal static bool MatrixNear(Matrix4x4 a, Matrix4x4 b)
    {
        if (!Near(a.M41, b.M41) || !Near(a.M42, b.M42) || !Near(a.M43, b.M43) || !Bits(a.M14, b.M14) || !Bits(a.M24, b.M24) || !Bits(a.M34, b.M34) || !Bits(a.M44, b.M44)) return false;
        return Matrix4x4.Decompose(a, out var scaleA, out var rotationA, out _) && Matrix4x4.Decompose(b, out var scaleB, out var rotationB, out _) &&
            Bits(scaleA, scaleB) && ShippedFoliageComparison.Angle(rotationA, rotationB) <= AngleTolerance;
    }
    static bool Near(double a, double b) => double.IsFinite(a) && double.IsFinite(b) && Math.Abs(a - b) <= GeometryTolerance;
    static Matrix4x4 Absolute(MapDrawRecord d) => d.LocalTransform * Matrix4x4.CreateTranslation(new((float)d.Anchor.X, (float)d.Anchor.Y, (float)d.Anchor.Z));
    static double Area(GltfMesh mesh)
    {
        double area = 0;
        for (int i = 0; i < mesh.Indices32.Length; i += 3)
        { var a = mesh.Vertices[mesh.Indices32[i]].Position; var b = mesh.Vertices[mesh.Indices32[i + 1]].Position; var c = mesh.Vertices[mesh.Indices32[i + 2]].Position; area += Vector3.Cross(b - a, c - a).Length() / 2; }
        return area;
    }
    static double TriangleArea(MapExactTriangle t)
    {
        var a = new Vector3((float)t.A.X.ToDouble(), (float)t.A.Y.ToDouble(), (float)t.A.Z.ToDouble());
        var b = new Vector3((float)t.B.X.ToDouble(), (float)t.B.Y.ToDouble(), (float)t.B.Z.ToDouble());
        var c = new Vector3((float)t.C.X.ToDouble(), (float)t.C.Y.ToDouble(), (float)t.C.Z.ToDouble());
        return Vector3.Cross(b - a, c - a).Length() / 2;
    }
    internal static string Category(MapRenderKind kind) => kind switch
    {
        MapRenderKind.Placement => "scene-placement-part",
        MapRenderKind.Roof => "scene-roof-channel",
        MapRenderKind.Terrain => "scene-terrain",
        MapRenderKind.Light => "scene-light",
        MapRenderKind.Water => "scene-water",
        MapRenderKind.Foliage => "scene-foliage",
        _ => "scene-marker"
    };
    static readonly System.Runtime.CompilerServices.ConditionalWeakTable<PrivateOracleReport, Dictionary<string, int>> Details = new();
    internal static void Check(PrivateOracleReport report, string category, bool ok, object detail)
    {
        if (ok) return;
        var counts = Details.GetOrCreateValue(report);
        counts.TryGetValue(category, out int count); counts[category] = count + 1;
        report.Check(category, false, count < 16 ? JsonSerializer.Serialize(detail, ShippedScenePolicy.Json) : "{}");
    }
    internal static string Fingerprint(MapPreparedResource resource) => Hash(new { resource.Reference.Key, resource.Bounds, payload = PayloadValue(resource.Payload) });
    static object MeshValue(GltfMesh mesh) => new { mesh.Vertices, mesh.Indices32 };
    static object PayloadValue(MapResourcePayload payload) => payload switch
    {
        MapRigidResource p => new { kind = "rigid", mesh = MeshValue(p.Mesh), p.Maps },
        MapLightResource p => new { kind = "light", p.Light },
        MapWaterResource p => new { kind = "water", mesh = MeshValue(p.Mesh), clip = p.ClipData.ToArray() },
        MapTerrainResource p => new { kind = "terrain", mesh = MeshValue(p.Mesh), p.Materials, p.FaceKeys },
        MapFoliageResource p => new
        {
            kind = "foliage",
            p.Anchor,
            p.TransformSpace,
            transforms = (p.TransformSpace == MapFoliageTransformSpace.World ? p.WorldTransforms : p.AnchorLocalTransforms).ToArray(),
            ranks = p.Ranks.ToArray(),
            indices = p.ModelIndices.ToArray(),
            models = p.Models.Select(m => new { m.ModelId, parts = m.Parts.Select(r => r.Key), m.Motion, m.CastShadows })
        },
        _ => throw new InvalidOperationException("unknown scene resource payload")
    };
    static string DrawFingerprint(MapDrawSet set) => Hash(new
    {
        resources = set.Resources.OrderBy(r => r.Reference.Key.Kind).ThenBy(r => r.Reference.Key.OwnerId, StringComparer.Ordinal).ThenBy(r => r.Reference.Key.PartId, StringComparer.Ordinal).ThenBy(r => r.Reference.Key.Lod).Select(Fingerprint),
        draws = set.Draws.Select(d => new { d.Resource.Key, d.Channels, d.Anchor, d.LocalTransform, d.LocalFocus, d.Rigid, d.Light, d.Water, d.Foliage })
    });
    static string Hash<T>(T value)
    {
        using var algorithm = System.Security.Cryptography.SHA256.Create();
        using var stream = new System.Security.Cryptography.CryptoStream(System.IO.Stream.Null, algorithm, System.Security.Cryptography.CryptoStreamMode.Write);
        using (var writer = new System.Text.Json.Utf8JsonWriter(stream, KhaozEngine.MapDoc.MapJsonTextOptions.IndentedWriter)) JsonSerializer.Serialize(writer, value, ShippedScenePolicy.Json);
        stream.FlushFinalBlock(); return Convert.ToHexStringLower(algorithm.Hash!);
    }
    internal static void CheckCoverage(MapSceneWitness witness, PrivateOracleReport report) =>
        Check(report, "scene-completeness", witness.CompleteForScope && witness.Unavailable.Count == 0 &&
            !witness.Required.Except(witness.Present.Concat(witness.KnownEmpty)).Any(), new { witness.CompleteForScope, required = witness.Required.Count, present = witness.Present.Count });
    internal static void CheckStamp(MapDrawSet set, MapResourceStamp stamp, PrivateOracleReport report)
    {
        foreach (var resource in set.Resources) Check(report, "scene-generation", resource.Reference.Stamp == stamp, new { key = resource.Reference.Key });
        foreach (var draw in set.Draws) Check(report, "scene-generation", draw.Resource.Stamp == stamp && set.Resources.Any(r => r.Reference == draw.Resource), new { key = draw.Resource.Key });
    }
    static MapFrameState Frame(SceneProbe p, MapRenderPlan plan, MapSceneSource source, MapAssetClosure closure, MapRenderPolicy policy)
    {
        var observer = ShippedSceneInputs.Point(p.ObserverX, p.ObserverY, p.ObserverZ);
        var focus = p.FocusX is { } x ? ShippedSceneInputs.Point(x, p.FocusY!.Value, p.FocusZ!.Value) : observer;
        if (p.FocusX is null)
        {
            var request = new MapSnapshotRequest(closure, source, plan.Scene.Scope, new(ShippedSceneInputs.Point(plan.Scene.Scope.DrawBounds.MinX / 2 + plan.Scene.Scope.DrawBounds.MaxX / 2, plan.Scene.Scope.DrawBounds.MaxY + 16, plan.Scene.Scope.DrawBounds.MinZ / 2 + plan.Scene.Scope.DrawBounds.MaxZ / 2), observer, true, 45, 32, 32), observer, null,
                p.RoofMode, p.TimeSeconds, p.WindSeed, "oracle", new(policy, new(Vector3.UnitY, KhaozEngine.Primitives.Color.White, 1, new()), MapCaptureAa.Native), MapCaptureRequirement.CompleteScope, MapCaptureRoute.TopDown);
            focus = MapCaptureFocus.Resolve(request, plan.Scene);
        }
        return new(plan.Scene.Scope.Surfaces.Frame, observer, p.RoofMode, p.TimeSeconds, p.WindSeed, focus);
    }
    static void Stream(MapSceneSource source, MapAssetClosure closure, MapAssetExtentProofSet proof, MapRenderPolicy policy, ShippedSceneInputs inputs, IReadOnlyDictionary<string, IReadOnlyDictionary<MapRenderKey, string>> completeWindows, PrivateOracleReport report)
    {
        var scheduler = new SceneOracleScheduler(); var device = new SceneOracleDevice();
        var active = new Dictionary<string, MapSceneScope>(StringComparer.Ordinal);
        using var residency = new MapRenderResidency(new(source, closure, proof, policy, []) { DecodeLimits = inputs.Manifest.Limits?.Decode, AssetLimits = inputs.Manifest.Limits?.Assets }, device, inputs.Manifest.Limits?.Residency ?? new(MaxActiveWindows: 256, MaxPlanBytes: 67108864), scheduler);
        Complete(residency, scheduler);
        foreach (var step in inputs.Manifest.Sequence)
        {
            if (step.Operation == "Unload") active.Remove(step.WindowId);
            else active[step.WindowId] = ShippedSceneInputs.Scope(inputs.Manifest.Windows.Single(w => w.Id == step.WindowId), inputs.Manifest.Limits?.Queries);
            residency.SetWindows(active.Values.ToArray()); Complete(residency, scheduler);
            var recorder = new MapDrawRecorder(); var point = ShippedSceneInputs.Point(0, 0, 0);
            residency.Record(new(default, point, KhaozEngine.MapDoc.Interiors.MapRoofMode.AlwaysVisible, 0, 0, point), recorder);
            var set = recorder.Snapshot();
            Check(report, "scene-stream-union", active.Values.Select(s => s.Digest).ToHashSet(StringComparer.Ordinal).SetEquals(residency.AcceptedPlans.Select(p => p.Key.ScopeDigest)), new { step.Operation });
            var expectedUnion = active.Keys.SelectMany(id => completeWindows[id]).GroupBy(p => p.Key).ToDictionary(g => g.Key, g => g.First().Value);
            var actualUnion = set.Resources.GroupBy(r => r.Reference.Key).ToDictionary(g => g.Key, g => Fingerprint(g.First()));
            Check(report, "scene-stream-union", expectedUnion.Count == actualUnion.Count && expectedUnion.All(p => actualUnion.TryGetValue(p.Key, out var fingerprint) && fingerprint == p.Value), new { step.Operation, expected = expectedUnion.Count, actual = actualUnion.Count });
            foreach (var group in set.Resources.GroupBy(r => r.Reference.Key))
                Check(report, "scene-stream-union", group.All(r => PayloadEqual(r.Payload, group.First().Payload) && Bits(r.Bounds, group.First().Bounds)), new { key = group.Key });
            Check(report, "scene-generation", set.Draws.All(d => device.Live.Values.Any(r => r.Reference.Key == d.Resource.Key && PayloadEqual(r.Payload, set.Resources.Single(r => r.Reference == d.Resource).Payload))), new { step.Operation });
            Check(report, "scene-completeness", residency.InspectReadiness().Transition.AcceptedWitness?.CompleteForScope == true, new { step.Operation });
        }
        Check(report, "scene-generation", device.Live.Count == 0 && residency.Lifetime.LiveHandles == 0, new { live = device.Live.Count });
    }
    internal static void Complete(MapRenderResidency residency, SceneOracleScheduler scheduler)
    {
        scheduler.RunAll(); residency.CompletePreparation();
        var transition = residency.InspectReadiness().Transition;
        for (int i = 0; i < 1024; i++)
        {
            var ready = residency.Update(); scheduler.RunAll(); residency.CompletePreparation();
            transition = ready.Transition;
            if (ready.Transition.Status != MapSceneStatus.Incomplete)
            {
                if (ready.Transition.Status != MapSceneStatus.CompleteForScope && scheduler.LastFailure is { } error)
                    System.Runtime.ExceptionServices.ExceptionDispatchInfo.Capture(error).Throw();
                Require(ready.Transition.Status == MapSceneStatus.CompleteForScope, "scene replacement refused " + ready.Transition.Status + " " + string.Join("\n", ready.Transition.Findings)); return;
            }
        }
        var findings = transition.Findings.Concat(transition.Capacity is { } capacity ? new[] { capacity.Message } : Array.Empty<string>());
        throw new InvalidOperationException("scene replacement did not settle " + string.Join("\n", findings.Distinct(StringComparer.Ordinal)),
            transition.Capacity is { } refused ? new MapSceneCapacityException(refused.LimitName, refused.Requested, refused.Allowance) : null);
    }

    internal static MapSceneSourceResult CaptureSource(MapDocumentSource document, MapAssetClosure closure, ShippedSceneManifest manifest)
        => MapSceneSource.Capture(document, closure, manifest.Limits?.Scene);
}
