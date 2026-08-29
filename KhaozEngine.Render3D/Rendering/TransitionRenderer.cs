using System;
using System.Collections.Generic;
using System.Numerics;
using KhaozEngine.Gpu;
using KhaozEngine.Primitives;
using KhaozEngine.Render3D.Internal;

namespace KhaozEngine.Render3D.Rendering
{
    /// <summary>
    /// Draws the active screen-space teleport transition (<see cref="IScreenTransition"/>) as a fullscreen pass OVER
    /// the final post image, with standard src-alpha blend. Two styles: a solid fill (<see cref="HardBlink"/>) and a
    /// crossfade from a captured frozen frame to the live view (<see cref="CameraDissolve"/>). Fully skipped when no
    /// transition is active (Cover 0 = fully revealed), so a frame with no transition is byte-identical to before this
    /// pass existed. Mirrors the fullscreen-pass plumbing of <see cref="PixelPostProcess"/> (shared
    /// <see cref="ShaderSources.FullscreenVert"/>, no vertex buffer, <c>Draw(3)</c>).
    ///
    /// <para>The frozen frame for a crossfade is captured from the PREVIOUS frame's resolved colour, not the current
    /// one: a teleport is a hard cut, so by the time a crossfade begins the avatar (and camera) have already cut to the
    /// destination and the current frame shows the post-teleport view. <see cref="BeginFrame"/> snapshots the
    /// still-resident previous-frame <c>ColorTex</c> at the top of the frame (before the model pass overwrites it) on
    /// the frame a crossfade first goes active. If no valid previous frame exists yet (the transition began the very
    /// first frame after a resize, when <c>ColorTex</c> is blank), the crossfade degrades to a plain solid fade rather
    /// than sampling a blank image.</para>
    /// </summary>
    internal sealed class TransitionRenderer : IDisposable
    {
        struct SolidUbo { public Vector4 ColorAlpha; }   // rgb = fill colour, a = opacity
        struct CrossfadeUbo { public Vector4 Params; }   // .x = frozen-frame opacity (Cover)

        readonly IGpuDevice _gd;
        readonly IGpuShaderSet _solidShaders, _crossShaders;
        readonly IGpuResourceLayout _solidLayout, _crossLayout;
        readonly IGpuPipeline _solidPipe, _crossPipe;
        readonly IGpuBuffer _solidBuf, _crossBuf;

        // The captured pre-teleport frame (a copy of the resolved ColorTex's base level), sized to the render target
        // and rebuilt on resize. Its colour format follows ColorTex (float16 in HDR mode, UNorm in legacy) so the
        // BeginFrame copy stays format-matched. Single-mip on purpose: the frozen frame is only ever sampled 1:1, so
        // it carries no chain even when ColorTex does, and BeginFrame copies mip 0 rather than the whole resource.
        // Only allocated/used by the crossfade style.
        IGpuTexture? _frozen;
        IGpuResourceSet _solidSet = null!;
        IGpuResourceSet? _crossSet;
        int _w, _h;
        GpuPixelFormat _frozenFmt;

        // FIX-1 frozen-frame lifecycle. _frozenValid: _frozen holds a real previous-frame image safe to sample (false
        // until the first successful capture, and after any resize). _prevFrozenActive: a crossfade was frozen-style
        // active LAST BeginFrame, so a false->true edge is "the crossfade just began" (capture once, then). _haveResolvedFrame:
        // at least one full frame has resolved into ColorTex at the current size since the last (re)allocation, so
        // ColorTex at the top of THIS frame is a real previous frame and not a blank post-resize target.
        bool _frozenValid;
        bool _prevFrozenActive;
        bool _haveResolvedFrame;

        public TransitionRenderer(IGpuDevice gd, GpuOutputDescription targetOutput)
        {
            _gd = gd;
            var f = gd.Factory;

            _solidBuf = f.CreateBuffer(new GpuBufferDescription(16, GpuBufferUsage.UniformBuffer));   // 1 vec4
            _crossBuf = f.CreateBuffer(new GpuBufferDescription(16, GpuBufferUsage.UniformBuffer));   // 1 vec4

            _solidShaders = f.CreateShadersFromSpirv(ShaderSources.FullscreenVert, ShaderSources.TransitionSolidFrag);
            _crossShaders = f.CreateShadersFromSpirv(ShaderSources.FullscreenVert, ShaderSources.TransitionCrossfadeFrag);

            _solidLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("Fill", GpuResourceKind.UniformBuffer, GpuShaderStages.Fragment)));
            _crossLayout = f.CreateResourceLayout(new GpuResourceLayoutDescription(
                new GpuResourceLayoutElement("Src", GpuResourceKind.TextureReadOnly, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("Samp", GpuResourceKind.Sampler, GpuShaderStages.Fragment),
                new GpuResourceLayoutElement("Params", GpuResourceKind.UniformBuffer, GpuShaderStages.Fragment)));

            _solidPipe = Pipeline(f, _solidShaders, _solidLayout, targetOutput);
            _crossPipe = Pipeline(f, _crossShaders, _crossLayout, targetOutput);

            _solidSet = f.CreateResourceSet(new GpuResourceSetDescription(_solidLayout, _solidBuf));
        }

        static IGpuPipeline Pipeline(IGpuResourceFactory f, IGpuShaderSet shaders, IGpuResourceLayout layout,
            GpuOutputDescription outputs) =>
            f.CreateGraphicsPipeline(new GpuPipelineDescription
            {
                BlendFactor = Vector4.Zero,
                BlendAttachments = new[] { GpuBlendAttachment.AlphaBlend },   // over the final image
                DepthStencil = GpuDepthStencilState.Disabled,
                Rasterizer = new GpuRasterizerState(GpuFaceCull.None, GpuPolygonFill.Solid, GpuFrontFace.Clockwise,
                    depthClipEnabled: false, scissorTestEnabled: false),
                Topology = GpuPrimitiveTopology.TriangleList,
                ResourceLayouts = new[] { layout },
                ShaderSet = shaders,
                VertexLayouts = new List<GpuVertexLayoutDescription>(),   // fullscreen triangle from gl_VertexIndex
                Outputs = outputs,
            });

        /// <summary>(Re)create the frozen capture texture + crossfade resource set to match the render target size.
        /// Call on construction and whenever the targets resize (alongside <c>PixelPostProcess.BindTargets</c>).</summary>
        public void BindTargets(RenderResources res)
        {
            // Match the frozen capture's colour format to ColorTex so the BeginFrame subresource copy is
            // format-matched (float16 in HDR mode, UNorm in legacy). The crossfade pipeline samples _frozen and
            // outputs to the swapchain, so a float16 source needs no pipeline rebuild here (only the OUTPUT format
            // is baked). The mip count deliberately does NOT follow ColorTex: see the _frozen field.
            var colorFmt = res.HdrColor ? GpuPixelFormat.R16G16B16A16Float : GpuPixelFormat.R8G8B8A8UNorm;
            if (_frozen != null && _w == res.Width && _h == res.Height && _frozenFmt == colorFmt) return;
            _crossSet?.Dispose();
            _frozen?.Dispose();
            _w = res.Width;
            _h = res.Height;
            _frozenFmt = colorFmt;
            _frozen = _gd.Factory.CreateTexture(new GpuTextureDescription((uint)_w, (uint)_h,
                colorFmt, GpuTextureUsage.Sampled | GpuTextureUsage.RenderTarget, 1, 1, 1));
            _crossSet = _gd.Factory.CreateResourceSet(
                new GpuResourceSetDescription(_crossLayout, _frozen, _gd.LinearSampler, _crossBuf));
            // The freshly (re)allocated _frozen is blank, and no frame has resolved into the new ColorTex yet: any
            // capture this frame would snapshot a blank target. Invalidate both so a crossfade beginning right now
            // falls back to a plain fade until the next frame has actually rendered.
            _frozenValid = false;
            _haveResolvedFrame = false;
        }

        /// <summary>Called once at the TOP of the frame (before the model pass overwrites <c>ColorTex</c>). On the
        /// frame a <see cref="ScreenTransitionStyle.FrozenCrossfade"/> transition FIRST goes active, snapshots the
        /// still-resident PREVIOUS-frame <c>ColorTex</c> as the frozen image - the origin view, before the teleport cut
        /// - so the crossfade blends FROM the origin, not from the already-cut destination. A no-op for a solid
        /// transition or none. If no previous frame is available yet (a crossfade beginning the first frame after a
        /// resize, when <c>ColorTex</c> is blank), leaves the frozen image invalid so <see cref="Render"/> degrades to
        /// a plain solid fade.</summary>
        public void BeginFrame(IGpuCommandList cl, RenderResources res, IScreenTransition? t)
        {
            bool frozenActive = t is { IsActive: true, Style: ScreenTransitionStyle.FrozenCrossfade };
            if (frozenActive && !_prevFrozenActive)   // rising edge: the crossfade just began this frame
            {
                if (_haveResolvedFrame)
                {
                    // MIP 0 ONLY, never the whole-resource CopyTexture. ColorTex carries a full mip chain whenever
                    // RenderResources.Mipped is on (Scene3D.WantsMipDownsample: supersampled MatchViewport, or an
                    // opted-in FixedInternal downscale), while _frozen is always single-mip. A whole copy names
                    // every subresource on BOTH sides, so the shape mismatch is refused: the native Metal and
                    // Vulkan backends throw ArgumentException from their RequireMatchingShape, and Direct3D 11's
                    // CopyResource wants two identical descriptions. Copying the base level is also all the pass
                    // needs, since the frozen frame is only ever sampled 1:1 by the fullscreen crossfade.
                    // The extent is the destination's own, which BindTargets sized to ColorTex's mip 0.
                    cl.CopyTextureSubresource(res.ColorTex, 0, 0, _frozen!, _frozen!.Width, _frozen.Height);
                    _frozenValid = true;
                }
                else
                {
                    _frozenValid = false;   // no real previous frame (post-resize / first frame): plain-fade fallback
                }
            }
            _prevFrozenActive = frozenActive;
        }

        /// <summary>Marks that a full frame has resolved into <c>ColorTex</c> at the current size. Call once per frame
        /// AFTER <c>ResolveColor</c>, so the NEXT frame's <see cref="BeginFrame"/> knows <c>ColorTex</c> holds a
        /// real previous frame (not a blank post-resize target).</summary>
        public void NoteFrameResolved() => _haveResolvedFrame = true;

        /// <summary>Drops any captured frozen-frame state so a later transition starts clean. Called by
        /// <c>Scene3D.ClearScreenTransition</c> when a consumer tears the overlay down mid-transition.</summary>
        public void Reset()
        {
            _frozenValid = false;
            _prevFrozenActive = false;
        }

        /// <summary>Draw the active screen transition over <paramref name="target"/> (the final post image). No-op when
        /// <paramref name="t"/> is null, not active, or fully revealed. The frozen frame for a crossfade is captured in
        /// <see cref="BeginFrame"/> (the previous frame), not here. Uploads its uniform BEFORE binding
        /// <paramref name="target"/>, mirroring the overlay renderers' between-pass upload.</summary>
        public void Render(IGpuCommandList cl, RenderResources res, IGpuFramebuffer target, IScreenTransition? t)
        {
            if (t is null || !t.IsActive) return;
            float cover = t.Cover;
            // Cover == 0 is FULLY REVEALED (the live view): nothing to draw, byte-identical to no pass. This is NOT the
            // "cover ramp at zero" case - an instant-cover (coverSeconds 0) transition reports Cover == 1 on its cut
            // frame (see Transition.Cover), so the opaque first frame is drawn, never skipped here.
            if (cover <= 0f) return;

            // A frozen crossfade with a VALID captured frame blends frozen (origin) -> live by the cover weight. Without
            // one (the crossfade began the first frame after a resize, ColorTex blank), degrade to a plain solid-black
            // fade rather than sampling a blank frozen image - still masks the cut, just without the origin crossfade.
            if (t.Style == ScreenTransitionStyle.FrozenCrossfade && _frozenValid)
            {
                var ubo = new CrossfadeUbo { Params = new Vector4(cover, 0f, 0f, 0f) };
                cl.UpdateBuffer(_crossBuf, 0, in ubo);
                cl.SetFramebuffer(target);
                cl.SetPipeline(_crossPipe);
                cl.SetGraphicsResourceSet(0, _crossSet!);
                cl.Draw(3);
            }
            else
            {
                // Solid fill: HardBlink (its Color), or the invalid-frozen crossfade fallback (opaque black).
                Color c = t.Style == ScreenTransitionStyle.FrozenCrossfade ? Color.Black : t.Color;
                var ubo = new SolidUbo { ColorAlpha = new Vector4(c.R, c.G, c.B, cover * c.A) };
                cl.UpdateBuffer(_solidBuf, 0, in ubo);
                cl.SetFramebuffer(target);
                cl.SetPipeline(_solidPipe);
                cl.SetGraphicsResourceSet(0, _solidSet);
                cl.Draw(3);
            }
        }

        public void Dispose()
        {
            _solidPipe.Dispose(); _crossPipe.Dispose();
            _solidSet.Dispose(); _crossSet?.Dispose();
            _solidLayout.Dispose(); _crossLayout.Dispose();
            _solidShaders.Dispose(); _crossShaders.Dispose();
            _solidBuf.Dispose(); _crossBuf.Dispose();
            _frozen?.Dispose();
        }
    }
}
