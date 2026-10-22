using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.MapDoc;
using KhaozEngine.MapDoc.Interiors;
using KhaozEngine.MapDoc.Scene;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Terrain;
using KhaozEngine.Tests.Gpu;
using KhaozEngine.MapDoc.Render3D;
using Xunit;

namespace KhaozEngine.Tests.MapDoc;

[Collection("NativeDeviceLifecycle")]
public sealed partial class NativeMapReadinessTests
{
    [Fact]
    public void IndexedCapture_PointAStaticKeySurvivesPreparationDescriptorAndDeviceSink()
    {
        // point-a: signed Int64 little-endian from the first eight SHA-256 bytes of its UTF-8 ID.
        // Task 13 consumes this fixed source-derived descriptor key (T12-R1).
        const long pointAKey = 3068690713473075190;
        var request = CaptureDependencyFixtures.Request();
        var prepared = MapCapturePreparation.Prepare(request);
        Assert.Equal(MapSceneStatus.CompleteForScope, prepared.Status);
        var point = new MapFramePoint(WorldFrame.Origin, Vector3.Zero);
        var frame = new MapFrameState(WorldFrame.Origin, point, request.RoofMode, request.TimeSeconds, request.WindSeed, request.Focus!.Value);
        var mapped = new MapDrawRecorder();
        foreach (var scope in prepared.Witness.AcquiredScopes)
            NativeMapLights.Record(request.Source.Acquire(scope).Scene!, frame, mapped);
        var descriptors = mapped.Snapshot().Draws.Where(d => d.Resource.Key.OwnerId == "point-a").ToArray();
        Assert.NotEmpty(descriptors); Assert.All(descriptors, d => Assert.Equal(pointAKey, d.Light!.Value.Shadow.Key));
        using var host = new NativeUploadTestScene();
        var scheduler = new ResidencyScheduler();
        var state = new MapRenderSourceState(request.Source, request.Closure, request.Source.RenderExtents!, request.Policy.Render, prepared.Witness.AcquiredScopes);
        using var residency = new MapRenderResidency(state, new MapScene3DSink(host.Scene), null, scheduler);
        scheduler.RunAll(); using var view = new NativeMapView(host.Scene, residency);
        Assert.Equal(0, view.SettleForCapture(1024).Pending);
        var captureDescriptors = ResidencyFixture.Draw(residency).Draws.Where(d => d.Light.HasValue && d.Resource.Key.OwnerId == "point-a").ToArray();
        Assert.NotEmpty(captureDescriptors);
        Assert.All(captureDescriptors, d => Assert.Equal(descriptors[0].Light, d.Light));
        host.Scene.Begin(); view.Draw(frame);
        var lights = (System.Collections.IEnumerable)typeof(Scene3D).GetField("_lights", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(host.Scene)!;
        var light = Assert.Single(lights.Cast<KhaozEngine.Render3D.Rendering.ModelRenderer.PointLightData>());
        Assert.Equal(pointAKey, light.LocalShadow.Key);
        Assert.Equal(captureDescriptors[0].Light!.Value.Shadow, light.LocalShadow);
    }
    [Fact]
    public void NativeSink_LightPreservesAnchorExclusionBoxAndStaticKey17()
    {
        using var host = new NativeUploadTestScene(); var sink = new MapScene3DSink(host.Scene);
        var stamp = new MapResourceStamp("closure", "source", "scope", "policy", 1);
        var resource = new MapPreparedResource(new(new(MapRenderKind.Light, "point-a", "light", 0), stamp),
            new(31990, -510, -32010, 32010, -490, -31990),
            new MapLightResource(new(1, "point-a", null, new(new(32000, 1), new(-500, 1), new(-32000, 1)), Vector3.One, 2, 10, true)), "point-a");
        using var upload = sink.BeginUpload(resource, new NativeUploadReservation(sink.MeasureUpload(resource)));
        upload.Advance(long.MaxValue); sink.CompleteUploads(); var handle = upload.Publish(); sink.Register(resource);
        var local = new Vector3(.125f, .25f, -.375f); var anchor = new KhaozEngine.MapDoc.Surfaces.MapSubmissionAnchor(32000, -500, -32000);
        var shadow = LightShadow.Static(17).WithExclusionBox(new(-.5f), new(.5f));
        sink.Write(new(resource.Reference, MapDrawChannels.Colour | MapDrawChannels.Shadow, anchor, Matrix4x4.CreateTranslation(local), default,
            null, new MapLightFrame(Color.White, 10, 2, shadow, null), null, null));
        var lights = (System.Collections.IEnumerable)typeof(Scene3D).GetField("_lights", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)!.GetValue(host.Scene)!;
        var light = Assert.Single(lights.Cast<KhaozEngine.Render3D.Rendering.ModelRenderer.PointLightData>());
        Assert.Equal(new RenderAnchor(32000, -500, -32000), light.Anchor); Assert.Equal(local, light.LocalPosition);
        Assert.Equal(17, light.LocalShadow.Key); Assert.Equal(shadow.ExclusionMin, light.LocalShadow.ExclusionMin);
        Assert.Equal(local, Scene3D.AnchoredLightPosition(light, new(32000, -500, -32000)));
        sink.Release(handle);
    }
    [Fact]
    public void NativeSink_PreservesModelBindingAndLocalFocus()
    {
        using var host = new NativeUploadTestScene();
        using var fixture = ResidencyFixture.Foliage();
        var state = fixture.FoliageState();
        var options = new GroundCoverRenderOptions
        {
            UseGpuBatches = true,
            FadeMode = GroundCoverFadeMode.HeightScale,
            DrawRadius = 4,
            DensityRadius = 2,
            MotionProfiles = new System.Collections.Generic.Dictionary<string, FoliageMotionProfile>
            {
                ["A"] = new(.3f, .15f, .4f, .035f),
                ["B"] = new(.6f, .2f, .1f, .07f)
            },
            ModelCastsShadows = new System.Collections.Generic.Dictionary<string, bool> { ["A"] = true, ["B"] = false }
        };
        state = state with { Policy = ResidencyFixture.Policy(foliage: new(MapFrozenFoliageOptions.Capture(options), state.Policy.Foliage.Extents)) };
        var scheduler = new ResidencyScheduler(); var sink = new MapScene3DSink(host.Scene);
        using var residency = new MapRenderResidency(state, sink, null, scheduler);
        scheduler.RunAll(); using var view = new NativeMapView(host.Scene, residency);
        var ready = view.SettleForCapture(1024);
        Assert.Equal(MapSceneStatus.CompleteForScope, ready.Transition.Status); Assert.Equal(0, ready.Pending);
        var recorded = ResidencyFixture.Draw(residency);
        var foliage = recorded.Resources.Where(r => r.Payload is MapFoliageResource).ToArray(); Assert.NotEmpty(foliage);
        foreach (var resource in foliage)
        {
            Assert.True(residency.TryGetHandle(resource.Reference, out var handle));
            var product = host.Scene.ResourceOf(new(handle.Value)).PreparedFoliage!;
            var payload = (MapFoliageResource)resource.Payload;
            foreach (var part in product.ExpandedParts)
            {
                Assert.Equal(payload.ModelIndices.Span[part.SourceRow], part.ModelIndex);
                var binding = payload.Models[part.ModelIndex];
                Assert.True(residency.TryGetHandle(binding.Parts[part.PartIndex], out var mesh));
                Assert.Equal(new SceneResourceAccess(host.Scene).MeshOf(new(mesh.Value)), part.Mesh);
                var patches = product.Patches.Where(p => p.Mesh.Equals(part.Mesh)).ToArray();
                Assert.NotEmpty(patches); Assert.All(patches, patch => Assert.Equal(binding.CastShadows, patch.CastShadows));
                int offset = product.ExpandedParts.ToList().IndexOf(part) * 96 + 80;
                var bytes = product.PackedInstances.Span;
                var motion = new Vector4(System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(bytes[offset..]),
                    System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(bytes[(offset + 4)..]),
                    System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(bytes[(offset + 8)..]),
                    System.Buffers.Binary.BinaryPrimitives.ReadSingleLittleEndian(bytes[(offset + 12)..]));
                Assert.Equal(binding.Motion.ToVector4(), motion);
            }
        }
        var point = new MapFramePoint(WorldFrame.Origin, Vector3.Zero);
        var frame = new MapFrameState(WorldFrame.Origin, point, MapRoofMode.AlwaysVisible, 3.25, 23, new(WorldFrame.Origin, new(5, 0, 0)));
        long uploads = host.Device.UploadBytes, owners = residency.Lifetime.Created;
        var plans = residency.AcceptedPlans.ToArray();
        var generations = plans.Select(p => MapFoliageRenderCache.For(p.FoliageContext).GenerationCalls).ToArray();
        host.Scene.Begin(); view.Draw(frame);
        Assert.All(host.Scene.FoliageUniformsForTests.ToArray(), u => Assert.Equal(5, u.FocusRadius.X));
        Assert.NotEmpty(host.Scene.FoliageUniformsForTests.ToArray());
        host.Scene.Begin(); view.Draw(frame with { Focus = point });
        Assert.All(host.Scene.FoliageUniformsForTests.ToArray(), u => Assert.Equal(0, u.FocusRadius.X));
        Assert.Equal(uploads, host.Device.UploadBytes); Assert.Equal(owners, residency.Lifetime.Created);
        Assert.Equal(plans, residency.AcceptedPlans);
        Assert.Equal(generations, plans.Select(p => MapFoliageRenderCache.For(p.FoliageContext).GenerationCalls));
    }
    [Fact]
    public void NativeView_PicksOnlyItsAcceptedPresentationAndRoofChannels()
    {
        using var host = new NativeUploadTestScene(); using var fixture = NativePresentationFixtures.Create();
        var scheduler = new ResidencyScheduler();
        using var residency = new MapRenderResidency(fixture.State(NativePresentationFixtures.Scope), new MapScene3DSink(host.Scene), null, scheduler);
        scheduler.RunAll(); using var view = new NativeMapView(host.Scene, residency); view.SettleForCapture(1024);
        var ray = new MapPresentationRay(new(WorldFrame.Origin, new(0, .5f, 0)), Vector3.UnitZ, 10);
        var frame = NativePresentationFixtures.Frame();
        Assert.Equal("object-behind", view.Pick(ray, MapPresentationChannel.Selection, frame)!.Value.OwnerId);
        Assert.Equal("roof-a", view.Pick(ray, MapPresentationChannel.Physical, frame)!.Value.OwnerId);
    }
    [Fact]
    public void FailedNativeReplacement_KeepsTheAcceptedDrawsAndWitness()
    {
        using var host = new NativeUploadTestScene(); using var fixture = ResidencyFixture.Placement();
        var scheduler = new ResidencyScheduler(); var state = fixture.State(ResidencyFixture.Window());
        using var residency = new MapRenderResidency(state, new MapScene3DSink(host.Scene), null, scheduler);
        scheduler.RunAll(); using var view = new NativeMapView(host.Scene, residency); view.SettleForCapture(1024);
        var before = ResidencyFixture.Draw(residency); var accepted = residency.Update().Transition.AcceptedWitness;
        var bad = state with { Policy = ResidencyFixture.Policy(materials: ["absent-material"]) };
        var transition = view.Replace(bad);
        Assert.Equal(MapSceneStatus.Invalid, transition.Status); Assert.Equal(accepted, transition.AcceptedWitness);
        Assert.Null(transition.PendingWitness); Assert.Equal(before.Draws, ResidencyFixture.Draw(residency).Draws);
    }
    [Fact]
    public void ReadyBeforeWarmup_FreshAndReusedAgree()
    {
        using var host = new NativeUploadTestScene();
        using var fixture = ResidencyFixture.Placement();
        var scheduler = new ResidencyScheduler();
        using var residency = new MapRenderResidency(fixture.State(ResidencyFixture.Window()), new ResidencyDevice(), null, scheduler);
        using var view = new NativeMapView(host.Scene, residency);
        Assert.True(residency.Update().Pending > 0);
        scheduler.RunAll();
        var fresh = view.SettleForCapture(1024);
        Assert.Equal(0, fresh.Pending);
        var reused = view.SettleForCapture(0);
        Assert.Equal(fresh, reused);
        var accepted = fresh.Transition.AcceptedWitness;
        residency.RequestReplacement(fixture.State(ResidencyFixture.Window(8, 12)));
        var pending = view.SettleForCapture(0);
        Assert.True(pending.Pending > 0); Assert.Equal(accepted, pending.Transition.AcceptedWitness);
        Assert.NotNull(pending.Transition.PendingWitness);
        Assert.NotEqual(accepted!.ScopeDigest, pending.Transition.PendingWitness!.ScopeDigest);
        scheduler.RunAll(); Assert.Equal(0, view.SettleForCapture(1024).Pending);
    }
}
