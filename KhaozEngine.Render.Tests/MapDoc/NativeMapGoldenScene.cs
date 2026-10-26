using System;
using System.Buffers.Binary;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Numerics;
using System.Reflection;
using System.Text;
using System.Text.Json.Nodes;
using KhaozEngine.Gpu;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Assets;
using KhaozEngine.MapDoc.Foliage;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Prefabs;
using KhaozEngine.MapDoc.Render3D;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.MapDoc.Spaces;
using KhaozEngine.MapDoc.Surfaces;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.Tests.MapDoc.Scene;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

/// <summary>One lazily created native scene for the invented capture families. Readiness precedes every temporal warm-up.</summary>
public sealed class NativeMapGoldenScene : IDisposable
{
    internal const int Width = 640, Height = 480, Warmup = 16;
    internal static readonly FoliageMotionProfile[] Profiles = [FoliageMotionProfile.Blade,
        new(.3f, .15f, .4f, .035f), new(.6f, .2f, .1f, .07f), new(.4f, .25f, .3f, .05f), new(.2f, .1f, .2f, .02f)];
    GpuDeviceContext? _gpu;
    IGpuTexture? _target;
    IGpuFramebuffer? _framebuffer;
    IGpuCommandList? _commands;
    Scene3D? _scene;
    internal NativeMapGoldenEvidence LastEvidence { get; private set; } = null!;
    internal Scene3D Scene => _scene!;
    internal static ShadowSettings Shadows() => new()
    {
        Mode = ShadowMode.ShadowMap,
        ShadowMapResolution = 1024,
        ShadowCascadeCount = 2,
        ShadowNearDistance = 16,
        ShadowMaxDistance = 64
    };
    void Open()
    {
        if (_gpu is not null) return;
        _gpu = GpuDeviceContext.CreateHeadless(); var device = _gpu.GpuDevice;
        _target = device.Factory.CreateTexture(GpuTextureDescription.Texture2D(Width, Height, GpuPixelFormat.R8G8B8A8UNorm,
            GpuTextureUsage.RenderTarget | GpuTextureUsage.Sampled));
        _framebuffer = device.Factory.CreateFramebuffer(null, _target); _commands = device.Factory.CreateCommandList();
        _scene = new(device, _framebuffer.Outputs, Shadows());
    }
    public Render3DCapture Capture(string variant, bool omitFeature = false, bool farOrigin = false) =>
        Capture(variant, omitFeature, farOrigin, MapRoofMode.AlwaysHidden, null);
    internal Render3DCapture Capture(string variant, bool omitFeature, bool farOrigin, MapRoofMode roofs, Vector3? focus,
        bool omitRoofShadow = false, bool omitFoliagePatch = false, bool omitFarLight = false)
    {
        Open();
        using var setup = NativeMapGoldenSetup.Create(variant);
        var state = setup.State;
        var scheduler = new ResidencyScheduler(); var sink = new MapScene3DSink(Scene);
        using var residency = new MapRenderResidency(state, sink, null, scheduler);
        using var view = new NativeMapView(Scene, residency);
        // A held preparation has neither foliage nor HLOD draws and cannot be made ready by temporal frames.
        var waiting = residency.InspectReadiness(); Assert.True(waiting.Pending > 0); Assert.Equal(0, waiting.Ready);
        var before = new MapDrawRecorder(); residency.Record(setup.Frame, before); Assert.Empty(before.Snapshot().Draws);
        scheduler.RunAll(); var ready = view.SettleForCapture(1024);
        Assert.Equal(MapSceneStatus.CompleteForScope, ready.Transition.Status); Assert.Equal(0, ready.Pending);
        var frame = setup.Frame with { RoofMode = roofs };
        if (focus is { } f) frame = frame with { Focus = new(frame.Frame, f) };
        Configure(setup.Framing, variant, farOrigin);
        long uploads = residency.Lifetime.UploadBytes, created = residency.Lifetime.Created;
        var filter = new NativeMapGoldenSubmission(sink, variant.StartsWith("water-", StringComparison.Ordinal) ? "water" : variant == "roof-hlod" ? "roofs" : variant, omitFeature, omitRoofShadow, omitFarLight);
        var expectedFoliage = ValidatePreparedFoliage(Scene, residency, frame);
        var frames = new NativeMapGoldenFrame[Warmup + 1];
        for (int i = 0; i <= Warmup; i++)
        {
            Scene.Begin(); sink.BeginFrame(); filter.Reset();
            Scene.EffectTimeSeconds = 12;
            residency.Record(frame, filter);
            if (omitFoliagePatch)
            {
                var patches = (System.Collections.IList)typeof(Scene3D).GetField("_foliageDraws", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(Scene)!;
                Assert.True(patches.Count > 1); patches.RemoveAt(0);
            }
            Scene.PrepareFrame();
            using (GpuRecording.Open(_gpu!.GpuDevice, _commands!, "NativeMapGoldenScene.Capture"))
                Scene.RenderInternal(_commands!, Width, Height, _framebuffer!);
            _gpu.GpuDevice.Submit(_commands!);
            frames[i] = new(Scene.LastShadowPassDiagnostics, Scene.ShadowOnlyInstances, filter.Draws.ToArray());
        }
        _gpu!.GpuDevice.WaitForIdle();
        Assert.Equal(uploads, residency.Lifetime.UploadBytes); Assert.Equal(created, residency.Lifetime.Created);
        Assert.Equal(0, Scene.LastFoliageStats.InstanceUploadBytes);
        byte[] rgba = GpuReadback.ToRgba(_gpu.GpuDevice, _target!, Width, Height);
        var inventory = SnapshotInventory(residency, frame);
        int actualLights = Scene.PointLightClusters.SubmittedLightCount;
        Assert.Equal(Scene.LightCount, actualLights);
        if (variant == "origin" && !omitFarLight)
        {
            NativeMapGoldenAssertions.FarLightDraw(filter.Draws, Scene.CurrentFrameView.RenderOrigin);
            NativeMapGoldenAssertions.FarLightSubmitted(Scene, Scene.CurrentFrameView.RenderOrigin);
        }
        LastEvidence = new(inventory, filter.Draws.ToArray(), Scene.DrawnInstances, Scene.ShadowOnlyInstances,
            actualLights, Scene.LastShadowPassDiagnostics.RigidDrawCalls, ready, uploads, created, setup.Framing,
            Scene.FoliageUniformsForTests.ToArray().Select(u => u.FocusRadius).ToArray(),
            residency.AcceptedPlans.SelectMany(p => p.Inventory.Foliage.GeneratedIdentities).ToArray())
        {
            ExpectedFoliageInstances = expectedFoliage.Instances,
            ExpectedFoliagePatches = expectedFoliage.Patches,
            SubmittedFoliageInstances = Scene.LastFoliageStats.CandidateInstances,
            SubmittedFoliagePatches = Scene.LastFoliageStats.SubmittedPatches,
            Frames = frames
        };
        return new(rgba, Width, Height, _gpu.GpuDevice.Backend);
    }
    internal static MapDrawSet SnapshotInventory(MapRenderResidency residency, MapFrameState frame)
    {
        var recorder = new MapDrawRecorder();
        foreach (var plan in residency.AcceptedPlans) plan.Register(recorder);
        residency.Record(frame, recorder);
        return recorder.Snapshot();
    }
    void Configure(MapSnapshotFraming framing, string variant, bool farOrigin)
    {
        Scene.CameraOverride = framing.Camera;
        Scene.RenderOrigin = variant == "origin" ? farOrigin ? new(31872, 0, -31872) : new(32000, 0, -32000) : Vector3.Zero;
        Scene.Post.Starfield = false; Scene.FrustumCulling = false;
        Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        Scene.Post.Temporal.Upscale = TemporalUpscale.Native;
        Scene.Post.LightDirection = -Vector3.Normalize(new(-1, 1, -.5f));
        Scene.Post.LightColor = Color.White;
        // Keep the same Halton phases as a fresh scene. CameraCut clears history, not the frame counter.
        typeof(Scene3D).GetField("_frameIndex", BindingFlags.NonPublic | BindingFlags.Instance)!.SetValue(Scene, 0L);
        Scene.CameraCut();
    }
    internal static (int Instances, int Patches) ValidatePreparedFoliage(Scene3D scene, MapRenderResidency residency, MapFrameState frame)
    {
        var recorder = new MapDrawRecorder(); residency.Record(frame, recorder);
        var data = recorder.Snapshot(); int instances = 0, patchCount = 0;
        foreach (var resource in data.Resources.Where(r => r.Payload is MapFoliageResource))
        {
            var source = (MapFoliageResource)resource.Payload;
            Assert.True(residency.TryGetHandle(resource.Reference, out var handle));
            var prepared = scene.ResourceOf(new(handle.Value)).PreparedFoliage!;
            MeshHandle Resolve(MapResourceReference reference)
            {
                Assert.True(residency.TryGetHandle(reference, out var part));
                return new SceneResourceAccess(scene).MeshOf(new(part.Value));
            }
            var counts = NativeMapFoliageEvidence.AssertComplete(source, prepared.ExpandedParts, prepared.Patches, prepared.PackedInstances, Resolve);
            instances += counts.Instances; patchCount += counts.Patches;
        }
        return (instances, patchCount);
    }
    internal void CaptureFocusPair()
    {
        Open(); using var setup = NativeMapGoldenSetup.Create("focus");
        var sink = new MapScene3DSink(Scene); var scheduler = new ResidencyScheduler();
        using var residency = new MapRenderResidency(setup.State, sink, null, scheduler);
        scheduler.RunAll(); using var view = new NativeMapView(Scene, residency); Assert.Equal(0, view.SettleForCapture(1024).Pending);
        var plans = residency.AcceptedPlans.ToArray();
        long uploads = residency.Lifetime.UploadBytes, created = residency.Lifetime.Created;
        var resources = plans.SelectMany(p => p.Resources).Where(r => r.Payload is MapFoliageResource).ToArray();
        var resource = Assert.Single(resources); var anchor = ((MapFoliageResource)resource.Payload).Anchor;
        Assert.True(residency.TryGetHandle(resource.Reference, out var handle));
        var product = Scene.ResourceOf(new(handle.Value)).PreparedFoliage;
        Render3DCapture? first = null, firstBare = null; int[]? firstLods = null;
        foreach (float x in new[] { 0f, 5f })
        {
            var frame = setup.Frame with { Focus = new(WorldFrame.Origin, new(x, 0, 6)) };
            var capture = Render(false);
            var uniforms = Scene.FoliageUniformsForTests.ToArray(); Assert.NotEmpty(uniforms);
            Assert.All(uniforms, u => Assert.Equal(new Vector4((float)(x - anchor.X), (float)-anchor.Y, (float)(6 - anchor.Z), 4), u.FocusRadius));
            var recorder = new MapDrawRecorder(); residency.Record(frame, recorder);
            int[] lods = recorder.Snapshot().Draws.Where(d => d.Resource.Key.OwnerId == "lod-prop").Select(d => d.Resource.Key.Lod).Distinct().ToArray();
            var bare = Render(true);
            if (first is null) { first = capture; firstBare = bare; firstLods = lods; }
            else
            {
                NativeMapGoldenAssertions.FeatureVisible(bare, firstBare!.Value);
                NativeMapGoldenAssertions.FoliageAfterBare(first.Value, firstBare.Value, capture, bare);
                Assert.NotEqual(firstLods, lods);
            }
            Assert.Equal(plans, residency.AcceptedPlans); Assert.Equal(uploads, residency.Lifetime.UploadBytes); Assert.Equal(created, residency.Lifetime.Created);
            Assert.Equal(0, Scene.LastFoliageStats.InstanceUploadBytes); Assert.Same(product, Scene.ResourceOf(new(handle.Value)).PreparedFoliage);

            Render3DCapture Render(bool omitFoliage)
            {
                Configure(setup.Framing, "focus", false);
                var filter = new NativeMapGoldenSubmission(sink, "foliage", omitFoliage, false);
                for (int i = 0; i <= Warmup; i++)
                {
                    Scene.Begin(); sink.BeginFrame(); view.Update(frame); filter.Reset(); residency.Record(frame, filter); Scene.PrepareFrame();
                    using (GpuRecording.Open(_gpu!.GpuDevice, _commands!, "NativeMapGoldenScene.Focus"))
                        Scene.RenderInternal(_commands!, Width, Height, _framebuffer!);
                    _gpu.GpuDevice.Submit(_commands!);
                }
                _gpu!.GpuDevice.WaitForIdle();
                return new(GpuReadback.ToRgba(_gpu.GpuDevice, _target!, Width, Height), Width, Height, _gpu.GpuDevice.Backend);
            }
        }
    }
    internal static Render3DCapture Fresh(string variant, bool farOrigin = false)
    { using var fresh = new NativeMapGoldenScene(); return fresh.Capture(variant, farOrigin: farOrigin); }
    public void Dispose()
    {
        if (_gpu is null) return;
        _gpu.GpuDevice.WaitForIdle(); _scene!.Dispose(); _commands!.Dispose(); _framebuffer!.Dispose(); _target!.Dispose();
        _gpu.Dispose(); _gpu = null;
    }
}

internal sealed record NativeMapGoldenFrame(ShadowPassDiagnostics Shadow, int ShadowOnlyInstances, MapDrawRecord[] Submitted);

internal sealed record NativeMapGoldenEvidence(MapDrawSet Inventory, MapDrawRecord[] Submitted, int ColourInstances,
    int ShadowOnlyInstances, int Lights, int ShadowDrawCalls, MapRenderReadiness Ready, long UploadBytes, long Created,
    MapSnapshotFraming Framing, Vector4[] FoliageUniforms, MapFoliageSampleKey[] SampleKeys)
{
    internal int ExpectedFoliageInstances { get; init; }
    internal int ExpectedFoliagePatches { get; init; }
    internal int SubmittedFoliageInstances { get; init; }
    internal int SubmittedFoliagePatches { get; init; }
    internal NativeMapGoldenFrame[] Frames { get; init; } = [];
}

/// <summary>Omission controls remove the actual submitted feature, after complete preparation and publication.</summary>
internal sealed class NativeMapGoldenSubmission(MapScene3DSink sink, string variant, bool omit, bool omitRoofShadow, bool omitFarLight = false) : IMapDrawSink
{
    internal List<MapDrawRecord> Draws { get; } = [];
    internal void Reset() => Draws.Clear();
    public void Register(MapPreparedResource resource) => sink.Register(resource);
    public void Write(in MapDrawRecord draw)
    {
        bool feature = variant switch
        {
            "paint" => draw.Resource.Key.Kind == MapRenderKind.Terrain && draw.Resource.Key.OwnerId == "ground",
            "props" => draw.Resource.Key.Kind == MapRenderKind.Placement,
            "roofs" => draw.Resource.Key.Kind == MapRenderKind.Roof,
            "water" => draw.Resource.Key.Kind == MapRenderKind.Water,
            "foliage" or "origin" => draw.Foliage.HasValue,
            _ => throw new ArgumentException(variant)
        };
        if (omit && feature || omitFarLight && draw.Resource.Key.OwnerId == "far-light") return;
        var value = omitRoofShadow && draw.Resource.Key.Kind == MapRenderKind.Roof ? draw with { Channels = draw.Channels & ~MapDrawChannels.Shadow } : draw;
        sink.Write(value); Draws.Add(value);
    }
}

internal sealed class NativeMapGoldenSetup : IDisposable
{
    readonly ResidencyFixture _fixture;
    internal MapRenderSourceState State { get; }
    internal MapSnapshotFraming Framing { get; }
    internal MapFrameState Frame { get; }
    NativeMapGoldenSetup(ResidencyFixture fixture, MapRenderSourceState state, MapSnapshotFraming framing, MapFrameState frame)
    { _fixture = fixture; State = state; Framing = framing; Frame = frame; }
    internal static NativeMapGoldenSetup Create(string variant)
    {
        if (variant == "origin")
        {
            var realization = NativeOriginRealizationFixtures.Create();
            var owner = new ResidencyFixture(realization.Scene.Document, realization.Scene.Assets);
            var state = owner.State(NativeOriginRealizationFixtures.Scope()) with { Policy = realization.Policy };
            return new(owner, state, realization.Framing, new(realization.PickOrigin.Frame, realization.PickOrigin,
                MapRoofMode.AlwaysHidden, 12, 17, realization.PickOrigin));
        }
        var closure = NativeRenderAssetFixtures.GoldenAssets();
        using var native = variant == "water" ? NativeSceneFixtures.Create("water-bent-one") : variant.StartsWith("water-", StringComparison.Ordinal) ? NativeSceneFixtures.Create(variant) : NativeSceneFixtures.Create("empty");
        var doc = native.Document; doc.NativeAssets = closure.Roots.ToList(); doc.TileSize = 1;
        MapSnapshotFraming framing;
        var materials = new Dictionary<ushort, string> { [1] = "gold-floor", [10] = "gold-floor", [11] = "gold-overlay", [12] = "gold-paint" };
        var placements = new MapPlacementPolicy([], 256);
        if (variant == "paint")
        {
            doc.Surfaces = new();
            AddSurface(doc, "ground", MapSurfaceRole.SupportFloor, 4, 4, 0, (x, z) =>
                new(10, 11, (MapOverlayCut)x, (byte)z, MapCellFlags.FeatherOverlay, MapCellTopology.Auto));
            AddSurface(doc, "partial", MapSurfaceRole.PaintOverride, 1, 1, 0, (_, _) => new(12, 0, MapOverlayCut.Full, 0, 0, MapCellTopology.ForceSwNe), "ground");
            doc.NativeResourceTransforms = [new(MapPrefabChildKind.Surface, "partial", new(.25, 0, .25, .371, .5), new string('c', 64))];
            AddSurface(doc, "ceiling-edge", MapSurfaceRole.Ceiling, 4, 1, 150, (_, _) => new(10, 0, MapOverlayCut.Full, 0, 0, MapCellTopology.ForceSwNe));
            doc.NativeResourceTransforms = [.. doc.NativeResourceTransforms, new(MapPrefabChildKind.Surface, "ceiling-edge", new(0, 0, 4.5, 0, 1), new string('d', 64))];
            framing = Camera(new(2, 8, 9), new(2, 0, 2), 7);
        }
        else if (variant is "props" or "roofs" or "roof-hlod" or "foliage" or "focus")
        {
            doc.Surfaces = new();
            int groundDepth = variant == "roof-hlod" ? 4 : 12;
            AddSurface(doc, "ground", MapSurfaceRole.SupportFloor, 16, groundDepth, 0, (_, _) => new(10, 0, MapOverlayCut.Full, 0, 0, MapCellTopology.ForceSwNe));
            if (variant == "props")
            {
                doc.Placements = [Placement("building-0", "building", 3, 0, 4, 1), Placement("building-1", "building", 8, 0, 4, 2),
                    Placement("building-2", "building", 13, 0, 4, 3)]; doc.NumericIdHighWaterMark = 3;
                placements = new([new("buildings", new HashSet<string> { "building" }, [3, 8], 1, 256, 0, 0, 0, true)], 256);
                framing = Camera(new(8, 9, 18), new(8, 1, 4), 12);
            }
            else if (variant is "roofs" or "roof-hlod")
            {
                doc.Placements = [Placement("solid", "building", 10, 0, 7, 1), Placement("roof-a", "roof", 2, 3, 2, 2)];
                doc.NumericIdHighWaterMark = 2;
                var patch = doc.Surfaces.Patches.Values.Single();
                patch.Records.Add(new MapSpaceDoc("outside", MapSpaceKind.Exterior, null, null, [], [], [], []));
                var parent = new MapRecordRef("outside", patch.Key);
                patch.Records.Add(new MapSpaceFootprint("outside-footprint", parent, patch.Key,
                    Enumerable.Range(0, groundDepth).SelectMany(z => Enumerable.Range(0, 16).Select(x => z * 64 + x)).ToArray(),
                    new(MapBoundKind.SupportFloor, "ground", null), new(MapBoundKind.OpenTop, null, null)));
                patch.Records.Add(new MapSpaceDoc("room", MapSpaceKind.Room, parent, null, [], [], [], []));
                patch.Records.Add(new MapPrismDomain("room-domain", 1, new("room", patch.Key), parent, "", "storey",
                    new(MapLatticeFrame.ImportedMetreCentimetre, [new(0, -4, 4, 0)], []), 0, 4));
                MapPrefabReference Ref(string id, MapPrefabChildKind kind) => new(MapPrefabReferenceScope.World, kind, id);
                doc.BuildingIntent = new(1, [new("roof-link", Ref("roof-a", MapPrefabChildKind.Placement), [Ref("room", MapPrefabChildKind.Record)], [], 4)], [],
                    Enumerable.Range(0, 17).Select(i => new MapLocalLightDoc(1, "light-" + i, null, new(8 + i * .1f, 4, 6), new(.2f, .2f, .2f), .1f, 8, false)).ToArray(), [])
                { Roofs = [Ref("roof-a", MapPrefabChildKind.Placement)], Storeys = [new(1, "storey", "Storey", null)] };
                placements = new([new("buildings", new HashSet<string> { "building" }, [3, 8], 1, 256, 30, 10, 0, true)], 256);
                if (variant == "roof-hlod")
                {
                    doc.Placements.Add(Placement("roof-b", "roof", 10, 3, 2, 3)); doc.NumericIdHighWaterMark = 3;
                    patch.Records.Add(new MapSpaceDoc("room-b", MapSpaceKind.Room, parent, null, [], [], [], []));
                    patch.Records.Add(new MapPrismDomain("room-b-domain", 1, new("room-b", patch.Key), parent, "", "storey",
                        new(MapLatticeFrame.ImportedMetreCentimetre, [new(8, -4, 12, 0)], []), 0, 4));
                    var intent = doc.BuildingIntent;
                    doc.BuildingIntent = new(1, [.. intent.RoofLinks, new("roof-b-link", Ref("roof-b", MapPrefabChildKind.Placement), [Ref("room-b", MapPrefabChildKind.Record)], [], 4)],
                        [], intent.Lights, [])
                    { Roofs = [.. intent.Roofs, Ref("roof-b", MapPrefabChildKind.Placement)], Storeys = intent.Storeys };
                    placements = new([.. placements.Layers, new("caps", new HashSet<string> { "roof" }, [], 0, 256, 30, 10, 0, true)], 256);
                }
                framing = Camera(new(8, 20, 6), new(8, 0, 6), 14);
            }
            else
            {
                var bytes = new byte[1024];
                for (int z = 0; z < 8; z++) for (int x = 0; x < 12; x++) bytes[z * 32 + x] = 255;
                doc.Foliage = new(1, [new(1, "five-styles", new(MapSupportBindingKind.Surface, "ground", null, null, null, null),
                    2, 2, 1, 12, 8, MapRowDirection.PositiveZ, 17, .8f, .8f, 1.4f, -.01f,
                    new[] { "A", "B", "C", "D", "E" }.Select(id => new MapFoliageModel(id, 1)).ToArray(), [10],
                    [new(10, "gold-floor", MapFoliageMaterialKind.Ordinary)], new("legacy-tile", 1, 0), false, false, 0, 0,
                    [new("ground", 0)], [], [], [], [new(new(0, 0), bytes)])]);
                if (variant == "focus")
                {
                    doc.Placements = [Placement("lod-prop", "building", 8, 0, 6, 1)]; doc.NumericIdHighWaterMark = 1;
                    placements = new([new("focus-prop", new HashSet<string> { "building" }, [3, 8], 1, 256, 0, 0, 0, true)], 256);
                }
                framing = Camera(new(8, 6, 18), new(8, 0, 6), 12);
            }
            Rehash(doc);
        }
        else if (variant == "water" || variant.StartsWith("water-", StringComparison.Ordinal)) framing = Camera(new(5, 16, 3.5f), new(5, 0, 3.5f), 10);
        else throw new ArgumentException(variant);
        var options = new GroundCoverRenderOptions
        {
            UseGpuBatches = true,
            FadeMode = GroundCoverFadeMode.HeightScale,
            DrawRadius = variant == "focus" ? 4 : 256,
            DensityRadius = variant == "focus" ? 2 : 200,
            WindStrength = .7f,
            MotionProfiles = new Dictionary<string, FoliageMotionProfile>
            {
                ["A"] = NativeMapGoldenScene.Profiles[0],
                ["B"] = NativeMapGoldenScene.Profiles[1],
                ["C"] = NativeMapGoldenScene.Profiles[2],
                ["D"] = NativeMapGoldenScene.Profiles[3],
                ["E"] = NativeMapGoldenScene.Profiles[4]
            },
            ModelCastsShadows = new Dictionary<string, bool> { ["A"] = true, ["B"] = false, ["C"] = true, ["D"] = true, ["E"] = true },
            Interactors = [new(new(8, 0, 6), 4, .7f, .5f)]
        };
        var foliage = new NativeFoliagePolicy(MapFrozenFoliageOptions.Capture(options), closure.Assets.Where(a => a.Id.Length == 1)
            .ToDictionary(a => a.Id, a => new MapFoliageModelExtent(a.Id, a.RenderBounds, a.SourceUnitsToMetres, .5f)));
        var policy = new MapRenderPolicy(placements, new HashSet<string>(), foliage,
            MapFrozenWaterLook.Capture(new WaterLook { SwellAmplitude = 0, WaveSource = WaterWaveSource.Procedural }),
            materials.Values.Distinct().ToArray(), materials);
        var fixture = new ResidencyFixture(doc, closure);
        var scope = new MapSceneScope(new(WorldFrame.Origin, new(-64), new(64), -64, 64, Enum.GetValues<MapSurfaceRole>(), null, new()),
            new(-64, -64, -64, 64, 64, 64), MapSceneChannels.All, 1);
        var render = fixture.State(scope) with { Policy = policy };
        var observer = new MapFramePoint(WorldFrame.Origin, new(2, 1, 2));
        return new(fixture, render, framing, new(WorldFrame.Origin, observer, MapRoofMode.AlwaysHidden, 12, 17,
            new(WorldFrame.Origin, variant == "props" ? new(3, 0, 4) : new(8, 0, 6))));
    }
    static MapPlacement Placement(string id, string asset, float x, float y, float z, int numeric) => new()
    { Id = id, AssetId = asset, NumericId = numeric, Kind = "invented", X = x, Y = y, Z = z, Scale = 1.2f, Yaw = .371f };
    static MapSnapshotFraming Camera(Vector3 eye, Vector3 target, float size) =>
        new(new(WorldFrame.Origin, eye), new(WorldFrame.Origin, target), true, size, 640, 480, .1f, 256);
    static void AddSurface(MapDocument doc, string id, MapSurfaceRole role, int width, int depth, int height,
        Func<int, int, MapSurfaceCell> cell, string? target = null)
    {
        var patch = new MapSurfacePatch
        {
            Key = new(id, 0, 0),
            Width = width,
            Depth = depth,
            Heights = Enumerable.Repeat(height, (width + 1) * (depth + 1)).ToArray(),
            Cells = Enumerable.Range(0, depth).SelectMany(z => Enumerable.Range(0, width).Select(x => cell(x, z))).ToArray(),
            Presence = Enumerable.Repeat(ulong.MaxValue, (width * depth + 63) / 64).ToArray()
        };
        if (width * depth % 64 != 0) patch.Presence[^1] = (1UL << (width * depth % 64)) - 1;
        doc.Surfaces.Patches.Add(patch.Key, patch);
        doc.Surfaces.Refs.Add(new(id, MapLatticeFrame.ImportedMetreCentimetre with { RowDirection = MapRowDirection.PositiveZ },
            role, MapPresencePolicy.Native, target, null, "")); Rehash(doc);
    }
    static void Rehash(MapDocument doc)
    {
        for (int i = 0; i < doc.Surfaces.Refs.Count; i++)
        {
            var surface = doc.Surfaces.Refs[i];
            doc.Surfaces.Refs[i] = surface with
            {
                SemanticSha256 = MapSurfaceSemantics.SurfaceDigest(surface,
                doc.Surfaces.Patches.Where(p => p.Key.SurfaceId == surface.Id).Select(p => KeyValuePair.Create(p.Key, MapSurfaceSemantics.PatchDigest(p.Value))))
            };
        }
    }
    public void Dispose() => _fixture.Dispose();
}

internal static partial class NativeRenderAssetFixtures
{
    internal static MapAssetClosure GoldenAssets()
    {
        var source = new Source(); var resources = new JsonArray(); var assets = new JsonArray();
        void Resource(string id, string kind, byte[] bytes, params string[] dependencies)
        {
            var r = source.Add(id, bytes);
            resources.Add(new JsonObject
            {
                ["reference"] = new JsonObject
                {
                    ["id"] = r.Id,
                    ["path"] = r.Path,
                    ["sha256"] = r.Sha256,
                    ["payloadVersion"] = 1
                },
                ["kind"] = kind,
                ["dependencies"] = new JsonArray(dependencies.Select(d => (JsonNode?)JsonValue.Create(d)).ToArray())
            });
        }
        Resource("texture", "Material", Png());
        foreach (var (id, tint) in new[] { ("gold-floor", "[0.65,0.65,0.65,1]"), ("gold-overlay", "[0.15,0.8,0.2,1]"), ("gold-paint", "[0.2,0.3,1,1]") })
            Resource(id, "Material", Encoding.UTF8.GetBytes("{\"schema\":\"kemap/render-material\",\"payloadVersion\":1,\"baseColour\":" + tint +
                ",\"roughness\":1,\"metallic\":0,\"alphaCutoff\":0,\"albedo\":\"texture\",\"normal\":null,\"roughnessMap\":null}"), "texture");
        foreach (string id in new[] { "building", "roof", "A", "B", "C", "D", "E" })
        {
            Vector3 min = id == "roof" ? new(-2, 0, -2) : id == "building" ? new(-1, 0, -1) : new(-.15f, 0, -.15f);
            Vector3 max = id == "roof" ? new(2, .3f, 2) : id == "building" ? new(1, 2, 1) : new(.15f, .8f, .15f);
            Resource(id + "-mesh", "Mesh", GoldenMesh(min, max, id != "B"));
            if (id == "building") for (int lod = 1; lod <= 2; lod++) Resource(id + "-lod-" + lod, "Lod", GoldenMesh(min, max - new Vector3(0, lod * .2f, 0), true));
            JsonObject Bounds() => new()
            {
                ["min"] = new JsonObject { ["x"] = min.X, ["y"] = min.Y, ["z"] = min.Z },
                ["max"] = new JsonObject { ["x"] = max.X, ["y"] = max.Y, ["z"] = max.Z }
            };
            assets.Add(new JsonObject
            {
                ["id"] = id,
                ["meshResourceId"] = id + "-mesh",
                ["sourceUnitsToMetres"] = 1,
                ["supportResourceIds"] = new JsonArray(),
                ["materialResourceIds"] = new JsonArray(),
                ["lodResourceIds"] = id == "building" ? new JsonArray("building-lod-1", "building-lod-2") : new JsonArray(),
                ["lightResourceIds"] = new JsonArray(),
                ["renderBounds"] = Bounds(),
                ["lodBounds"] = id == "building" ? Bounds() : null,
                ["source"] = "invented.blend",
                ["license"] = "CC0",
                ["textured"] = true
            });
        }
        var root = source.Add("root", Encoding.UTF8.GetBytes(new JsonObject
        {
            ["payloadVersion"] = 1,
            ["resources"] = resources,
            ["assets"] = assets
        }.ToJsonString(MapJsonTextOptions.CreateSerializer(false))));
        return MapAssetClosure.Load([root], source);
    }
    static byte[] GoldenMesh(Vector3 min, Vector3 max, bool multipart)
    {
        var glb = BoxMesh(min, max); int length = BinaryPrimitives.ReadInt32LittleEndian(glb.AsSpan(12));
        var root = JsonNode.Parse(glb.AsSpan(20, length))!.AsObject(); var buffer = glb.AsSpan(28 + length).ToArray();
        var primitives = root["meshes"]![0]!["primitives"]!.AsArray();
        if (multipart)
        {
            // Separate lower/upper triangle ranges share a single embedded verified texture.
            root["accessors"]![2]!["count"] = 18;
            root["accessors"]!.AsArray().Add(new JsonObject
            {
                ["bufferView"] = 2,
                ["byteOffset"] = 72,
                ["componentType"] = 5125,
                ["count"] = 18,
                ["type"] = "SCALAR"
            });
            var second = primitives[0]!.DeepClone(); second["indices"] = 3; second["material"] = 1; primitives.Add(second);
        }
        primitives[0]!["material"] = 0;
        var image = Png(); var binary = new byte[buffer.Length + image.Length]; buffer.CopyTo(binary, 0); image.CopyTo(binary, buffer.Length);
        root["bufferViews"]!.AsArray().Add(new JsonObject { ["buffer"] = 0, ["byteOffset"] = buffer.Length, ["byteLength"] = image.Length });
        root["images"] = new JsonArray(new JsonObject { ["bufferView"] = 3, ["mimeType"] = "image/png" });
        root["textures"] = new JsonArray(new JsonObject { ["source"] = 0 });
        root["materials"] = JsonNode.Parse("""
        [{"pbrMetallicRoughness":{"baseColorTexture":{"index":0}}},
         {"pbrMetallicRoughness":{"baseColorFactor":[0.2,0.5,1,1],"baseColorTexture":{"index":0}}}]
        """);
        root["buffers"]![0]!["byteLength"] = binary.Length;
        return Pack(root, binary);
    }
}
