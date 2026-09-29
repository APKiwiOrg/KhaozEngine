using System;
using System.Collections.Generic;
using System.Linq;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D;
using KhaozEngine.Render3D.Internal;
using KhaozEngine.Render3D.Rendering;
using KhaozEngine.Tests.Gpu;
using Xunit;

namespace KhaozEngine.Tests.Render3D;

/// <summary>
/// The History, Disocclusion and Reactive debug views on the scene's frame path, headless on
/// <see cref="HeadlessSceneRig"/>. A view is drawn only on the render that ran the temporal resolve, over the very set
/// the resolve bound, after the post chain. A later render of the frame draws none, and a frame without temporal
/// anti-aliasing builds nothing. The resolve, the sharpen and the material mip bias run as they do without a view.
/// </summary>
public sealed class TemporalDebugViewSceneTests
{
    const int W = 96, H = 64;

    public static TheoryData<SceneDebugView> ResolveViews => new()
    {
        SceneDebugView.History, SceneDebugView.Disocclusion, SceneDebugView.Reactive,
    };

    static HeadlessSceneRig Rig(SceneDebugView view, bool temporal)
    {
        var rig = new HeadlessSceneRig();
        if (temporal) rig.Scene.Post.Quality.AntiAliasing = AntiAliasing.Temporal;
        rig.Scene.Post.Temporal.Upscale = TemporalUpscale.Performance;
        rig.Scene.DebugView = view;
        return rig;
    }

    static bool IsDebugView(IGpuPipeline? p)
        => (p as FakePipeline)?.Request?.FragmentGlsl == ShaderSources.TemporalDebugFrag;

    static bool Is(IGpuPipeline? p, string fragment) => (p as FakePipeline)?.Request?.FragmentGlsl == fragment;

    // A draw of either entry point of the resolve (TemporalResolveEntry), whichever the policy or its override picks.
    static bool IsResolve(IGpuPipeline? p)
        => Enum.GetValues<TemporalResolveEntry>().Any(e => TemporalResolveRenderer.EntryFragments(e)
            .Any(fragment => Is(p, fragment)));

    [Theory]
    [MemberData(nameof(ResolveViews))]
    public void OutsideTemporalAntiAliasingAViewBuildsAndDrawsNothing(SceneDebugView view)
    {
        using HeadlessSceneRig rig = Rig(view, temporal: false);
        var capture = new DrawCapture(new NullGpuCommandList());
        for (int i = 0; i < 3; i++) rig.Frame(W, H, commands: capture);

        Assert.False(rig.Scene.TemporalActive);
        Assert.False(rig.Scene.TemporalDebugViewBuiltForTests);
        Assert.Null(rig.Scene.TemporalResolveRendererForTests);
        Assert.DoesNotContain(rig.Factory.GraphicsPipelines, p => p.FragmentGlsl == ShaderSources.TemporalDebugFrag);
        Assert.DoesNotContain(capture.Draws, d => IsDebugView(d.Pipeline));
    }

    [Theory]
    [MemberData(nameof(ResolveViews))]
    public void OnlyTheRenderThatRanTheResolveDrawsTheViewOverTheSetItBound(SceneDebugView view)
    {
        using HeadlessSceneRig rig = Rig(view, temporal: true);
        rig.Frame(W, H);
        var capture = new DrawCapture(new NullGpuCommandList());

        rig.Frame(W, H, commands: capture);
        Assert.True(rig.Scene.ResolvedLastRenderForTests);
        TemporalResolveRenderer resolve = rig.Scene.TemporalResolveRendererForTests!;
        // The entry point the resolve recorded, either one: its two draws in order, then the view after both, over
        // the set the fused resolve binds, which the renderer builds on either entry point for the views.
        TemporalResolveEntry entry = resolve.LastEntry
            ?? throw new InvalidOperationException("the resolve recorded no entry point");
        string[] fragments = TemporalResolveRenderer.EntryFragments(entry);
        int resolveDraw = capture.Draws.FindIndex(d => Is(d.Pipeline, fragments[0]));
        DrawCapture.Drawn drawn = Assert.Single(capture.Draws, d => IsDebugView(d.Pipeline));
        int viewDraw = capture.Draws.IndexOf(drawn);
        Assert.True(resolveDraw >= 0 && viewDraw > resolveDraw + 1,
            $"the view draw {viewDraw} follows the {entry} resolve's draws from {resolveDraw}");
        Assert.True(Is(capture.Draws[resolveDraw + 1].Pipeline, fragments[1]));
        if (entry == TemporalResolveEntry.Fused) Assert.Same(capture.Draws[resolveDraw].Set0, drawn.Set0);
        Assert.Same(resolve.CurrentSet, drawn.Set0);
        Assert.NotNull(drawn.Set1);
        // Into the target the post chain ended on, the draw right before it.
        Assert.Same(capture.Draws[viewDraw - 1].Framebuffer, drawn.Framebuffer);
        FakeGraphicsPipelineRequest pipeline = Assert.Single(rig.Factory.GraphicsPipelines,
            p => p.FragmentGlsl == ShaderSources.TemporalDebugFrag);
        Assert.Same(resolve.ResolveLayout, pipeline.Description.ResourceLayouts[0]);
        Assert.Equal(2, pipeline.Description.ResourceLayouts.Length);
        Assert.True(rig.Scene.TemporalDebugViewBuiltForTests);

        // A later render inside the frame, such as a capture, is unjittered and unresolved: it neither draws the view
        // nor binds the resolve's set.
        capture.Clear();
        rig.Render(W, H, capture);
        Assert.False(rig.Scene.ResolvedLastRenderForTests);
        Assert.DoesNotContain(capture.Draws, d => IsDebugView(d.Pipeline) || IsResolve(d.Pipeline));
        Assert.DoesNotContain(capture.Draws, d => ReferenceEquals(d.Set0, resolve.CurrentSet));

        // The next frame resolves and draws it again, once, with the pipeline already built.
        capture.Clear();
        rig.Frame(W, H, commands: capture);
        Assert.Single(capture.Draws, d => IsDebugView(d.Pipeline));
        Assert.Single(rig.Factory.GraphicsPipelines, p => p.FragmentGlsl == ShaderSources.TemporalDebugFrag);
    }

    [Fact]
    public void NoViewBuildsNothingUnderTemporalAntiAliasing()
    {
        using HeadlessSceneRig rig = Rig(SceneDebugView.None, temporal: true);
        for (int i = 0; i < 3; i++) rig.Frame(W, H);
        Assert.True(rig.Scene.ResolvedLastRenderForTests);
        Assert.False(rig.Scene.TemporalDebugViewBuiltForTests);
        Assert.DoesNotContain(rig.Factory.GraphicsPipelines, p => p.FragmentGlsl == ShaderSources.TemporalDebugFrag);
    }

    /// <summary>A view neither turns the temporal frame on nor touches what runs before it: with it and without it the
    /// frame block, the material mip bias in its Params lanes included, is the same to the byte, and the sharpen is
    /// built for the display chain alone under temporal anti-aliasing and not at all without it.</summary>
    [Theory]
    [InlineData(SceneDebugView.History, true)]
    [InlineData(SceneDebugView.Disocclusion, true)]
    [InlineData(SceneDebugView.Reactive, true)]
    [InlineData(SceneDebugView.History, false)]
    [InlineData(SceneDebugView.Disocclusion, false)]
    [InlineData(SceneDebugView.Reactive, false)]
    public void AViewLeavesTheFrameBlockTheMipBiasAndTheSharpenAsTheyAreWithoutIt(SceneDebugView view, bool temporal)
    {
        using HeadlessSceneRig plain = Rig(SceneDebugView.None, temporal);
        using HeadlessSceneRig viewed = Rig(view, temporal);
        for (int i = 0; i < 3; i++)
        {
            plain.Frame(W, H);
            viewed.Frame(W, H);
            Assert.True(plain.Scene.FrameImageForTests.SequenceEqual(viewed.Scene.FrameImageForTests),
                $"frame {i}: the frame block differs with {view} selected");
        }

        Assert.Equal(temporal, viewed.Scene.TemporalActive);
        Assert.Equal(temporal, viewed.Scene.ResolvedLastRenderForTests);
        Assert.Equal(temporal, viewed.Scene.TemporalSharpenBuiltForTests);
        Assert.Equal(plain.Scene.TemporalSharpenBuiltForTests, viewed.Scene.TemporalSharpenBuiltForTests);
        float bias = BitConverter.ToSingle(viewed.Scene.FrameImageForTests.Slice(120, 4));
        Assert.Equal(temporal ? -1.5f : 0f, bias);

        viewed.Render(W, H);   // a later render of the frame
        plain.Render(W, H);
        Assert.True(plain.Scene.FrameImageForTests.SequenceEqual(viewed.Scene.FrameImageForTests),
            "the later render's frame block differs with the view selected");
        Assert.False(viewed.Scene.LaterRenderSharpenBuiltForTests);
    }

    [Fact]
    public void SwitchingFromTheMotionVectorsViewToAResolveViewRetiresTheMotionVectorsPass()
    {
        using HeadlessSceneRig rig = Rig(SceneDebugView.MotionVectors, temporal: true);
        rig.Frame(W, H);
        Assert.True(rig.Scene.MotionVectorsViewBuiltForTests);

        rig.Scene.DebugView = SceneDebugView.History;
        rig.Frame(W, H);
        Assert.False(rig.Scene.MotionVectorsViewBuiltForTests);
        Assert.True(rig.Scene.TemporalDebugViewBuiltForTests);
    }

    /// <summary>Records each non-indexed draw with the pipeline, framebuffer and the sets at slots 0 and 1 bound at
    /// that moment, and forwards everything.</summary>
    sealed class DrawCapture(IGpuCommandList inner) : IGpuCommandList
    {
        internal readonly record struct Drawn(IGpuPipeline? Pipeline, IGpuFramebuffer? Framebuffer,
            IGpuResourceSet? Set0, IGpuResourceSet? Set1);

        IGpuPipeline? _pipeline;
        IGpuFramebuffer? _framebuffer;
        readonly Dictionary<uint, IGpuResourceSet> _sets = new();

        internal List<Drawn> Draws { get; } = new();

        internal void Clear() => Draws.Clear();

        void Note() => Draws.Add(new Drawn(_pipeline, _framebuffer, _sets.GetValueOrDefault(0u),
            _sets.GetValueOrDefault(1u)));

        public void Begin() { _sets.Clear(); inner.Begin(); }
        public void End() => inner.End();
        public void SetFramebuffer(IGpuFramebuffer fb) { _framebuffer = fb; inner.SetFramebuffer(fb); }
        public void ClearColorTarget(uint index, Color rgba) => inner.ClearColorTarget(index, rgba);
        public void ClearDepthStencil(float depth) => inner.ClearDepthStencil(depth);
        public void SetPipeline(IGpuPipeline p) { _pipeline = p; inner.SetPipeline(p); }
        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set)
        {
            _sets[slot] = set;
            inner.SetGraphicsResourceSet(slot, set);
        }
        public void SetGraphicsResourceSet(uint slot, IGpuResourceSet set, uint dynamicOffset)
        {
            _sets[slot] = set;
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
        public void CopyTexture(IGpuTexture src, IGpuTexture dst) => inner.CopyTexture(src, dst);
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
