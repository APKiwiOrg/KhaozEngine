using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.Tests.MapDoc.Scene;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.Render3D;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

[Collection("NativeDeviceLifecycle")]
public sealed class NativeMaterialLeaseTests
{
    [Fact]
    public void TexturedPartialPaint_RecordsAdmittedImageAndSecondFrameNeverDecodesOrUploads()
    {
        var closure = NativeRenderAssetFixtures.Create();
        using var sceneFixture = NativeSceneFixtures.Create("terrain-paint");
        sceneFixture.Document.NativeAssets = closure.Roots.ToList();
        using var f = new ResidencyFixture(sceneFixture.Document, closure);
        var state = f.State(ResidencyFixture.Window() with { Channels = MapSceneChannels.Terrain }) with
        { Policy = ResidencyFixture.Policy(materials: ["paint"], terrain: new Dictionary<ushort, string> { [10] = "paint", [11] = "paint", [12] = "paint" }) };
        using var host = new NativeUploadTestScene();
        var device = new MaterialDevice(host.Scene); var scheduler = new ResidencyScheduler();
        using var residency = new MapRenderResidency(state, device, null, scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        var plan = Assert.Single(residency.AcceptedPlans);
        Assert.Contains(plan.Inventory.Terrain.Partitions, p => p.PaintId == "paint-a");
        Assert.Equal(220, plan.Input.Materials["paint"].Maps.Albedo!.Value.Rgba[0]);
        Assert.NotEmpty(ResidencyFixture.Draw(residency).Draws);
        int decodes = KhaozEngine.Render2D.ImageDecodeAdmission.PixelDecodeEntries;
        long upload = host.Device.UploadBytes;
        host.Scene.Begin();
        var point = new MapFramePoint(WorldFrame.Origin, default);
        residency.Record(new(WorldFrame.Origin, point, MapRoofMode.AlwaysVisible, 2, 1, point), new MaterialSink(residency, host.Scene));
        host.Render();
        Assert.Equal(decodes, KhaozEngine.Render2D.ImageDecodeAdmission.PixelDecodeEntries);
        Assert.Equal(upload, host.Device.UploadBytes);
    }

    [Fact]
    public void MaterialOnlyReplacement_RefusesBeforeUnadmittedImageDecodeAndRetainsAcceptedDraw()
    {
        var closure = NativeRenderAssetFixtures.Create();
        var large = NativeRenderAssetFixtures.Create("resident");
        using var fixture = NativeSceneFixtures.Create("terrain-paint");
        using var changed = NativeSceneFixtures.Create("terrain-paint");
        fixture.Document.NativeAssets = closure.Roots.ToList(); changed.Document.NativeAssets = large.Roots.ToList();
        using var f = new ResidencyFixture(fixture.Document, closure);
        using var g = new ResidencyFixture(changed.Document, large);
        var scope = ResidencyFixture.Window() with { Channels = MapSceneChannels.Terrain };
        var policy = ResidencyFixture.Policy(materials: ["paint"], terrain: new Dictionary<ushort, string> { [10] = "paint", [11] = "paint", [12] = "paint" });
        var scheduler = new ResidencyScheduler(); var device = new ResidencyDevice();
        using var residency = new MapRenderResidency(f.State(scope) with { Policy = policy }, device,
            new(MaxResidentCpuBytes: 2_000_000, MaxPlanBytes: 500_000), scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        var accepted = residency.CurrentTransition.AcceptedWitness;
        var draws = ResidencyFixture.Draw(residency).Draws;
        int decodes = KhaozEngine.Render2D.ImageDecodeAdmission.PixelDecodeEntries, uploads = device.Begins;
        residency.RequestReplacement(g.State(scope) with { Policy = policy });
        scheduler.RunAll(); residency.Update();
        Assert.Equal(MapSceneStatus.CapacityExceeded, residency.CurrentTransition.Status);
        Assert.Equal(accepted, residency.CurrentTransition.AcceptedWitness);
        Assert.Equal(draws, ResidencyFixture.Draw(residency).Draws);
        Assert.Equal(decodes, KhaozEngine.Render2D.ImageDecodeAdmission.PixelDecodeEntries);
        Assert.Equal(uploads, device.Begins);
    }

    [Fact]
    public void EncodedClosureCharge_FollowsCachedInputAfterItsPlanOwnerEnds()
    {
        var closure = NativeRenderAssetFixtures.Create();
        var proofs = MapRenderExtentValidator.Validate(closure, ["prop-a"], new(new())).Proofs!;
        var admission = new MapRenderAdmission(new(MaxResidentCpuBytes: 100_000));
        var source = admission.TryReserveCpu(1024)!;
        using var cache = new MapRenderAssetCache(admission);
        using var input = cache.Acquire(closure, ["prop-a"], ["paint"], proofs, new(new(MaxPeakBytes: 65_536)),
            System.Threading.CancellationToken.None, source).Input!;
        long retained = admission.CpuBytes;
        Assert.True(retained > 1024);
        source.Dispose();
        Assert.Equal(retained, admission.CpuBytes);
        input.Dispose(); cache.Trim();
        Assert.Equal(0, admission.CpuBytes);
        source.Dispose();
        Assert.Equal(0, admission.CpuBytes);
    }

    [Fact]
    public void CompositeTerrain_AdmitsWallOnlyMaterialFromTheSelectedInput()
    {
        var (closure, _) = NativeRenderAssetFixtures.TexturelessMaterials(3);
        using var fixture = NativeSceneFixtures.Create("terrain-wall");
        fixture.Document.NativeAssets = closure.Roots.ToList();
        var patch = fixture.Document.Surfaces.Patches[new("ground", 0, 0)];
        int index = patch.Records.FindIndex(r => r is MapWallStrip);
        patch.Records[index] = ((MapWallStrip)patch.Records[index]) with { MaterialId = 12 };
        var surface = fixture.Document.Surfaces.Refs[0];
        fixture.Document.Surfaces.Refs[0] = surface with
        {
            SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface,
            [KeyValuePair.Create(patch.Key, MapSurfaceSemantics.PatchDigest(patch))])
        };
        using var f = new ResidencyFixture(fixture.Document, closure);
        var state = f.State(ResidencyFixture.Window() with { Channels = MapSceneChannels.Terrain }) with
        { Policy = ResidencyFixture.Policy(terrain: new Dictionary<ushort, string> { [10] = "mat-0", [12] = "mat-2" }) };
        var scheduler = new ResidencyScheduler(); var device = new ResidencyDevice();
        using var residency = new MapRenderResidency(state, device, null, scheduler);
        ResidencyFixture.Complete(residency, scheduler);
        var plan = Assert.Single(residency.AcceptedPlans);
        Assert.Equal(new[] { "mat-0", "mat-2" }, plan.Input.Materials.Keys.Order(StringComparer.Ordinal));
        Assert.Contains(plan.Inventory.Terrain.CanonicalFaces, f => f.Face.Role == MapFaceRole.Wall);
    }

    [Fact]
    public void PlanPreparation_TakesInputOnSuccessAndLeavesItWithCallerOnRefusal()
    {
        var closure = NativeRenderAssetFixtures.Create();
        using var fixture = NativeSceneFixtures.Create("terrain"); fixture.Document.NativeAssets = closure.Roots.ToList();
        using var f = new ResidencyFixture(fixture.Document, closure);
        var scope = ResidencyFixture.Window() with { Channels = MapSceneChannels.Terrain };
        var state = f.State(scope) with { Policy = ResidencyFixture.Policy(terrain: new Dictionary<ushort, string> { [10] = "paint" }) };
        var scene = state.Source.Acquire(scope).Scene!;
        using var bad = MapPreparedInputLease.LoadSelected(closure, [], [], state.Extents, new(new())).Input!;
        Assert.Throws<MapRenderAssetRefusal>(() => MapRenderPlan.Prepare(state.Source, scene, bad, state.Policy, 1));
        Assert.Equal(closure.Hash, bad.ClosureHash);
        var input = MapPreparedInputLease.LoadSelected(closure, [], ["paint"], state.Extents, new(new())).Input!;
        var plan = MapRenderPlan.Prepare(state.Source, scene, input, state.Policy, 2);
        Assert.Same(input, plan.Input);
        var recorder = new MapDrawRecorder(); plan.Register(recorder);
        var point = new MapFramePoint(WorldFrame.Origin, default);
        plan.Record(new(WorldFrame.Origin, point, MapRoofMode.AlwaysVisible, 0, 0, point), recorder);
        Assert.NotEmpty(recorder.Snapshot().Draws);
        plan.Dispose();
        Assert.Throws<ObjectDisposedException>(() => input.Retain());
    }

    [Fact]
    public void VerifiedMeshContent_SurvivesMaterialOnlyClosureChange()
    {
        var a = NativeRenderAssetFixtures.Create(); var b = NativeRenderAssetFixtures.Create("resident");
        var ap = MapRenderExtentValidator.Validate(a, ["prop-a"], new(new())).Proofs!;
        var bp = MapRenderExtentValidator.Validate(b, ["prop-a"], new(new())).Proofs!;
        using var cache = new MapRenderAssetCache(); var budget = new GltfDecodeBudget(new());
        using var before = cache.Acquire(a, ["prop-a"], [], ap, budget).Input!;
        int decodes = KhaozEngine.Render2D.ImageDecodeAdmission.PixelDecodeEntries;
        using var after = cache.Acquire(b, ["prop-a"], [], bp, budget).Input!;
        Assert.Same(before.Assets.ById["prop-a"], after.Assets.ById["prop-a"]);
        Assert.Equal(b.Hash, after.ClosureHash);
        Assert.Equal(1, before.Materials["paint"].Maps.Albedo!.Value.Width);
        Assert.Equal(256, after.Materials["paint"].Maps.Albedo!.Value.Width);
        Assert.Equal(1, KhaozEngine.Render2D.ImageDecodeAdmission.PixelDecodeEntries - decodes);
    }

    [Fact]
    public void OverlappingSelectedSets_ShareVerifiedPayloadsAndChargeThemOnce()
    {
        var closure = NativeRenderAssetFixtures.Create("foliage");
        var proofs = MapRenderExtentValidator.Validate(closure, ["A", "B"], new(new())).Proofs!;
        var budget = new GltfDecodeBudget(new());
        using var cache = new MapRenderAssetCache();
        using var a = cache.Acquire(closure, ["A"], [], proofs, budget).Input!;
        long first = budget.OwnedBytes;
        using var ab = cache.Acquire(closure, ["A", "B"], [], proofs, budget).Input!;
        Assert.Same(a.Assets.ById["A"], ab.Assets.ById["A"]);
        Assert.True(budget.OwnedBytes < first + ab.RetainedBytes);
    }

    [Fact]
    public void CacheRetainsOnlySelectedInputUntilLastPlanAndBorrow()
    {
        var closure = NativeRenderAssetFixtures.Create();
        var proofs = MapRenderExtentValidator.Validate(closure, ["prop-a"], new(new())).Proofs!;
        var budget = new GltfDecodeBudget(new());
        using var cache = new MapRenderAssetCache();
        using var first = cache.Acquire(closure, ["prop-a"], ["paint"], proofs, budget).Input!;
        using var borrow = first.Retain();
        using var second = cache.Acquire(closure, ["prop-a"], ["paint"], proofs, budget).Input!;
        long charge = budget.OwnedBytes;
        Assert.True(charge > 0); Assert.Equal(first.RetainedBytes, charge);
        Assert.Equal(charge, cache.RetainedBytes);
        first.Dispose(); second.Dispose(); cache.Trim();
        Assert.Equal(charge, budget.OwnedBytes);
        Assert.Equal(220, borrow.Materials["paint"].Maps.Albedo!.Value.Rgba[0]);
        borrow.Dispose(); cache.Trim();
        Assert.Equal(0, budget.OwnedBytes); Assert.Equal(0, cache.RetainedBytes);
    }
}

internal sealed class MaterialDevice(Scene3D scene) : IMapRenderDevice
{
    readonly Dictionary<MapResourceReference, SceneUploadData> _inputs = new();
    SceneUploadData Input(MapPreparedResource resource)
    {
        if (_inputs.TryGetValue(resource.Reference, out var input)) return input;
        var terrain = Assert.IsType<MapTerrainResource>(resource.Payload);
        var materials = terrain.Materials;
        input = new(terrain.Mesh, null, null, default, new SceneGroundMaterialData(materials.Width, materials.Height,
            materials.Layers.Select(l => new TileGroundLayerImage { AlbedoRgba = l.Albedo!.Value.Rgba }).ToArray()));
        _inputs.Add(resource.Reference, input); return input;
    }
    public SceneUploadDemand MeasureUpload(MapPreparedResource resource) => scene.MeasureResourceUpload(Input(resource));
    public IMapRenderUpload BeginUpload(MapPreparedResource resource, ISceneUploadReservation reservation) => new Upload(scene.PrepareResourceUpload(Input(resource), reservation));
    public void Release(MapRenderHandle handle) => scene.ReleaseResource(new(handle.Value));
    sealed class Upload(SceneResourceUpload upload) : IMapRenderUpload
    {
        public bool Ready => upload.Ready;
        public long Advance(long bytes) => upload.Advance(bytes);
        public MapRenderHandle Publish() => new(upload.Publish().Value);
        public void Dispose() => upload.Dispose();
    }
}
internal sealed class MaterialSink(MapRenderResidency residency, Scene3D scene) : IMapDrawSink
{
    public void Register(MapPreparedResource resource) { }
    public void Write(in MapDrawRecord draw)
    {
        Assert.True(residency.TryGetHandle(draw.Resource, out var handle));
        new SceneResourceAccess(scene).DrawResourceAnchored(new(handle.Value), new(default, draw.LocalTransform),
            new(draw.Anchor.X, draw.Anchor.Y, draw.Anchor.Z));
    }
}
