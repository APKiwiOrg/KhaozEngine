using System;
using System.Collections.Generic;
using System.Linq;
using System.Numerics;
using System.Reflection;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

public sealed class WaterColumnCopyTests
{
    sealed class Host : IDisposable
    {
        internal readonly FakeGpuDevice Device = new(maxMsaaSampleCount: 4);
        internal readonly Scene3D Scene;
        internal readonly RecordingGpuCommandList Commands = new(new NullGpuCommandList());
        internal readonly Timeline Events;
        internal FakeGpuResourceFactory Factory => (FakeGpuResourceFactory)Device.Factory;
        internal RenderResources Targets => Field<RenderResources>("_res");
        internal WaterColumnColorSource Source => Field<WaterColumnColorSource>("_waterColumnColor");
        T Field<T>(string name) => (T)typeof(Scene3D).GetField(name,
            BindingFlags.Instance | BindingFlags.NonPublic)!.GetValue(Scene)!;

        internal Host()
        {
            Scene = new Scene3D(Device, new(null, GpuPixelFormat.R8G8B8A8UNorm));
            Scene.Post.Starfield = false;
            Scene.Post.RenderScale = RenderScale.MatchViewport;
            Scene.Post.Water.SwellAmplitude = 0f;
            Scene.RenderOrigin = Vector3.Zero;
            Events = new Timeline(Commands);
        }

        internal void Queue(bool column = true)
        {
            Scene.Begin();
            Scene.DrawWater(new WaterPlane(0f, 0f, 0f, 2f,
                look: new WaterLook { WaterColumnLight = column }));
        }

        internal void Render(bool viewportPreparation = true, Action? afterPreparation = null)
        {
            if (viewportPreparation) Scene.PrepareFrame(96, 64);
            else Scene.PrepareFrame();
            afterPreparation?.Invoke();
            Commands.Clear();
            Events.Entries.Clear();
            using IGpuFramebuffer target = Factory.CreateFramebuffer(null, Scene.ResolvedDepthTarget);
            Scene.RenderInternal(Events, 96, 64, target);
        }

        public void Dispose() { Commands.Dispose(); Scene.Dispose(); Device.Dispose(); }
    }

    [Fact]
    public void CopyRunsOnceAfterDecalsBeforeWater()
    {
        using var h = new Host();
        h.Scene.Begin();
        // A legacy draw precedes the opted-in draw. Neither may contaminate the copy.
        h.Scene.DrawWater(new WaterPlane(0f, 0f, 0f, 2f));
        h.Scene.DrawWater(new WaterPlane(0f, 1f, 0f, 2f, look: new WaterLook { WaterColumnLight = true }));
        h.Scene.DrawGroundDecal(new GroundDecal
        {
            Shape = DecalShape.Circle,
            Center = Vector3.Zero,
            Size = new Vector4(2f),
            FillColor = Color.White,
            FillFraction = 1f,
        });
        h.Render();

        var copy = Assert.Single(h.Commands.TextureCopies, c => ReferenceEquals(c.Destination, h.Source.Texture));
        Assert.Same(h.Targets.ColorTex, copy.Source);
        Assert.NotSame(copy.Source, copy.Destination);
        Assert.Equal(copy.Source.Format, copy.Destination.Format);
        Assert.False(copy.WholeResource);
        int copied = h.Events.Entries.FindIndex(e => e.Kind == "copy" && ReferenceEquals(e.Destination, h.Source.Texture));
        int decal = h.Events.Entries.FindLastIndex(e => e.Kind == "decal");
        var water = h.Events.Entries.Select((e, i) => (e, i)).Where(x => x.e.Kind == "water").ToArray();
        Assert.True(decal >= 0 && decal < copied);
        Assert.Equal(2, water.Length);
        Assert.All(water, x => Assert.True(copied < x.i));
    }

    [Fact]
    public void MsaaResolvesTheActualColorAttachment()
    {
        using var h = new Host();
        h.Scene.Post.Quality.AntiAliasing = AntiAliasing.Msaa(4);
        h.Queue();
        h.Render();

        Assert.Same(h.Targets.ColorTex, h.Source.Texture);
        var color = h.Commands.Resolves.Where(r => ReferenceEquals(r.Source, h.Targets.MsColor)).ToArray();
        Assert.Equal(2, color.Length);
        Assert.True(color[0].PreserveSource);
        Assert.False(color[1].PreserveSource);
        Assert.All(color, r => Assert.Same(h.Targets.ColorTex, r.Destination));
        Assert.DoesNotContain(h.Commands.TextureCopies, c => ReferenceEquals(c.Source, h.Targets.ColorTex)
            || ReferenceEquals(c.Source, h.Targets.MsColor));
        var resolves = h.Events.Entries.Select((e, i) => (e, i))
            .Where(x => x.e.Kind == "resolve" && ReferenceEquals(x.e.Source, h.Targets.MsColor)).ToArray();
        int water = h.Events.Entries.FindIndex(e => e.Kind == "water");
        Assert.True(resolves[0].i < water && water < resolves[1].i);
        Assert.Equal(new WaterColumnDiagnostics(1, true, WaterColumnCopyPath.MsaaResolve), h.Scene.WaterColumnLightDiagnostics);
    }

    [Fact]
    public void MippedSingleSampleCopiesOnlyMipZero()
    {
        using var h = new Host();
        h.Scene.Post.Supersample = 2f;
        h.Queue();
        h.Render();

        var copy = Assert.Single(h.Commands.TextureCopies, c => ReferenceEquals(c.Destination, h.Source.Texture));
        Assert.True(copy.Source.MipLevels > 1);
        Assert.Equal(1u, copy.Destination.MipLevels);
        Assert.Equal(1u, copy.Destination.SampleCount);
        Assert.False(copy.WholeResource);
        Assert.Equal((0u, 0u, 0u, 0u), (copy.SourceMipLevel, copy.SourceArrayLayer, copy.DestinationMipLevel, copy.DestinationArrayLayer));
        Assert.Equal((192u, 128u), (copy.Width, copy.Height));
        Assert.NotSame(copy.Source, copy.Destination);
        Assert.Equal(copy.Source.Format, copy.Destination.Format);
    }

    [Fact]
    public void SameSizeFormatAndSamplesRebind()
    {
        using var device = new FakeGpuDevice(maxMsaaSampleCount: 4);
        var factory = (FakeGpuResourceFactory)device.Factory;
        using var retired = GpuRetireQueue.CreateFrameCounted(device, 3);
        using var targets = new RenderResources(device, 96, 64, false, retired: retired);
        using var source = new WaterColumnColorSource(device, retired);
        using var commands = new RecordingGpuCommandList(new NullGpuCommandList());
        source.Prepare(targets, true);
        var first = Assert.IsType<FakeTexture>(source.Texture);
        int generation = source.Generation;
        int textures = factory.Textures.Count;
        source.Prepare(targets, true);
        Assert.Same(first, source.Texture);
        Assert.Equal(generation, source.Generation);
        Assert.Equal(textures, factory.Textures.Count);

        targets.Resize(96, 64, false, 1, false, true);
        source.Prepare(targets, true);
        var hdr = Assert.IsType<FakeTexture>(source.Texture);
        Assert.NotSame(first, hdr);
        Assert.Equal(GpuPixelFormat.R16G16B16A16Float, hdr.Format);
        Assert.True(source.Generation > generation);
        Assert.False(first.Disposed);
        generation = source.Generation;
        targets.Resize(96, 64, false, 4, false, true);
        textures = factory.Textures.Count;
        source.Prepare(targets, true);
        Assert.Same(targets.ColorTex, source.Texture);
        Assert.Equal(textures, factory.Textures.Count);
        Assert.True(source.Generation > generation);
        Assert.False(hdr.Disposed);
        Assert.Equal(WaterColumnCopyPath.MsaaResolve, source.Record(commands, targets, true));
        Assert.Same(targets.MsColor, Assert.Single(commands.Resolves).Source);
        Assert.Empty(commands.TextureCopies);
        retired.Dispose();
        Assert.True(first.Disposed);
        Assert.True(hdr.Disposed);
        Assert.False(Assert.IsType<FakeTexture>(targets.ColorTex).Disposed);
    }

    [Fact]
    public void EnabledDisabledEmptyFramesResetDiagnostics()
    {
        using var h = new Host();
        h.Scene.Post.Water.WaterColumnLight = true;
        h.Scene.Begin();
        h.Scene.DrawWater(new WaterPlane(0f, 0f, 0f, 2f));
        h.Scene.DrawWater(new WaterPlane(1024f, 0f, 0f, 2f));
        h.Scene.DrawWater(new WaterPlane(0f, 1f, 0f, 2f, look: new WaterLook { WaterColumnLight = false }));
        h.Render();

        Assert.Equal(WaterRenderer.PlaneRoute.Culled, h.Scene.WaterRouteForTests(1));
        Assert.Equal(new WaterColumnDiagnostics(2, true, WaterColumnCopyPath.SingleSampleCopy), h.Scene.WaterColumnLightDiagnostics);
        var colorSource = h.Source;
        var commands = h.Commands;
        Assert.Single(commands.TextureCopies, c => ReferenceEquals(c.Destination, colorSource.Texture));
        WaterColumnDiagnostics last = h.Scene.WaterColumnLightDiagnostics;
        h.Scene.Begin();
        Assert.Equal(last, h.Scene.WaterColumnLightDiagnostics);
        h.Scene.PrepareFrame(96, 64);
        Assert.Equal(last, h.Scene.WaterColumnLightDiagnostics);
        h.Render();
        WaterColumnDiagnostics emptyFrameDiagnostics = h.Scene.WaterColumnLightDiagnostics;
        Assert.Equal(default(WaterColumnDiagnostics), emptyFrameDiagnostics);
        Assert.Empty(h.Commands.TextureCopies);
        Assert.Equal((1u, 1u), (h.Source.Texture.Width, h.Source.Texture.Height));

        h.Queue(false);
        int textures = h.Factory.Textures.Count;
        h.Render();
        Assert.Equal(default, h.Scene.WaterColumnLightDiagnostics);
        Assert.Equal(textures, h.Factory.Textures.Count);
        Assert.Empty(h.Commands.TextureCopies);
        Assert.DoesNotContain(h.Commands.Resolves, r => ReferenceEquals(r.Source, h.Targets.MsColor));
    }

    [Fact]
    public void OffSourceKeepsOnePlaceholderWithoutAllocationsOrTransfers()
    {
        using var device = new FakeGpuDevice(maxMsaaSampleCount: 4);
        using var retired = GpuRetireQueue.CreateFrameCounted(device, 3);
        using var targets = new RenderResources(device, 96, 64, false, retired: retired);
        var factory = (FakeGpuResourceFactory)device.Factory;
        int textures = factory.Textures.Count;
        using var source = new WaterColumnColorSource(device, retired);
        IGpuTexture placeholder = source.Texture;
        Assert.Equal(textures + 1, factory.Textures.Count);
        Assert.Equal((1u, 1u), (placeholder.Width, placeholder.Height));
        using var commands = new RecordingGpuCommandList(new NullGpuCommandList());
        source.Prepare(targets, false);
        Assert.Equal(WaterColumnCopyPath.None, source.Record(commands, targets, false));
        targets.Resize(96, 64, false, 4, false, true);
        textures = factory.Textures.Count;
        source.Prepare(targets, false);
        Assert.Equal(WaterColumnCopyPath.None, source.Record(commands, targets, false));
        Assert.Equal(textures, factory.Textures.Count);
        Assert.Same(placeholder, source.Texture);
        Assert.Empty(commands.Resolves);
        Assert.Empty(commands.TextureCopies);
    }

    [Theory]
    [InlineData(false, false)]
    [InlineData(false, true)]
    [InlineData(true, false)]
    [InlineData(true, true)]
    public void UnboundPreparationRebindsLateFormatAndSamples(bool viewportPreparation, bool msaa)
    {
        using var h = new Host();
        h.Queue();
        h.Render(viewportPreparation);
        IGpuTexture first = h.Source.Texture;
        h.Queue();
        h.Render(viewportPreparation, () =>
        {
            h.Scene.Post.Hdr.Enabled = !h.Scene.Post.Hdr.Enabled;
            if (msaa) h.Scene.Post.Quality.AntiAliasing = AntiAliasing.Msaa(4);
        });
        Assert.NotSame(first, h.Source.Texture);
        Assert.Equal(h.Targets.ColorTex.Format, h.Source.Texture.Format);
        Assert.Equal(msaa ? WaterColumnCopyPath.MsaaResolve : WaterColumnCopyPath.SingleSampleCopy,
            h.Scene.WaterColumnLightDiagnostics.CopyPath);
        Assert.False(Assert.IsType<FakeTexture>(first).Disposed);
    }

    [Theory]
    [InlineData(false)]
    [InlineData(true)]
    public void LateEnabledValuesAreValidatedEvenWhenCulled(bool changeLook)
    {
        using var h = new Host();
        var look = new WaterLook { WaterColumnLight = true };
        h.Scene.Begin();
        h.Scene.DrawWater(new WaterPlane(1024f, 0f, 0f, 2f, look: look));
        Assert.Throws<ArgumentException>(() => h.Render(afterPreparation: () =>
        {
            if (changeLook) look.Clarity = float.NaN;
            else h.Scene.Post.Water.Clarity = float.NaN;
        }));
        Assert.Empty(h.Commands.TextureCopies);
    }

    sealed class Timeline(IGpuCommandList inner) : IGpuCommandList
    {
        internal readonly record struct Entry(string Kind, IGpuTexture? Source = null, IGpuTexture? Destination = null);
        internal readonly List<Entry> Entries = new();
        FakePipeline? _pipeline;
        void NoteDraw()
        {
            string? fragment = _pipeline?.Request?.FragmentGlsl;
            Entries.Add(new(fragment == ShaderSources.DecalFrag ? "decal"
                : fragment == ShaderSources.WaterFrag || fragment == ShaderSources.WaterTriangleFrag ? "water" : "draw"));
        }
        public void Begin() => inner.Begin();
        public void End() => inner.End();
        public void SetFramebuffer(IGpuFramebuffer fb) => inner.SetFramebuffer(fb);
        public void ClearColorTarget(uint index, Color rgba) => inner.ClearColorTarget(index, rgba);
        public void ClearDepthStencil(float depth) => inner.ClearDepthStencil(depth);
        public void SetPipeline(IGpuPipeline p) { _pipeline = p as FakePipeline; inner.SetPipeline(p); }
        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set) => inner.SetGraphicsResourceSet(slot, set);
        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset) => inner.SetGraphicsResourceSet(slot, set, dynamicOffset);
        public void SetVertexBuffer(uint slot, IGpuBuffer b) => inner.SetVertexBuffer(slot, b);
        public void SetVertexBuffer(uint slot, IGpuBuffer b, uint offsetBytes) => inner.SetVertexBuffer(slot, b, offsetBytes);
        public void SetIndexBuffer(IGpuBuffer b, GpuIndexFormat fmt) => inner.SetIndexBuffer(b, fmt);
        public void SetScissorRect(uint index, uint x, uint y, uint w, uint h) => inner.SetScissorRect(index, x, y, w, h);
        public void SetFullScissorRects() => inner.SetFullScissorRects();
        public void Draw(uint vertexCount, uint instanceCount, uint vertexStart, uint instanceStart)
        { NoteDraw(); inner.Draw(vertexCount, instanceCount, vertexStart, instanceStart); }
        public void Draw(uint vertexCount) { NoteDraw(); inner.Draw(vertexCount); }
        public void DrawIndexed(uint indexCount, uint instanceCount, uint indexStart, int vertexOffset, uint instanceStart)
        { NoteDraw(); inner.DrawIndexed(indexCount, instanceCount, indexStart, vertexOffset, instanceStart); }
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged => inner.UpdateBuffer(b, offsetBytes, in data);
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged => inner.UpdateBuffer(b, offsetBytes, data);
        public void CopyBuffer(IGpuBuffer src, uint srcOffsetBytes, IGpuBuffer dst, uint dstOffsetBytes, uint sizeInBytes) => inner.CopyBuffer(src, srcOffsetBytes, dst, dstOffsetBytes, sizeInBytes);
        public void CopyTexture(IGpuTexture src, IGpuTexture dst)
        { Entries.Add(new("copy", src, dst)); inner.CopyTexture(src, dst); }
        public void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer, IGpuTexture dst, uint width, uint height)
        { Entries.Add(new("copy", src, dst)); inner.CopyTextureSubresource(src, srcMipLevel, srcArrayLayer, dst, width, height); }
        public void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer, IGpuTexture dst,
            uint dstMipLevel, uint dstArrayLayer, uint width, uint height)
        { Entries.Add(new("copy", src, dst)); inner.CopyTextureSubresource(src, srcMipLevel, srcArrayLayer, dst, dstMipLevel, dstArrayLayer, width, height); }
        public void GenerateMipmaps(IGpuTexture texture) => inner.GenerateMipmaps(texture);
        public void ResolveTexture(IGpuTexture src, IGpuTexture dst)
        { Entries.Add(new("resolve", src, dst)); inner.ResolveTexture(src, dst); }
        public void ResolveTexture(IGpuTexture src, IGpuTexture dst, bool preserveSource)
        { Entries.Add(new("resolve", src, dst)); inner.ResolveTexture(src, dst, preserveSource); }
        public void SetComputePipeline(IGpuComputePipeline p) => inner.SetComputePipeline(p);
        public void SetComputeResourceSet(uint slot, IGpuResourceSet set) => inner.SetComputeResourceSet(slot, set);
        public void SetComputeResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset) => inner.SetComputeResourceSet(slot, set, dynamicOffset);
        public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ) => inner.Dispatch(groupCountX, groupCountY, groupCountZ);
        public void Dispose() { }
    }
}
