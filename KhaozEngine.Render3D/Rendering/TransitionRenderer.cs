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
    /// transition is active (or its cover is 0), so a frame with no transition is byte-identical to before this pass
    /// existed. Mirrors the fullscreen-pass plumbing of <see cref="PixelPostProcess"/> (shared
    /// <see cref="ShaderSources.FullscreenVert"/>, no vertex buffer, <c>Draw(3)</c>).
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

        // The captured pre-teleport frame (a copy of the resolved ColorTex), sized to the render target and rebuilt on
        // resize. Only allocated/used by the crossfade style.
        IGpuTexture? _frozen;
        IGpuResourceSet _solidSet = null!;
        IGpuResourceSet? _crossSet;
        int _w, _h;

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
            if (_frozen != null && _w == res.Width && _h == res.Height) return;
            _crossSet?.Dispose();
            _frozen?.Dispose();
            _w = res.Width;
            _h = res.Height;
            _frozen = _gd.Factory.CreateTexture(new GpuTextureDescription((uint)_w, (uint)_h,
                GpuPixelFormat.R8G8B8A8UNorm, GpuTextureUsage.Sampled | GpuTextureUsage.RenderTarget, 1, 1, 1));
            _crossSet = _gd.Factory.CreateResourceSet(
                new GpuResourceSetDescription(_crossLayout, _frozen, _gd.LinearSampler, _crossBuf));
        }

        /// <summary>Draw the active screen transition over <paramref name="target"/> (the final post image). No-op when
        /// <paramref name="t"/> is null, not active, or fully revealed. Uploads its uniform (and, for a crossfade,
        /// captures the frozen frame) BEFORE binding <paramref name="target"/>, mirroring the overlay renderers'
        /// between-pass upload.</summary>
        public void Render(IGpuCommandList cl, RenderResources res, IGpuFramebuffer target, IScreenTransition? t)
        {
            if (t is null || !t.IsActive) return;
            float cover = t.Cover;
            if (cover <= 0f) return;

            if (t.Style == ScreenTransitionStyle.FrozenCrossfade)
            {
                // Capture the pre-teleport frame during the cover phase (before the swap warps the camera). Cover is
                // instant for CameraDissolve, so this snapshots the origin view; the frozen frame then holds it through
                // the hold + reveal. Capturing the resolved ColorTex (pre-post) is a hair different from the final
                // post image, imperceptible across a sub-second crossfade.
                if (t.Phase == TransitionPhase.Cover) cl.CopyTexture(res.ColorTex, _frozen!);
                var ubo = new CrossfadeUbo { Params = new Vector4(cover, 0f, 0f, 0f) };
                cl.UpdateBuffer(_crossBuf, 0, in ubo);
                cl.SetFramebuffer(target);
                cl.SetPipeline(_crossPipe);
                cl.SetGraphicsResourceSet(0, _crossSet!);
                cl.Draw(3);
            }
            else
            {
                Color c = t.Color;
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
