using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Editing;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Storage;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.MapDoc.Scene;
using KhaozEngine.Tests.Render3D;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

public sealed class NativeMapTerrainTests
{
    static readonly IReadOnlyDictionary<ushort, string> Materials = new Dictionary<ushort, string>
    { [10] = "mat-0", [11] = "mat-1", [12] = "mat-2" };
    static readonly MapResourceStamp Stamp = new("closure", "source", "scope", "policy", 7);
    static MapSceneScope Window(bool far = false) => new(
        new(WorldFrame.Origin, far ? new(31990, -32010) : new(-2, -66), far ? new(32010, -31990) : new(66, 2),
            null, null, Enum.GetValues<MapSurfaceRole>(), null, new()),
        far ? new MapBox3(31990, -510, -32010, 32010, 510, -31990) : new(-2, -510, -66, 66, 510, 2), MapSceneChannels.Terrain);
    static NativeSceneFixture Fixture(string variant, MapAssetClosure closure)
    {
        var fixture = NativeSceneFixtures.Create(variant) with { Assets = closure };
        fixture.Document.NativeAssets = closure.Roots.ToList();
        return fixture;
    }
    static MapScopedScene Acquire(NativeSceneFixture fixture, bool tiled = false, bool far = false)
    {
        var result = NativeSceneFixtures.Open(fixture, tiled).Acquire(Window(far));
        Assert.True(result.Witness.CompleteForScope, string.Join("\n", result.Findings));
        return Assert.IsType<MapScopedScene>(result.Scene);
    }
    static MapPreparedInputLease Input(MapAssetClosure closure, params string[] ids)
    {
        var proofs = MapRenderExtentValidator.Validate(closure, [], new(new())).Proofs!;
        var loaded = MapPreparedInputLease.LoadSelected(closure, [], ids, proofs, new(new()));
        Assert.Equal(MapRenderAssetStatus.Complete, loaded.Status);
        return loaded.Input!;
    }
    static MapDrawSet Record(MapTerrainRenderBatch batch, bool far = false)
    {
        var recorder = new MapDrawRecorder();
        batch.Register(Stamp, recorder);
        var point = new MapFramePoint(far ? new WorldFrame(250, -250) : WorldFrame.Origin, new(.125f, 2, -.25f));
        batch.Record(new(point.Frame, point, MapRoofMode.Auto, 2, 17, point), recorder);
        return recorder.Snapshot();
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void DistanceNeverChangesCanonicalSurface(bool tiled)
    {
        var (closure, ids) = NativeRenderAssetFixtures.TexturelessMaterials(3);
        using var fixture = Fixture("terrain", closure);
        var scene = Acquire(fixture, tiled);
        using var input = Input(closure, ids);
        using var batch = NativeMapTerrain.Prepare(scene, input, Materials);
        var near = Record(batch);
        var far = Record(batch, true);
        var nearFaceKeys = near.Resources.SelectMany(r => Assert.IsType<MapTerrainResource>(r.Payload).FaceKeys).ToArray();
        var farFaceKeys = far.Resources.SelectMany(r => Assert.IsType<MapTerrainResource>(r.Payload).FaceKeys).ToArray();
        Assert.Equal(nearFaceKeys, farFaceKeys);
        var a = new MapExactPoint(new(0, 1), new(5, 4), new(0, 1));
        var b = new MapExactPoint(new(1, 1), new(5, 4), new(0, 1));
        var c = new MapExactPoint(new(1, 1), new(5, 4), new(-1, 1));
        var d = new MapExactPoint(new(0, 1), new(5, 4), new(-1, 1));
        Assert.Equal(new[] { new MapExactTriangle(a, b, c), new MapExactTriangle(a, c, d) },
            batch.CanonicalFaces.Select(f => f.Triangle));
        Assert.Equal(2, nearFaceKeys.Length);
        Assert.All(near.Draws, draw =>
        {
            Assert.Same(Stamp, draw.Resource.Stamp);
            Assert.Equal(new MapSubmissionAnchor(0, 1, -1), draw.Anchor);
            Assert.Equal(new Vector3(.125f, 1, .75f), draw.LocalFocus);
            Assert.Equal(Matrix4x4.Identity, draw.LocalTransform);
            Assert.Equal(Color.White, draw.Rigid!.Value.Tint);
            Assert.Equal(0, draw.Rigid.Value.Dissolve);
            Assert.True(draw.Rigid.Value.Motion.IsNone);
        });
        Assert.True(batch.RetainedBytes > 0);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void PartialPaint_PartitionsOnePhysicalFaceWithDeterministicWinner(bool tiled)
    {
        var (closure, ids) = NativeRenderAssetFixtures.TexturelessMaterials(3);
        using var fixture = Fixture("terrain-paint", closure);
        using var input = Input(closure, ids);
        using var batch = NativeMapTerrain.Prepare(Acquire(fixture, tiled), input, Materials);
        Assert.Equal(2, batch.CanonicalFaces.Count(f => f.Face.Role == MapFaceRole.SupportFloor));
        Assert.Contains(batch.Partitions, p => p.PaintId is not null);
        Assert.All(batch.Partitions.Where(p => p.PaintId is not null), p => Assert.Equal("paint-a", p.PaintId));
        Assert.Equal(.25, batch.Partitions.Where(p => p.PaintId is not null).Sum(p => Area(p.Polygon)), 10);
        Assert.Equal(.75, batch.Partitions.Where(p => p.PaintId is null).Sum(p => Area(p.Polygon)), 10);
        Assert.Equal(1, batch.Partitions.Sum(p => Area(p.Polygon)), 10);
        AssertGeometry(batch, Record(batch));
    }

    [Fact]
    public void ConflictingEqualPriorityPaint_KeepsReleasedRefusal()
    {
        var (closure, _) = NativeRenderAssetFixtures.TexturelessMaterials(3);
        using var fixture = Fixture("terrain-conflict", closure);
        var result = NativeSceneFixtures.Open(fixture, false).Acquire(Window());
        Assert.False(result.Witness.CompleteForScope);
        Assert.Contains(result.Findings, f => f.Contains("equal-priority", StringComparison.Ordinal));
    }

    [Theory]
    [InlineData("terrain-feather")]
    [InlineData("terrain-cuts")]
    public void Feather_PacksBothMaterialsOnOneSurfaceAndPreservesUnderlay(string variant)
    {
        var (closure, ids) = NativeRenderAssetFixtures.TexturelessMaterials(3);
        using var fixture = Fixture(variant, closure);
        using var input = Input(closure, ids);
        using var batch = NativeMapTerrain.Prepare(Acquire(fixture), input, Materials);
        var set = Record(batch);
        Assert.Equal(2, batch.CanonicalFaces.Count);
        AssertGeometry(batch, set);
        var terrain = Assert.IsType<MapTerrainResource>(Assert.Single(set.Resources).Payload);
        Assert.Equal(new ushort[] { 10, 11 }, terrain.Materials.Layers.Select(l => l.MaterialId));
        var vertices = terrain.Mesh.Vertices;
        Assert.All(vertices, v =>
        {
            Assert.Equal(1, v.Tangent.Z);
            Assert.Equal(Vector2.Zero, v.Uv);
            Assert.Equal(0, v.Tangent.X);
            Assert.Equal(0, v.Tangent.Y);
            Assert.InRange(v.Color.X, 0, 1);
            Assert.Equal(Vector3.Zero, new Vector3(v.Color.Y, v.Color.Z, v.Color.W));
        });
        Assert.Contains(vertices, v => v.Color.X == 1);
        Assert.Contains(vertices, v => v.Tangent.W == 2 && v.Color.X == 0);
        Assert.Contains(Enumerable.Range(0, vertices.Length / 3), i =>
            vertices.Skip(i * 3).Take(3).All(v => v.Color.X > 0 && v.Color.X < 1 && v.Tangent.W == 2));
    }

    [Fact]
    public void Feather_SharedCanonicalDiagonalHasFullOverlay()
    {
        var (closure, ids) = NativeRenderAssetFixtures.TexturelessMaterials(3);
        using var fixture = Fixture("terrain-feather", closure);
        using var input = Input(closure, ids);
        using var batch = NativeMapTerrain.Prepare(Acquire(fixture), input, Materials);
        AssertPackedOverlayAtVertex(Record(batch), .5, -.5, 1);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void Feather_SharedCollinearSubdivisionsHaveFullOverlay(bool tiled)
    {
        var (closure, ids) = NativeRenderAssetFixtures.TexturelessMaterials(3);
        using var fixture = Fixture("terrain-feather-collinear", closure);
        using var input = Input(closure, ids);
        using var batch = NativeMapTerrain.Prepare(Acquire(fixture, tiled), input, Materials);
        var set = Record(batch);
        AssertGeometry(batch, set);
        AssertPackedOverlayAtVertex(set, 64, -.25, 1);
        AssertPackedOverlayAtVertex(set, 64, -.5, 1);
        AssertPackedOverlayAtVertex(set, 64, -.75, 1);
    }

    [Theory]
    [InlineData(.8)]
    [InlineData(1.6)]
    [InlineData(3.2)]
    public void Feather_ScaledFacesKeepPackedBandWeightsInWorldMetres(double scale)
    {
        var (closure, ids) = NativeRenderAssetFixtures.TexturelessMaterials(3);
        using var fixture = Fixture("terrain-feather", closure);
        ScaleTerrain(fixture, scale);
        using var input = Input(closure, ids);
        using var batch = NativeMapTerrain.Prepare(Acquire(fixture), input, Materials);
        var set = Record(batch);
        AssertPackedOverlayAtVertex(set, 0, -scale / 2, 0);
        AssertPackedOverlayAtVertex(set, .1, -scale / 2, .5f);
        AssertPackedOverlayAtVertex(set, .2, -scale / 2, 1);
    }

    [Fact]
    public void Feather_RefusesScaledFaceBeforeRegisteringAWidenedBand()
    {
        var (closure, ids) = NativeRenderAssetFixtures.TexturelessMaterials(3);
        using var fixture = Fixture("terrain-feather", closure);
        ScaleTerrain(fixture, 64);
        using var input = Input(closure, ids);
        var scene = Acquire(fixture);
        var recorder = new MapDrawRecorder();
        Assert.Equal(MapRenderAssetStatus.CapacityExceeded, Assert.Throws<MapRenderAssetRefusal>(() =>
        {
            using var batch = NativeMapTerrain.Prepare(scene, input, Materials);
            batch.Register(Stamp, recorder);
        }).Status);
        Assert.Empty(recorder.Snapshot().Resources);
    }

    [Fact]
    public void MismatchedAlbedoDimensions_RefuseBeforeRegistration()
    {
        var closure = NativeRenderAssetFixtures.Create("terrain-mismatched-albedo");
        using var fixture = Fixture("terrain-feather", closure);
        using var input = Input(closure, "paint", "wide-paint");
        Assert.Equal((1, 1), (input.Materials["paint"].Maps.Albedo!.Value.Width, input.Materials["paint"].Maps.Albedo!.Value.Height));
        Assert.Equal((2, 1), (input.Materials["wide-paint"].Maps.Albedo!.Value.Width, input.Materials["wide-paint"].Maps.Albedo!.Value.Height));
        var recorder = new MapDrawRecorder();
        Assert.Equal(MapRenderAssetStatus.Unsupported, Assert.Throws<MapRenderAssetRefusal>(() =>
        {
            using var batch = NativeMapTerrain.Prepare(Acquire(fixture), input,
                new Dictionary<ushort, string> { [10] = "paint", [11] = "wide-paint" });
            batch.Register(Stamp, recorder);
        }).Status);
        Assert.Empty(recorder.Snapshot().Resources);
    }

    static void ScaleTerrain(NativeSceneFixture fixture, double scale) =>
        fixture.Document.NativeResourceTransforms = new[] { new MapStandaloneResourceTransform(MapPrefabChildKind.Surface, "ground",
            new(0, 0, 0, 0, scale), new string('a', 64)) };

    static void AssertPackedOverlayAtVertex(MapDrawSet set, double x, double z, float expected)
    {
        var selected = set.Resources.SelectMany(resource =>
        {
            var terrain = Assert.IsType<MapTerrainResource>(resource.Payload);
            var draw = set.Draws.Single(d => d.Resource == resource.Reference);
            return terrain.Mesh.Vertices.Where(v => Math.Abs(draw.Anchor.X + (double)v.Position.X - x) < .000001 &&
                Math.Abs(draw.Anchor.Z + (double)v.Position.Z - z) < .000001);
        }).ToArray();
        Assert.NotEmpty(selected);
        Assert.All(selected, vertex =>
        {
            Assert.Equal(Vector2.Zero, vertex.Uv);
            Assert.Equal(new Vector4(0, 0, 1, 2), vertex.Tangent);
            Assert.Equal(Vector3.Zero, new(vertex.Color.Y, vertex.Color.Z, vertex.Color.W));
            Assert.InRange(Math.Abs(1 - vertex.Color.X - expected), 0, .00001);
        });
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void CeilingSidednessAndLocalAnchor_AreExact(bool far)
    {
        var (closure, ids) = NativeRenderAssetFixtures.TexturelessMaterials(3);
        using var fixture = Fixture(far ? "terrain-posed" : "terrain-ceiling", closure);
        using var input = Input(closure, ids);
        using var batch = NativeMapTerrain.Prepare(Acquire(fixture, far: far), input, Materials);
        Assert.Equal(2, batch.CanonicalFaces.Count(f => f.Face.Role == MapFaceRole.Ceiling));
        Assert.All(batch.CanonicalFaces.Where(f => f.Face.Role == MapFaceRole.Ceiling), f => Assert.True(f.Face.Normal.Y < 0));
        AssertGeometry(batch, Record(batch));
        var pose = new MapEffectiveTransform(31999.123456, -499.5, -31999.654321, .371, 1.2);
        var anchor = new MapSubmissionAnchor(31999, -500, -32000);
        var local = MapRenderTransform.ToLocal(pose, anchor);
        Assert.InRange(Math.Abs(local.M41 - .123456), 0, .0001);
        Assert.InRange(Math.Abs(local.M42 - .5), 0, .0001);
        Assert.InRange(Math.Abs(local.M43 - .345679), 0, .0001);
        var focus = new MapFramePoint(new(250, -250), new(.123456f, -499.5f, .345679f));
        Assert.Equal(new Vector3(1.123456f, .5f, .345679f), MapRenderTransform.FocusLocal(focus, anchor));
    }

    [Fact]
    public void AbsentMaterial_RefusesBeforeTerrainUpload()
    {
        var (closure, ids) = NativeRenderAssetFixtures.TexturelessMaterials(3);
        using var fixture = Fixture("terrain", closure);
        using var input = Input(closure, ids[1..]);
        var scene = Acquire(fixture);
        Assert.Equal(MapRenderAssetStatus.Missing,
            Assert.Throws<MapRenderAssetRefusal>(() => NativeMapTerrain.Prepare(scene, input, Materials)).Status);
        Assert.Equal(MapRenderAssetStatus.Missing,
            Assert.Throws<MapRenderAssetRefusal>(() => NativeMapTerrain.Prepare(scene, input, new Dictionary<ushort, string>())).Status);
    }

    [Fact]
    public void MaterialSets_PartitionDeterministicallyAndRefuseAnUnfittableFace()
    {
        var (closure, ids) = NativeRenderAssetFixtures.TexturelessMaterials(65);
        using var fixture = Fixture("terrain-many", closure);
        using var input = Input(closure, ids);
        var mapping = Enumerable.Range(0, 65).ToDictionary(i => (ushort)(10 + i), i => ids[i]);
        using var batch = NativeMapTerrain.Prepare(Acquire(fixture), input, mapping);
        var payloads = Record(batch).Resources.Select(r => Assert.IsType<MapTerrainResource>(r.Payload)).ToArray();
        Assert.Equal(new[] { TileGroundMaterialConfig.MaxMaterials, 1 }, payloads.Select(p => p.Materials.Layers.Count));
        Assert.Equal(Enumerable.Range(10, 65).Select(i => (ushort)i), payloads.SelectMany(p => p.Materials.Layers.Select(l => l.MaterialId)));
        using var split = NativeMapTerrain.Prepare(Acquire(fixture), input, mapping, maxMaterials: 1);
        Assert.Equal(65, Record(split).Resources.Count);
        using var feather = Fixture("terrain-feather", closure);
        Assert.Equal(MapRenderAssetStatus.CapacityExceeded, Assert.Throws<MapRenderAssetRefusal>(() =>
            NativeMapTerrain.Prepare(Acquire(feather), input, mapping, maxMaterials: 1)).Status);
        using var oneFace = Fixture("terrain-many-face", closure);
        var scope = Window();
        scope = scope with
        {
            Surfaces = scope.Surfaces with { LocalMin = new(0, -1), LocalMax = new(1, 0) },
            DrawBounds = new(0, -510, -1, 1, 510, 0)
        };
        var acquired = NativeSceneFixtures.Open(oneFace, false).Acquire(scope);
        Assert.True(acquired.Witness.CompleteForScope, string.Join("\n", acquired.Findings));
        Assert.Equal(MapRenderAssetStatus.CapacityExceeded, Assert.Throws<MapRenderAssetRefusal>(() =>
            NativeMapTerrain.Prepare(acquired.Scene!, input, mapping)).Status);
    }

    [Fact]
    public void NonAlbedoMaps_ReportTypedFindingsAndRetainPreparedOwnership()
    {
        var closure = NativeRenderAssetFixtures.Create("terrain-extra-maps");
        using var fixture = Fixture("terrain", closure);
        var input = Input(closure, "paint");
        long bytes = input.RetainedBytes;
        using var batch = NativeMapTerrain.Prepare(Acquire(fixture), input, new Dictionary<ushort, string> { [10] = "paint" });
        input.Dispose();
        Assert.Equal(new[] { MapTerrainFindingKind.NormalMapNotConsumed, MapTerrainFindingKind.RoughnessMapNotConsumed }, batch.Findings.Select(f => f.Kind));
        Assert.Equal(bytes, batch.InputRetainedBytes);
        var payload = Assert.IsType<MapTerrainResource>(Assert.Single(Record(batch).Resources).Payload);
        Assert.Equal(220, payload.Materials.Layers[0].Albedo!.Value.Rgba[0]);
    }

    [Fact]
    public void MaterialZero_HasNoInventedLayerOrDraw()
    {
        var (closure, _) = NativeRenderAssetFixtures.TexturelessMaterials(1);
        using var fixture = Fixture("terrain-zero", closure);
        using var input = Input(closure);
        using var batch = NativeMapTerrain.Prepare(Acquire(fixture), input, Materials);
        Assert.Equal(2, batch.CanonicalFaces.Count);
        Assert.Empty(Record(batch).Resources);
    }

    [Fact]
    public void WallSides_RetainCanonicalNormalsAndShadowGeometry()
    {
        var (closure, ids) = NativeRenderAssetFixtures.TexturelessMaterials(3);
        using var fixture = Fixture("terrain-wall", closure);
        using var input = Input(closure, ids);
        using var batch = NativeMapTerrain.Prepare(Acquire(fixture), input, Materials);
        var walls = batch.CanonicalFaces.Where(f => f.Face.Role == MapFaceRole.Wall).ToArray();
        Assert.Equal(4, walls.Length);
        Assert.All(walls.Where(f => f.Face.Key.Side == MapSide.Front), f => Assert.Equal(-Vector3.UnitZ, f.Face.Normal));
        Assert.All(walls.Where(f => f.Face.Key.Side == MapSide.Back), f => Assert.Equal(Vector3.UnitZ, f.Face.Normal));
        AssertGeometry(batch, Record(batch));
    }

    [Fact]
    public void CombinedPaintAndFeather_PreserveOneGroundSurfaceAndItsCeiling()
    {
        var (closure, ids) = NativeRenderAssetFixtures.TexturelessMaterials(3);
        using var fixture = Fixture("terrain-combined", closure);
        using var input = Input(closure, ids);
        using var batch = NativeMapTerrain.Prepare(Acquire(fixture), input, Materials);
        Assert.Equal(2, batch.CanonicalFaces.Count(f => f.Face.Role == MapFaceRole.SupportFloor));
        Assert.Equal(2, batch.CanonicalFaces.Count(f => f.Face.Role == MapFaceRole.Ceiling));
        Assert.Equal(.25, batch.Partitions.Where(p => p.PaintId == "paint-a").Sum(p => Area(p.Polygon)), 10);
        var set = Record(batch);
        AssertGeometry(batch, set);
        var ground = Assert.IsType<MapTerrainResource>(set.Resources.Single(r => r.Reference.Key.OwnerId == "ground").Payload);
        Assert.Equal(new ushort[] { 10, 11, 12 }, ground.Materials.Layers.Select(l => l.MaterialId));
        Assert.Contains(ground.Mesh.Vertices, v => v.Uv.X == 2 && v.Color.X == 1 && v.Tangent.W == 0);
        Assert.Contains(ground.Mesh.Vertices, v => v.Uv.X == 0 && v.Color.X > 0 && v.Color.X < 1 && v.Tangent.W == 2);
    }

    [Fact]
    public void ZeroUnderlay_OverlayKeepsFullCoverageWithoutAnInventedLayer()
    {
        var (closure, ids) = NativeRenderAssetFixtures.TexturelessMaterials(3);
        using var fixture = Fixture("terrain-zero-overlay", closure);
        using var input = Input(closure, ids[1]);
        using var batch = NativeMapTerrain.Prepare(Acquire(fixture), input, Materials);
        var set = Record(batch);
        var resource = Assert.IsType<MapTerrainResource>(Assert.Single(set.Resources).Payload);
        Assert.Equal((ushort)11, Assert.Single(resource.Materials.Layers).MaterialId);
        Assert.All(resource.Mesh.Vertices, v =>
        {
            Assert.Equal(new Vector4(1, 0, 0, 0), v.Color);
            Assert.Equal(Vector2.Zero, v.Uv);
            Assert.Equal(new Vector4(0, 0, 1, 0), v.Tangent);
        });
        AssertGeometry(batch, set);
    }

    [Fact]
    public void MaterialSets_AreIndependentPerCanonicalOwner()
    {
        var (closure, ids) = NativeRenderAssetFixtures.TexturelessMaterials(65);
        using var fixture = Fixture("terrain-many", closure);
        using var input = Input(closure, ids);
        var mapping = Enumerable.Range(0, 65).ToDictionary(i => (ushort)(10 + i), i => ids[i]);
        using var batch = NativeMapTerrain.Prepare(Acquire(fixture), input, mapping);
        var last = Record(batch).Resources.Single(r => r.Reference.Key.OwnerId == "last");
        Assert.Equal("patch/1/0/set/0", last.Reference.Key.PartId);
        Assert.Equal((ushort)74, Assert.Single(Assert.IsType<MapTerrainResource>(last.Payload).Materials.Layers).MaterialId);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LocalTransform_RefusesUnrepresentableGpuValues(bool scale)
    {
        var pose = scale ? new MapEffectiveTransform(0, 0, 0, 0, double.MaxValue)
            : new MapEffectiveTransform(double.MaxValue, 0, 0, 0, 1);
        Assert.Throws<MapDocumentException>(() => MapRenderTransform.ToLocal(pose, default));
    }

    [Theory]
    [InlineData(short.MinValue, short.MaxValue)]
    [InlineData(short.MaxValue, short.MinValue)]
    public void FocusLocal_PreservesSubUlpOffsetsAtFrameExtrema(short x, short z)
    {
        var frame = new WorldFrame(x, z);
        var point = new MapFramePoint(frame, new(.03125f, 500.03125f, -.03125f));
        var anchor = new MapSubmissionAnchor((long)x * 128, 500, (long)z * 128);
        Assert.Equal(new Vector3(.03125f, .03125f, -.03125f), MapRenderTransform.FocusLocal(point, anchor));
    }

    [Fact]
    public void AnchoredRigid_ActualRenderUploadRetainsLocalPrecision()
    {
        using var harness = new MotionTestScene();
        var scene = harness.Scene;
        var anchor = new RenderAnchor(32000, -500, -32000);
        var local = Matrix4x4.CreateTranslation(.123456f, .234567f, -.345678f);
        var mesh = scene.LoadMesh(new GltfMesh(new[]
        {
            new ModelVertex(Vector3.Zero, Vector3.UnitY, Vector4.One),
            new ModelVertex(Vector3.UnitX, Vector3.UnitY, Vector4.One),
            new ModelVertex(Vector3.UnitZ, Vector3.UnitY, Vector4.One),
        }, new uint[] { 0, 2, 1 }));
        scene.RenderOrigin = new(32000, -500, -32000);
        scene.Camera.Frame(new(32000, -500, -32000), new Vector3(4));
        scene.Begin();
        scene.DrawAnchored(new(mesh, local), anchor);
        harness.Render();
        var uploaded = Assert.IsType<List<ModelRenderer.InstanceData>>(typeof(Scene3D)
            .GetField("_instanceDataRelative", BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(scene));
        Assert.Equal(local, Assert.Single(uploaded).Model);
    }

    [Fact]
    public void AnchoredRigid_RetainsLocalPrecisionAcrossGroupingAndPreviousOrigins()
    {
        using var harness = new MotionTestScene();
        var scene = harness.Scene;
        scene.ForceTemporalForTests = true;
        var anchor = new RenderAnchor(32000, -500, -32000);
        var local = Matrix4x4.CreateTranslation(.123456f, .234567f, -.345678f);
        var key = MotionKey.From(91);
        scene.Begin();
        scene.DrawAnchored(new(new MeshHandle(3), local) { Motion = key }, anchor);
        scene.Draw(new(new MeshHandle(4), Matrix4x4.Identity));
        var data = new List<ModelRenderer.InstanceData>();
        var runs = new List<Scene3D.MeshRun>();
        var anchors = new List<AnchoredRigidTransform?>();
        Scene3D.GroupInstances(scene.QueuedInstancesForTests, data, runs, anchored: anchors);
        var relative = new List<ModelRenderer.InstanceData>();
        Scene3D.ReduceRigidInstances(data, anchors, new(32000, -500, -32000), relative);
        Assert.Equal(local, relative[0].Model);
        Assert.Equal(new Vector3(-32000, 500, 32000), relative[1].Model.Translation);
        scene.Begin();
        var previous = new List<Matrix4x4>();
        var slots = new float[1];
        Assert.Equal(1, RigidMotionSlots.Build(new[] { key }, scene.ActiveMotionHistory, new(31872, 0, -32000), slots, previous));
        Assert.Equal((float)(128d + local.M41), previous[0].M41);
        Assert.Equal((float)(-500d + local.M42), previous[0].M42);
        Assert.Equal(local.M43, previous[0].M43);
    }

    [Fact]
    public void AnchoredShadowSignatures_NoticeMovementBelowAbsoluteFloatPrecision()
    {
        var anchor = new RenderAnchor(32000, -500, -32000);
        var a = new AnchoredRigidTransform(Matrix4x4.CreateTranslation(.0001f, 0, 0), anchor);
        var b = new AnchoredRigidTransform(Matrix4x4.CreateTranslation(.0002f, 0, 0), anchor);
        Assert.Equal(a.Absolute, b.Absolute);
        var spans = new List<Scene3D.ShadowCasterSpan> { new(1, 1, 0, 1, ShadowCastKind.Opaque) };
        Assert.True(Scene3D.ShadowCastersChanged(spans,
            new List<Scene3D.ShadowCasterInstance> { new(a.Absolute, 0, Anchored: a) }, spans,
            new List<Scene3D.ShadowCasterInstance> { new(b.Absolute, 0, Anchored: b) }));
        ulong first = 17, second = 17;
        Scene3D.MixAnchoredPointSignature(ref first, a);
        Scene3D.MixAnchoredPointSignature(ref second, b);
        Assert.NotEqual(first, second);
    }

    static double Area(IReadOnlyList<MapExactPoint> polygon)
    {
        double sum = 0;
        for (int i = 1; i + 1 < polygon.Count; i++)
            sum += Math.Abs((polygon[i].X.ToDouble() - polygon[0].X.ToDouble()) * (polygon[i + 1].Z.ToDouble() - polygon[0].Z.ToDouble()) -
                (polygon[i].Z.ToDouble() - polygon[0].Z.ToDouble()) * (polygon[i + 1].X.ToDouble() - polygon[0].X.ToDouble())) / 2;
        return sum;
    }

    static void AssertGeometry(MapTerrainRenderBatch batch, MapDrawSet set)
    {
        double area = 0;
        foreach (var resource in set.Resources)
        {
            var payload = Assert.IsType<MapTerrainResource>(resource.Payload);
            var draw = set.Draws.Single(d => d.Resource == resource.Reference);
            Assert.True(draw.Channels.HasFlag(MapDrawChannels.Colour | MapDrawChannels.Shadow));
            var mesh = payload.Mesh;
            Assert.Equal(mesh.TriangleCount, payload.FaceKeys.Count);
            for (int i = 0; i < mesh.TriangleCount; i++)
            {
                var canonical = batch.CanonicalFaces.Single(f => f.Face.Key == payload.FaceKeys[i]);
                var t = canonical.Triangle;
                var a = Vector3d(t.A); var b = Vector3d(t.B); var c = Vector3d(t.C);
                var points = new List<Vector3>();
                for (int j = 0; j < 3; j++)
                {
                    var v = mesh.Vertices[mesh.Indices32[3 * i + j]];
                    double x = draw.Anchor.X + (double)v.Position.X, y = draw.Anchor.Y + (double)v.Position.Y,
                        z = draw.Anchor.Z + (double)v.Position.Z;
                    double plane = Math.Abs((x - a.X) * canonical.Face.Normal.X + (y - a.Y) * canonical.Face.Normal.Y + (z - a.Z) * canonical.Face.Normal.Z);
                    Assert.InRange(plane, 0, .0001);
                    Assert.InRange(x, Math.Min(a.X, Math.Min(b.X, c.X)) - .0001, Math.Max(a.X, Math.Max(b.X, c.X)) + .0001);
                    Assert.InRange(z, Math.Min(a.Z, Math.Min(b.Z, c.Z)) - .0001, Math.Max(a.Z, Math.Max(b.Z, c.Z)) + .0001);
                    var point = (X: x, Y: y, Z: z);
                    Assert.True(EdgeSide(a, b, point, canonical.Face.Normal) >= -.0001);
                    Assert.True(EdgeSide(b, c, point, canonical.Face.Normal) >= -.0001);
                    Assert.True(EdgeSide(c, a, point, canonical.Face.Normal) >= -.0001);
                    Assert.Equal(canonical.Face.Normal, v.Normal);
                    points.Add(v.Position);
                }
                area += Vector3.Cross(points[1] - points[0], points[2] - points[0]).Length() / 2d;
                Assert.True(Vector3.Dot(Vector3.Cross(points[1] - points[0], points[2] - points[0]), canonical.Face.Normal) > 0);
            }
        }
        double expectedArea = batch.CanonicalFaces.Sum(f =>
        {
            var a = Vector3d(f.Triangle.A); var b = Vector3d(f.Triangle.B); var c = Vector3d(f.Triangle.C);
            return Vector3.Cross(new((float)(b.X - a.X), (float)(b.Y - a.Y), (float)(b.Z - a.Z)),
                new((float)(c.X - a.X), (float)(c.Y - a.Y), (float)(c.Z - a.Z))).Length() / 2d;
        });
        Assert.InRange(Math.Abs(area - expectedArea), 0, .0001);
    }
    static double EdgeSide((double X, double Y, double Z) a, (double X, double Y, double Z) b,
        (double X, double Y, double Z) p, Vector3 normal) =>
        ((b.Y - a.Y) * (p.Z - a.Z) - (b.Z - a.Z) * (p.Y - a.Y)) * normal.X +
        ((b.Z - a.Z) * (p.X - a.X) - (b.X - a.X) * (p.Z - a.Z)) * normal.Y +
        ((b.X - a.X) * (p.Y - a.Y) - (b.Y - a.Y) * (p.X - a.X)) * normal.Z;
    static (double X, double Y, double Z) Vector3d(MapExactPoint p) => (p.X.ToDouble(), p.Y.ToDouble(), p.Z.ToDouble());
}
