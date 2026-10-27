using System;
using System.Linq;
using System.Numerics;
using System.Runtime.InteropServices;
using KhaozEngine.Gpu;
using KhaozEngine.Gpu.Internal;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class WaterOpticsTriangleResourceTests
{
    internal static WaterOpticsTriangleField Field(bool partial = false) => new(new(2), 1, 1,
        [new(new(0, -1, 0), new(2, -1, 0), new(2, -1, 2)),
         new(new(0, -1, 0), new(2, -1, 2), new(0, -1, 2))],
        partial ? [new(new(.5f), new(1.5f))] : [new(Vector2.Zero, new(2))]);

    [Fact]
    public void MixedKindsRetainTheirPayloadAndChargePaddedGpuAndCpuStorageUntilRetirement()
    {
        using var device = new FakeGpuDevice();
        using var retired = GpuRetireQueue.CreateFrameCounted(device, 2);
        using var resources = new WaterOpticsResources(device, retired);
        WaterOpticsTriangleField field = Field();
        float[]? uploaded = null;
        device.OnTextureUpload = (_, bytes) => uploaded = MemoryMarshal.Cast<byte, float>(bytes).ToArray();
        WaterOpticsHandle triangle = resources.LoadTriangles(field);
        Assert.Equal(field.AtlasScalars.ToArray(), uploaded);
        Assert.Equal(128, uploaded!.Length);
        WaterOpticsHandle bilinear = resources.Load(new WaterOpticsField(2, 2, [1, 2, 3, 4]));
        Assert.Equal(2, resources.Stats.LiveFields);
        Assert.Equal(528, resources.Stats.LiveTextureBytes);
        Assert.Equal(544, resources.Stats.LiveRetainedCpuBytes);
        Assert.Same(resources.Resolve(triangle), resources.Resolve(triangle));
        Assert.Equal(2, resources.Resolve(triangle).Payload.Mode);
        Assert.Equal(1, resources.Resolve(bilinear).Payload.Mode);
        var texture = (FakeTexture)resources.Resolve(triangle).Texture;
        Assert.Equal(64u, texture.Width);
        Assert.Equal(2u, texture.Height);
        resources.Unload(triangle);
        Assert.Equal(16, resources.Stats.LiveRetainedCpuBytes);
        Assert.Equal(528, resources.Stats.RetiredRetainedCpuBytes);
        Assert.Equal(512, resources.Stats.RetiredTextureBytes);
        Assert.Throws<ArgumentException>(() => resources.Resolve(triangle));
        WaterOpticsHandle replacement = resources.LoadTriangles(field);
        Assert.Equal(triangle.Index, replacement.Index);
        Assert.NotEqual(triangle.Generation, replacement.Generation);
        retired.BeginFrame();
        Assert.False(texture.Disposed);
        retired.BeginFrame();
        Assert.True(texture.Disposed);
        Assert.Equal(0, resources.Stats.RetiredRetainedCpuBytes);
        Assert.Equal(0, resources.Stats.RetiredTextureBytes);
        Assert.Equal(544, resources.Stats.LiveRetainedCpuBytes);
    }

    [Fact]
    public void FailedTriangleUploadRetainsUnpublishedPayloadWithoutConsumingSlotOrUploadCount()
    {
        using var device = new FakeGpuDevice();
        using var retired = GpuRetireQueue.CreateFrameCounted(device, 2);
        using var resources = new WaterOpticsResources(device, retired);
        device.OnTextureUpload = (_, _) => throw new InvalidOperationException("planned failure");
        Assert.Throws<InvalidOperationException>(() => resources.LoadTriangles(Field()));
        Assert.Equal(0, resources.Stats.LiveFields);
        Assert.Equal(0, resources.Stats.UploadCount);
        Assert.Equal(512, resources.Stats.RetiredTextureBytes);
        Assert.Equal(528, resources.Stats.RetiredRetainedCpuBytes);
        device.OnTextureUpload = null;
        WaterOpticsHandle handle = resources.Load(new WaterOpticsField(2, 2, [0, 0, 0, 0]));
        Assert.Equal(0, handle.Index);
        Assert.Equal(1, handle.Generation);
        retired.Dispose();
        Assert.Equal(0, resources.Stats.RetiredRetainedCpuBytes);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void SubnormalAtlasValuesAreRefusedBeforeTextureCreationRatherThanMovedByBackendFlush(bool depth)
    {
        float tiny = float.Epsilon;
        float x = depth ? 0 : tiny, q = depth ? -tiny : 0;
        var field = new WaterOpticsTriangleField(Vector2.One, 1, 1,
            [new(new(x, q, 0), new(1, q, 0), new(x, q, 1))], [new(new(.25f), new(.375f))]);
        Assert.True(field.TrySampleDepth(.3f, .3f, out _));
        using var device = new FakeGpuDevice();
        using var retired = GpuRetireQueue.CreateFrameCounted(device, 2);
        using var resources = new WaterOpticsResources(device, retired);
        var error = Assert.Throws<WaterOpticsSurfaceException>(() => resources.LoadTriangles(field));
        Assert.Equal(WaterOpticsSurfaceFailure.UnrepresentableSurface, error.Reason);
        Assert.Empty(((FakeGpuResourceFactory)device.Factory).Textures);
        Assert.Equal(default, resources.Stats);
    }

    [Fact]
    public void ImpossibleAtlasPreflightDoesNotCopyTheLargeInputSpan()
    {
        var triangles = new WaterOpticsTriangle[87_380];
        WaterOpticsRectangle[] certificates = [new(Vector2.Zero, Vector2.One)];
        void Attempt() => Assert.Equal(WaterOpticsSurfaceFailure.CapacityExceeded,
            Assert.Throws<WaterOpticsSurfaceException>(() => new WaterOpticsTriangleField(
                Vector2.One, 1, 1, triangles, certificates)).Reason);
        long allocated = AllocationMeasurement.Measure("impossible triangle atlas preflight", Attempt);
        Assert.InRange(allocated, 0, 65_536);
    }

    [Fact]
    public void CertificateUnionCannotReplaceOneContainingCertificate()
    {
        var field = new WaterOpticsTriangleField(new(2), 1, 1,
            [new(new(0, 0, 0), new(2, 0, 0), new(2, 0, 2)), new(new(0, 0, 0), new(2, 0, 2), new(0, 0, 2))],
            [new(Vector2.Zero, new(1, 2)), new(new(1, 0), new(2))]);
        using var device = new FakeGpuDevice();
        using var scene = new Scene3D(device, new(null, GpuPixelFormat.R8G8B8A8UNorm));
        scene.Post.Water.SwellAmplitude = 0;
        WaterOpticsHandle handle = scene.LoadWaterOpticsTriangles(field);
        var binding = new WaterOpticsBinding(handle, Vector3.Zero, new(2));
        Assert.Throws<ArgumentException>(() => scene.DrawWater(new(1, 0, 1, .25f, optics: binding)));
        scene.DrawWater(new(.5f, 0, 1, .25f, optics: binding));
    }

    [Fact]
    public void SubnormalInverseExtentIsRefusedBeforeUncertifiedBinSelection()
    {
        float size = float.MaxValue;
        var field = new WaterOpticsTriangleField(new(size), 1, 1,
            [new(new(0,0,0),new(size,0,0),new(size,0,size)),
             new(new(0,0,0),new(size,0,size),new(0,0,size))], [new(Vector2.Zero, new(size))]);
        using var device = new FakeGpuDevice();
        using var retired = GpuRetireQueue.CreateFrameCounted(device, 2);
        using var resources = new WaterOpticsResources(device, retired);
        Assert.Equal(WaterOpticsSurfaceFailure.UnrepresentableSurface,
            Assert.Throws<WaterOpticsSurfaceException>(() => resources.LoadTriangles(field)).Reason);
        Assert.Empty(((FakeGpuResourceFactory)device.Factory).Textures);
    }

    [Fact]
    public void CpuAndTextureStorageRemainChargedUntilDelayedGpuCompletion()
    {
        using var device = new FakeGpuDevice();
        var barrier = new DelayedBarrier();
        using var retired = new GpuRetireQueue(() => Assert.Fail("A delayed completion must not drain"), barrier);
        using var resources = new WaterOpticsResources(device, retired);
        WaterOpticsHandle handle = resources.LoadTriangles(Field());
        var texture = (FakeTexture)resources.Resolve(handle).Texture;
        resources.Unload(handle);
        retired.BeginFrame();
        retired.BeginFrame();
        retired.BeginFrame();
        Assert.False(texture.Disposed);
        Assert.Equal(512, resources.Stats.RetiredTextureBytes);
        Assert.Equal(528, resources.Stats.RetiredRetainedCpuBytes);
        barrier.Fence.Complete = true;
        retired.BeginFrame();
        Assert.True(texture.Disposed);
        Assert.Equal(0, resources.Stats.RetiredTextureBytes);
        Assert.Equal(0, resources.Stats.RetiredRetainedCpuBytes);
    }

    sealed class DelayedFence : IGpuFence
    {
        internal bool Complete;
        public bool Signaled => Complete;
        public void Reset() => Complete = false;
        public void Dispose() { }
    }

    sealed class DelayedBarrier : IRetireBarrier
    {
        internal readonly DelayedFence Fence = new();
        public IGpuFence Submit() => Fence;
        public void Release(IGpuFence fence) { }
        public void Dispose() { }
    }

    [Fact]
    public void SceneReloadRejectsForeignAndReleasedTriangleHandlesAndRequiresExactSize()
    {
        using var device = new FakeGpuDevice();
        var output = new GpuOutputDescription(null, GpuPixelFormat.R8G8B8A8UNorm);
        using var first = new Scene3D(device, output);
        using var second = new Scene3D(device, output);
        first.Post.Water.SwellAmplitude = second.Post.Water.SwellAmplitude = 0;
        WaterOpticsTriangleField field = Field();
        WaterOpticsHandle old = first.LoadWaterOpticsTriangles(field);
        WaterOpticsHandle fresh = second.LoadWaterOpticsTriangles(field);
        WaterPlane Plane(WaterOpticsHandle handle, Vector2 size) => new(1, 0, 1, .25f,
            optics: new(handle, Vector3.Zero, size));
        Assert.Throws<ArgumentException>(() => second.DrawWater(Plane(old, new(2))));
        Assert.Throws<ArgumentException>(() => second.UnloadWaterOpticsField(old));
        Assert.Throws<ArgumentException>(() => second.DrawWater(Plane(fresh, new(float.BitIncrement(2), 2))));
        second.DrawWater(Plane(fresh, new(2)));
        second.UnloadWaterOpticsField(fresh);
        Assert.Throws<ArgumentException>(() => second.DrawWater(Plane(fresh, new(2))));
        Assert.Throws<ArgumentException>(() => second.UnloadWaterOpticsField(fresh));
        Assert.Throws<ArgumentNullException>(() => second.LoadWaterOpticsTriangles(null!));
    }

    [Fact]
    public void SubmissionAndPreparationRequireOneCertificateIncludingEffectiveDisplacement()
    {
        using var h = new Harness(partial: true);
        h.Prepare();
        h.Settings.SwellWavelength = 2;
        h.Settings.SwellSteepness = 1;
        h.Settings.SwellAmplitude = 1;
        Assert.Throws<ArgumentException>(h.Prepare);
        using var scene = new Scene3D(h.Device, h.Targets.ColorDepthFB.Outputs);
        scene.Post.Water.SwellAmplitude = 0;
        WaterOpticsHandle handle = scene.LoadWaterOpticsTriangles(Field(partial: true));
        var binding = new WaterOpticsBinding(handle, Vector3.Zero, new(2));
        scene.DrawWater(new(1, 0, 1, .25f, optics: binding));
        Assert.Throws<ArgumentException>(() => scene.DrawWater(new(1, 0, 1, .75f, optics: binding)));
        scene.Post.Water.SwellWavelength = 2;
        scene.Post.Water.SwellSteepness = 1;
        scene.Post.Water.SwellAmplitude = 1;
        Assert.InRange(scene.GetWaterOpticsPadding(), .25f, .75f);
        Assert.Throws<ArgumentException>(() => scene.PrepareFrame(96, 64));
    }

    [Fact]
    public void ChangedReleasedAndFailedPreparationsCannotRecordAndRetryDoesNotUpload()
    {
        using var h = new Harness();
        h.Factory.ThrowOnResourceSetCreate = h.Factory.ResourceSets.Count + 3;
        Assert.Throws<InvalidOperationException>(h.Prepare);
        Assert.Throws<InvalidOperationException>(h.Draw);
        h.Factory.ThrowOnResourceSetCreate = 0;
        h.Prepare();
        h.Draw();
        h.Planes[0] = h.Planes[0].WithOptics(default);
        Assert.Throws<InvalidOperationException>(h.Draw);
        h.Prepare();
        h.Draw();
        h.Renderer.UnloadOpticsField(h.Triangle);
        h.Planes[0] = h.TrianglePlane;
        Assert.Throws<ArgumentException>(h.Prepare);
        Assert.Throws<InvalidOperationException>(h.Draw);
        Assert.Equal(2, h.Renderer.Optics.Stats.UploadCount);
    }

    [Fact]
    public void TargetUboAndUnusedCacheSetsRetireForBothKinds()
    {
        using var h = new Harness();
        h.Prepare();
        FakeResourceSet[] first = h.WaterSets();
        Assert.Equal(3, first.Length);
        h.Targets.Resize(120, 80, false, 1, false, false);
        h.Prepare();
        h.Retired.BeginFrame();
        Assert.All(first, s => Assert.False(s.Disposed));
        h.Retired.BeginFrame();
        Assert.All(first, s => Assert.True(s.Disposed));
        FakeResourceSet[] second = h.WaterSets().Where(s => !s.Disposed).ToArray();
        h.Planes = Enumerable.Repeat(h.TrianglePlane, 10).ToArray();
        h.Prepare();
        h.Retired.BeginFrame();
        h.Retired.BeginFrame();
        Assert.All(second, s => Assert.True(s.Disposed));
        FakeResourceSet bound = h.WaterSets().Last(s => ReferenceEquals(s.Resources[10],
            h.Renderer.Optics.Resolve(h.Triangle).Texture));
        h.Planes = [new(1, 0, 1, .25f)];
        h.Prepare();
        h.Retired.BeginFrame();
        h.Retired.BeginFrame();
        Assert.True(bound.Disposed);
        Assert.Equal(2, h.Renderer.Optics.Stats.UploadCount);
    }

    [Fact]
    public void WarmedMixedFrameAllocatesNoManagedBytesSetsTexturesOrUploads()
    {
        using var h = new Harness();
        void Frame() { h.Prepare(); h.Draw(); }
        Frame();
        int textures = h.Factory.Textures.Count, sets = h.Factory.ResourceSets.Count, uploads = 0;
        h.Device.OnTextureUpload = (_, _) => uploads++;
        long allocated = AllocationMeasurement.Measure("one warmed triangle, bilinear and legacy frame", Frame);
        Assert.Equal(0, allocated);
        Assert.Equal(textures, h.Factory.Textures.Count);
        Assert.Equal(sets, h.Factory.ResourceSets.Count);
        Assert.Equal(0, uploads);
        Assert.Equal(2, h.Renderer.Optics.Stats.UploadCount);
    }

    [Fact]
    public void PerSubmissionSlotsKeepModesDimensionsAndMappingsWhenOrderReverses()
    {
        using var h = new Harness();
        using var commands = new RecordingGpuCommandList(new NullGpuCommandList()) { CapturePayloads = true };
        h.Prepare();
        h.Record(commands);
        var upload = commands.Uploads.Single(u => u.Bytes % WaterRenderer.SlotBytes == 0
            && u.Bytes >= 3 * WaterRenderer.SlotBytes);
        Assert.Equal(0u, upload.Offset);
        Assert.Equal(upload.Buffer.SizeInBytes, upload.Bytes);
        WaterRenderer.WaterUbo Slot(int index) => MemoryMarshal.Read<WaterRenderer.WaterUbo>(
            upload.Data!.AsSpan(index * (int)WaterRenderer.SlotBytes));
        Assert.Equal(new Vector4(0, 2, 64, 2), Slot(0).OpticsParams);
        Assert.Equal(new Vector4(0, 1, 2, 2), Slot(1).OpticsParams);
        Assert.Equal(Vector4.Zero, Slot(2).OpticsParams);
        Assert.Equal(new Vector4(0, 0, .5f, .5f), Slot(0).OpticsRect);
        Assert.Equal(new Vector4(3, 0, .5f, .5f), Slot(1).OpticsRect);
        Array.Reverse(h.Planes);
        commands.Clear();
        h.Prepare();
        h.Record(commands);
        upload = commands.Uploads.Single(u => u.Bytes % WaterRenderer.SlotBytes == 0
            && u.Bytes >= 3 * WaterRenderer.SlotBytes);
        Assert.Equal(Vector4.Zero, Slot(0).OpticsParams);
        Assert.Equal(new Vector4(0, 1, 2, 2), Slot(1).OpticsParams);
        Assert.Equal(new Vector4(0, 2, 64, 2), Slot(2).OpticsParams);
    }

    sealed class Harness : IDisposable
    {
        internal readonly FakeGpuDevice Device = new();
        internal readonly RenderResources Targets;
        internal readonly GpuRetireQueue Retired;
        internal readonly WaterRenderer Renderer;
        internal readonly NullGpuCommandList Commands = new();
        internal readonly WaterSettings Settings = new() { SwellAmplitude = 0 };
        internal readonly SkySettings Sky = new();
        internal readonly WaterOpticsHandle Triangle;
        internal readonly WaterPlane TrianglePlane;
        internal WaterPlane[] Planes;
        internal FakeGpuResourceFactory Factory => (FakeGpuResourceFactory)Device.Factory;

        internal Harness(bool partial = false)
        {
            Targets = new(Device, 96, 64, false);
            Retired = GpuRetireQueue.CreateFrameCounted(Device, 2);
            Renderer = new(Device, Targets.ColorDepthFB.Outputs, Retired);
            Triangle = Renderer.Optics.LoadTriangles(Field(partial));
            WaterOpticsHandle bilinear = Renderer.Optics.Load(new WaterOpticsField(2, 2, [2, 2, 2, 2]));
            TrianglePlane = new(1, 0, 1, .25f, optics: new(Triangle, Vector3.Zero, new(2)));
            Planes = [TrianglePlane, new(4, 0, 1, .25f, optics: new(bilinear, new(3, 0, 0), new(2))),
                new(7, 0, 1, .25f)];
        }

        internal void Prepare()
        {
            var frame = new FramePrepare(Settings, Planes, 0);
            Renderer.PrepareFrame(frame);
            Renderer.PrepareOpticsFrame(Targets, frame);
        }

        internal void Draw() => Record(Commands);

        internal void Record(IGpuCommandList commands) => Renderer.Draw(commands, Targets, Planes, WaterTestFrames.TopDown,
            WaterTestFrames.TopDown, -Vector3.UnitY, Color.White, Vector3.UnitY * 100, Settings, Sky, 0);

        internal FakeResourceSet[] WaterSets() => Factory.ResourceSets.Where(s => s.Resources.Length == 17).ToArray();

        public void Dispose()
        {
            Renderer.Dispose();
            Retired.Dispose();
            Targets.Dispose();
            Commands.Dispose();
            Device.Dispose();
        }
    }
}
