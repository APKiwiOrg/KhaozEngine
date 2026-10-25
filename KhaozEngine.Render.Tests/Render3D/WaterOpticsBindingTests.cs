using System;
using System.Linq;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

[Collection("AllocSensitive")]
public sealed class WaterOpticsBindingTests
{
    sealed class Harness : IDisposable
    {
        internal readonly FakeGpuDevice Device = new();
        internal readonly RenderResources Targets;
        internal readonly GpuRetireQueue Retired;
        internal readonly WaterRenderer Renderer;
        internal readonly NullGpuCommandList Commands = new();
        internal readonly WaterSettings Settings = new() { SwellAmplitude = 0f };
        internal readonly SkySettings Sky = new();
        internal WaterPlane[] Planes;
        internal readonly WaterOpticsHandle Handle;
        internal FakeGpuResourceFactory Factory => (FakeGpuResourceFactory)Device.Factory;

        internal Harness()
        {
            Targets = new RenderResources(Device, 96, 64, false);
            Retired = GpuRetireQueue.CreateFrameCounted(Device, 2);
            Renderer = new WaterRenderer(Device, Targets.ColorDepthFB.Outputs, Retired);
            Handle = Renderer.Optics.Load(new WaterOpticsField(2, 2, [2f, -1f, 30f, 100f]));
            Planes = [new WaterPlane(0f, 0f, 0f, 5f, optics: new WaterOpticsBinding(Handle,
                new Vector3(-5f, 0f, -5f), new Vector2(10f))), new WaterPlane(12f, 0f, 0f, 5f)];
        }

        internal void Prepare()
        {
            var frame = new FramePrepare(Settings, Planes, 0f);
            Renderer.PrepareFrame(frame);
            Renderer.PrepareOpticsFrame(Targets, frame);
        }

        internal void Draw() => Renderer.Draw(Commands, Targets, Planes, WaterTestFrames.TopDown,
            WaterTestFrames.TopDown, -Vector3.UnitY, Color.White, Vector3.UnitY * 100f, Settings, Sky, 0f);

        internal FakeResourceSet[] WaterSets() => Factory.ResourceSets.Where(s => s.Resources.Length == 12).ToArray();

        public void Dispose()
        {
            Renderer.Dispose();
            Retired.Dispose();
            Targets.Dispose();
            Commands.Dispose();
            Device.Dispose();
        }
    }

    // Mutable looks, scene defaults and queue entries must never invalidate a coverage proof silently.
    [Theory]
    [InlineData("amplitude")]
    [InlineData("direction")]
    [InlineData("look")]
    [InlineData("binding")]
    [InlineData("bathymetry")]
    [InlineData("grid")]
    public void InputsChangedAfterPreparationAreRefused(string input)
    {
        using var h = new Harness();
        var look = new WaterLook { SwellAmplitude = 0f };
        if (input == "look") h.Planes[0] = new WaterPlane(0f, 0f, 0f, 5f, look: look, optics: h.Planes[0].Optics);
        h.Prepare();
        switch (input)
        {
            case "amplitude": h.Settings.SwellAmplitude = 1f; break;
            case "direction": h.Settings.SwellDirectionDegrees += 1f; break;
            case "look": look.SwellAmplitude = 1f; break;
            case "binding": h.Planes[0] = h.Planes[0].WithOptics(default); break;
            case "grid": h.Settings.GridMode = WaterGridMode.Clipmap; break;
            case "bathymetry": h.Settings.Bathymetry = new WaterBathymetry(8, 0f, 0f, 40f); break;
        }
        int sets = h.Factory.ResourceSets.Count, buffers = h.Factory.Buffers.Count;
        Assert.Throws<InvalidOperationException>(h.Draw);
        Assert.Equal(sets, h.Factory.ResourceSets.Count);
        Assert.Equal(buffers, h.Factory.Buffers.Count);
    }

    [Fact]
    public void PreparationRevalidatesMutatedCoverage()
    {
        using var h = new Harness();
        h.Settings.SwellAmplitude = 1f;
        Assert.Throws<ArgumentException>(h.Prepare);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void ReleasedQueuedFieldIsRefusedBeforeUse(bool afterPreparation)
    {
        using var h = new Harness();
        if (afterPreparation) h.Prepare();
        h.Renderer.Optics.Unload(h.Handle);
        if (afterPreparation) Assert.Throws<ArgumentException>(h.Draw);
        else Assert.Throws<ArgumentException>(h.Prepare);
    }

    // Both the bound and legacy sets range over the same UBO and target generation.
    [Fact]
    public void TargetAndUboReplacementRetireAllAffectedSets()
    {
        using var h = new Harness();
        h.Prepare();
        FakeResourceSet[] first = h.WaterSets();
        Assert.Equal(2, first.Length);
        IGpuBindableResource texture = h.Renderer.Optics.Resolve(h.Handle).Texture;
        Assert.Contains(first, s => ReferenceEquals(texture, s.Resources[10]));
        h.Targets.Resize(120, 80, false, 1, false, false);
        Assert.Throws<InvalidOperationException>(h.Draw);
        h.Prepare();
        Assert.All(first, s => Assert.False(s.Disposed));
        h.Retired.BeginFrame();
        h.Retired.BeginFrame();
        Assert.All(first, s => Assert.True(s.Disposed));
        FakeResourceSet[] second = h.WaterSets().Where(s => !s.Disposed).ToArray();
        Assert.Equal(2, second.Length);
        h.Planes = Enumerable.Repeat(h.Planes[0], 9).Append(h.Planes[1]).ToArray();
        h.Prepare();
        h.Retired.BeginFrame();
        h.Retired.BeginFrame();
        Assert.All(second, s => Assert.True(s.Disposed));
        h.Draw();
    }

    [Fact]
    public void FailedPreparationCanRetryWithoutPublishingSuccess()
    {
        using var h = new Harness();
        h.Factory.ThrowOnResourceSetCreate = h.Factory.ResourceSets.Count + 2;
        Assert.Throws<InvalidOperationException>(h.Prepare);
        Assert.Throws<InvalidOperationException>(h.Draw);
        h.Factory.ThrowOnResourceSetCreate = 0;
        h.Prepare();
        h.Draw();
    }

    [Fact]
    public void FailedUboGrowthKeepsAUsableCapacityForRetry()
    {
        using var h = new Harness();
        h.Prepare();
        h.Planes = Enumerable.Repeat(h.Planes[0], 10).ToArray();
        h.Factory.FailBufferCreationAfter(0);
        Assert.Throws<InvalidOperationException>(h.Prepare);
        h.Factory.ThrowOnBufferCreate = 0;
        h.Prepare();
        h.Draw();
    }

    [Fact]
    public void EqualGenerationTargetsStillRequireTheirOwnPreparedSets()
    {
        using var h = new Harness();
        using var other = new RenderResources(h.Device, 96, 64, false);
        h.Prepare();
        Assert.Equal(h.Targets.Generation, other.Generation);
        Assert.Throws<InvalidOperationException>(() => h.Renderer.Draw(h.Commands, other, h.Planes,
            WaterTestFrames.TopDown, WaterTestFrames.TopDown, -Vector3.UnitY, Color.White,
            Vector3.UnitY * 100f, h.Settings, h.Sky, 0f));
        FakeResourceSet[] previous = h.WaterSets();
        var frame = new FramePrepare(h.Settings, h.Planes, 0f);
        h.Renderer.PrepareFrame(frame);
        h.Renderer.PrepareOpticsFrame(other, frame);
        FakeResourceSet[] current = h.WaterSets().Except(previous).ToArray();
        Assert.Equal(2, current.Length);
        Assert.All(current, set => Assert.Same(other.DepthColorTex, set.Resources[4]));
        h.Retired.BeginFrame();
        h.Retired.BeginFrame();
        Assert.All(previous, set => Assert.True(set.Disposed));
    }

    [Fact]
    public void ReusedSlotGenerationGetsANewSetAndRetiresThePreviousOne()
    {
        using var h = new Harness();
        h.Prepare();
        var oldTexture = (FakeTexture)h.Renderer.Optics.Resolve(h.Handle).Texture;
        FakeResourceSet oldSet = h.WaterSets().Single(set => ReferenceEquals(set.Resources[10], oldTexture));
        h.Renderer.UnloadOpticsField(h.Handle);
        WaterOpticsHandle replacement = h.Renderer.Optics.Load(new WaterOpticsField(2, 2, [5f, 5f, 5f, 5f]));
        Assert.Equal(h.Handle.Index, replacement.Index);
        Assert.NotEqual(h.Handle.Generation, replacement.Generation);
        h.Planes[0] = h.Planes[0].WithOptics(new WaterOpticsBinding(replacement,
            h.Planes[0].Optics.Origin, h.Planes[0].Optics.Size));
        h.Prepare();
        h.Draw();
        IGpuTexture current = h.Renderer.Optics.Resolve(replacement).Texture;
        Assert.Contains(h.WaterSets(), set => ReferenceEquals(set.Resources[10], current));
        Assert.False(oldTexture.Disposed);
        Assert.False(oldSet.Disposed);
        h.Retired.BeginFrame();
        h.Retired.BeginFrame();
        Assert.True(oldTexture.Disposed);
        Assert.True(oldSet.Disposed);
    }

    [Fact]
    public void UnchangedDrawAllocatesAndUploadsNothing()
    {
        using var h = new Harness();
        int uploads = 0;
        h.Device.OnTextureUpload = (_, _) => uploads++;
        void Frame() { h.Prepare(); h.Draw(); }
        Frame();
        int textures = h.Factory.Textures.Count, sets = h.Factory.ResourceSets.Count;
        long opticalUploads = h.Renderer.Optics.Stats.UploadCount;
        long allocated = AllocationMeasurement.Measure("one warmed bound and legacy water frame", Frame);
        Assert.Equal(0, allocated);
        Assert.Equal(textures, h.Factory.Textures.Count);
        Assert.Equal(sets, h.Factory.ResourceSets.Count);
        Assert.Equal(0, uploads);
        Assert.Equal(opticalUploads, h.Renderer.Optics.Stats.UploadCount);
    }
}
