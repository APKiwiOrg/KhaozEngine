using System;
using System.Collections.Generic;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// The on-request temporal counts on the scene's frame path, headless on <see cref="HeadlessSceneRig"/>. A request
/// arms only a render that runs the temporal resolve, which then records the probe right after the resolve over the
/// very set it bound and copies the grid out. The next <see cref="Scene3D.PrepareFrame"/> reads it back. The fake
/// device refuses every map, so a frame that completes here read nothing back, and a map shows up as a throw.
/// </summary>
public sealed class TemporalCountProbeSceneTests
{
    const int W = 96, H = 64;

    static HeadlessSceneRig Rig(bool temporal)
    {
        var rig = new HeadlessSceneRig();
        if (temporal) rig.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        rig.Scene.Post.Temporal.Upscale = TemporalUpscale.Performance;
        return rig;
    }

    static bool Is(IGpuPipeline? p, string fragment) => (p as FakePipeline)?.Request?.FragmentGlsl == fragment;

    static bool IsProbe(IGpuPipeline? p) => Is(p, ShaderSources.TemporalProbeFrag);

    static bool IsGridCopy(ProbeCapture.Copy c) => c.Destination is FakeTexture
    {
        Width: TemporalCountProbe.GridWidth, Height: TemporalCountProbe.GridHeight, Usage: GpuTextureUsage.Staging,
    };

    [Fact]
    public void ARequestRecordsTheProbeOnTheResolvingRenderRightAfterTheResolveOverItsSet()
    {
        using HeadlessSceneRig rig = Rig(temporal: true);
        rig.Frame(W, H);
        var capture = new ProbeCapture(new NullGpuCommandList());

        rig.Scene.RequestTemporalCounts();
        rig.Frame(W, H, commands: capture);
        Assert.True(rig.Scene.ResolvedLastRenderForTests);
        TemporalResolveRenderer resolve = rig.Scene.TemporalResolveRendererForTests!;
        // The entry point the resolve recorded, either one: the resolve and the depth store, or the split's two
        // passes, then the probe, the last thing the resolve's run records, over the set the fused resolve binds,
        // which the renderer builds on either entry point for the counts.
        TemporalResolveEntry entry = resolve.LastEntry
            ?? throw new InvalidOperationException("the resolve recorded no entry point");
        string[] fragments = TemporalResolveRenderer.EntryFragments(entry);
        int resolveDraw = capture.Draws.FindIndex(d => Is(d.Pipeline, fragments[0]));
        ProbeCapture.Drawn probe = Assert.Single(capture.Draws, d => IsProbe(d.Pipeline));
        int probeDraw = capture.Draws.IndexOf(probe);
        Assert.True(resolveDraw >= 0 && probeDraw == resolveDraw + 2,
            $"the probe draw {probeDraw} follows the {entry} resolve's two draws from {resolveDraw}");
        Assert.True(Is(capture.Draws[resolveDraw + 1].Pipeline, fragments[1]));
        if (entry == TemporalResolveEntry.Fused) Assert.Same(capture.Draws[resolveDraw].Set0, probe.Set0);
        Assert.Same(resolve.CurrentSet, probe.Set0);
        FakeGraphicsPipelineRequest pipeline = Assert.Single(rig.Factory.GraphicsPipelines,
            p => p.FragmentGlsl == ShaderSources.TemporalProbeFrag);
        Assert.Same(resolve.ResolveLayout, Assert.Single(pipeline.Description.ResourceLayouts));
        // The grid goes to the staging copy straight after the probe draw, and nothing else is copied there.
        ProbeCapture.Copy copy = Assert.Single(capture.Copies, IsGridCopy);
        Assert.Equal(probeDraw + 1, copy.DrawsBefore);
        Assert.True(rig.Scene.TemporalCountProbeBuiltForTests);

        // A later render inside the frame is unresolved: it records no probe and copies no grid.
        capture.Clear();
        rig.Render(W, H, capture);
        Assert.False(rig.Scene.ResolvedLastRenderForTests);
        Assert.DoesNotContain(capture.Draws, d => IsProbe(d.Pipeline));
        Assert.DoesNotContain(capture.Copies, IsGridCopy);

        // The next frame's PrepareFrame maps the grid, which the fake device refuses: the harvest is there, before
        // the frame records anything.
        rig.Scene.Begin();
        Assert.Throws<NotSupportedException>(rig.Scene.PrepareFrame);
    }

    [Fact]
    public void ARequestBeforeFramesThatDoNotResolveWaitsForTheFirstResolvingRender()
    {
        using HeadlessSceneRig rig = Rig(temporal: false);
        var capture = new ProbeCapture(new NullGpuCommandList());
        rig.Scene.RequestTemporalCounts();
        for (int i = 0; i < 3; i++) rig.Frame(W, H, commands: capture);
        Assert.Null(rig.Scene.TemporalResolveRendererForTests);
        Assert.False(rig.Scene.TemporalCountProbeBuiltForTests);
        Assert.DoesNotContain(capture.Copies, IsGridCopy);

        // A temporal frame that does not resolve, through the MotionVectors view, arms nothing either.
        rig.Scene.DebugView = SceneDebugView.MotionVectors;
        rig.Frame(W, H, commands: capture);
        Assert.True(rig.Scene.TemporalActive);
        Assert.False(rig.Scene.ResolvedLastRenderForTests);
        Assert.False(rig.Scene.TemporalCountProbeBuiltForTests);

        rig.Scene.DebugView = SceneDebugView.None;
        rig.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        capture.Clear();
        rig.Frame(W, H, commands: capture);
        Assert.True(rig.Scene.ResolvedLastRenderForTests);
        Assert.Single(capture.Draws, d => IsProbe(d.Pipeline));
        Assert.Single(capture.Copies, IsGridCopy);
        rig.Scene.Begin();
        Assert.Throws<NotSupportedException>(rig.Scene.PrepareFrame);   // and the next PrepareFrame reads it back
    }

    [Fact]
    public void ARequestMadeAfterTheFramesFirstRenderWaitsForTheNextFrame()
    {
        using HeadlessSceneRig rig = Rig(temporal: true);
        var capture = new ProbeCapture(new NullGpuCommandList());
        rig.Frame(W, H);
        rig.Scene.RequestTemporalCounts();
        rig.Render(W, H, capture);   // a later render of the frame: not the one the resolve ran
        Assert.DoesNotContain(capture.Draws, d => IsProbe(d.Pipeline));
        Assert.False(rig.Scene.TemporalCountProbeBuiltForTests);

        // Nothing was armed, so the next PrepareFrame maps nothing, and the frame's resolving render arms it. The
        // PrepareFrame after that reads back the frame the probe ran on. Had the later render armed the request, the
        // first PrepareFrame would have spent it and this one would read nothing.
        capture.Clear();
        rig.Frame(W, H, commands: capture);
        Assert.Single(capture.Draws, d => IsProbe(d.Pipeline));
        Assert.Single(capture.Copies, IsGridCopy);
        rig.Scene.Begin();
        Assert.Throws<NotSupportedException>(rig.Scene.PrepareFrame);
    }

    [Fact]
    public void RequestingTwiceBeforeAFrameArmsItOnce()
    {
        using HeadlessSceneRig rig = Rig(temporal: true);
        var capture = new ProbeCapture(new NullGpuCommandList());
        rig.Frame(W, H);
        rig.Scene.RequestTemporalCounts();
        rig.Scene.RequestTemporalCounts();
        rig.Frame(W, H, commands: capture);
        Assert.Single(capture.Draws, d => IsProbe(d.Pipeline));
    }

    [Fact]
    public void WithoutARequestNothingIsBuiltRecordedOrReadBack()
    {
        using HeadlessSceneRig rig = Rig(temporal: true);
        var capture = new ProbeCapture(new NullGpuCommandList());
        for (int i = 0; i < 4; i++)
        {
            rig.Frame(W, H, commands: capture);   // PrepareFrame would throw on a map
            rig.Render(W, H, capture);
        }
        Assert.True(rig.Scene.TemporalResolveRendererForTests is not null);
        Assert.False(rig.Scene.TemporalCountProbeBuiltForTests);
        Assert.Equal(0, rig.Scene.TemporalCountReadbacksForTests);
        Assert.DoesNotContain(rig.Factory.GraphicsPipelines, p => p.FragmentGlsl == ShaderSources.TemporalProbeFrag);
        Assert.DoesNotContain(rig.Factory.Textures, t => t.Usage == GpuTextureUsage.Staging);
        Assert.DoesNotContain(capture.Draws, d => IsProbe(d.Pipeline));
        Assert.DoesNotContain(capture.Copies, IsGridCopy);
        Assert.Equal(-1, rig.Scene.LastTemporalDiagnostics.CountsFrameIndex);
    }

    /// <summary>Records each non-indexed draw with its pipeline, framebuffer and set 0, and each whole-texture copy
    /// with the number of draws before it, and forwards everything.</summary>
    sealed class ProbeCapture(IGpuCommandList inner) : IGpuCommandList
    {
        internal readonly record struct Drawn(IGpuPipeline? Pipeline, IGpuFramebuffer? Framebuffer,
            IGpuResourceSet? Set0);

        internal readonly record struct Copy(IGpuTexture Source, IGpuTexture Destination, int DrawsBefore);

        IGpuPipeline? _pipeline;
        IGpuFramebuffer? _framebuffer;
        IGpuResourceSet? _set0;

        internal List<Drawn> Draws { get; } = new();
        internal List<Copy> Copies { get; } = new();

        internal void Clear()
        {
            Draws.Clear();
            Copies.Clear();
        }

        void Note() => Draws.Add(new Drawn(_pipeline, _framebuffer, _set0));

        void NoteSet(uint slot, IGpuResourceSet set)
        {
            if (slot == 0) _set0 = set;
        }

        public void Begin() { _set0 = null; inner.Begin(); }
        public void End() => inner.End();
        public void SetFramebuffer(IGpuFramebuffer fb) { _framebuffer = fb; inner.SetFramebuffer(fb); }
        public void ClearColorTarget(uint index, Color rgba) => inner.ClearColorTarget(index, rgba);
        public void ClearDepthStencil(float depth) => inner.ClearDepthStencil(depth);
        public void SetPipeline(IGpuPipeline p) { _pipeline = p; inner.SetPipeline(p); }
        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set)
        {
            NoteSet(slot, set);
            inner.SetGraphicsResourceSet(slot, set);
        }
        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset)
        {
            NoteSet(slot, set);
            inner.SetGraphicsResourceSet(slot, set, dynamicOffset);
        }
        public void SetVertexBuffer(uint slot, IGpuBuffer b) => inner.SetVertexBuffer(slot, b);
        public void SetVertexBuffer(uint slot, IGpuBuffer b, uint offsetBytes) =>
            inner.SetVertexBuffer(slot, b, offsetBytes);
        public void SetIndexBuffer(IGpuBuffer b, GpuIndexFormat fmt) => inner.SetIndexBuffer(b, fmt);
        public void SetScissorRect(uint index, uint x, uint y, uint w, uint h) =>
            inner.SetScissorRect(index, x, y, w, h);
        public void SetFullScissorRects() => inner.SetFullScissorRects();
        public void Draw(uint vertexCount, uint instanceCount, uint vertexStart, uint instanceStart)
        {
            Note();
            inner.Draw(vertexCount, instanceCount, vertexStart, instanceStart);
        }
        public void Draw(uint vertexCount) { Note(); inner.Draw(vertexCount); }
        public void DrawIndexed(uint indexCount, uint instanceCount, uint indexStart, int vertexOffset,
            uint instanceStart) =>
            inner.DrawIndexed(indexCount, instanceCount, indexStart, vertexOffset, instanceStart);
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, in T data) where T : unmanaged =>
            inner.UpdateBuffer(b, offsetBytes, in data);
        public void UpdateBuffer<T>(IGpuBuffer b, uint offsetBytes, ReadOnlySpan<T> data) where T : unmanaged =>
            inner.UpdateBuffer(b, offsetBytes, data);
        public void CopyBuffer(IGpuBuffer src, uint srcOffsetBytes, IGpuBuffer dst, uint dstOffsetBytes,
            uint sizeInBytes) => inner.CopyBuffer(src, srcOffsetBytes, dst, dstOffsetBytes, sizeInBytes);
        public void CopyTexture(IGpuTexture src, IGpuTexture dst)
        {
            Copies.Add(new Copy(src, dst, Draws.Count));
            inner.CopyTexture(src, dst);
        }
        public void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer,
            IGpuTexture dst, uint width, uint height) =>
            inner.CopyTextureSubresource(src, srcMipLevel, srcArrayLayer, dst, width, height);
        public void CopyTextureSubresource(IGpuTexture src, uint srcMipLevel, uint srcArrayLayer, IGpuTexture dst,
            uint dstMipLevel, uint dstArrayLayer, uint width, uint height) =>
            inner.CopyTextureSubresource(src, srcMipLevel, srcArrayLayer, dst, dstMipLevel, dstArrayLayer,
                width, height);
        public void GenerateMipmaps(IGpuTexture texture) => inner.GenerateMipmaps(texture);
        public void ResolveTexture(IGpuTexture src, IGpuTexture dst) => inner.ResolveTexture(src, dst);
        public void SetComputePipeline(IGpuComputePipeline p) => inner.SetComputePipeline(p);
        public void SetComputeResourceSet(uint slot, IGpuResourceSet set) => inner.SetComputeResourceSet(slot, set);
        public void SetComputeResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset) =>
            inner.SetComputeResourceSet(slot, set, dynamicOffset);
        public void Dispatch(uint groupCountX, uint groupCountY, uint groupCountZ) =>
            inner.Dispatch(groupCountX, groupCountY, groupCountZ);
        public void Dispose() { }
    }
}
